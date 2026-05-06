using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using LudeonTK;
using Multiplayer.Client.AsyncTime;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    public static class TickPatch
    {
        public static int Timer { get; private set; }

        public static int ticksToRun;
        public static int tickUntil; // Ticks < tickUntil can be simulated
        public static int workTicks;
        public static bool currentExecutingCmdIssuedBySelf;
        public static CommandType? currentExecutingCmdType;
        public static bool serverFrozen;
        public static int frozenAt;

        public const float StandardTimePerFrame = 1000.0f / 60.0f;

        // Time is in milliseconds
        private static float realTime;
        public static float avgFrameTime = StandardTimePerFrame;
        public static float serverTimePerTick;

        private static float frameTimeSentAt;

        public static TimeSpeed replayTimeSpeed;

        public static SimulatingData simulating;

        public static bool ShouldHandle => LongEventHandler.currentEvent == null && !Multiplayer.session.desynced;
        public static bool Simulating => simulating?.target != null;
        public static bool Frozen => serverFrozen && Timer >= frozenAt && !Simulating && ShouldHandle;

        public static IEnumerable<ITickable> AllTickables
        {
            get
            {
                yield return Multiplayer.AsyncWorldTime;

                var maps = Find.Maps;
                // Canonical iteration order: ascending by uniqueID. Find.Maps insertion order can
                // diverge across peers after lazy reloads (a map removed and re-streamed lands at a
                // different list index) — using the synced uniqueID instead keeps per-tick state
                // mutations applied in the same order on every peer.
                int n = maps.Count;
                if (n <= 1)
                {
                    for (int i = 0; i < n; i++) yield return maps[i].AsyncTime();
                    yield break;
                }
                // Avoid LINQ allocation in the hot path: small N, heap-allocate a tiny index array
                // (stackalloc isn't usable inside an iterator state machine), insertion-sort it,
                // then yield in order. Insertion sort is O(N^2) but N is bounded by playable map
                // count (~10), so the work is trivial vs. the per-tick cost downstream.
                int[] idx = new int[n];
                for (int i = 0; i < n; i++) idx[i] = i;
                for (int i = 1; i < n; i++)
                {
                    int cur = idx[i];
                    int key = maps[cur].uniqueID;
                    int j = i - 1;
                    while (j >= 0 && maps[idx[j]].uniqueID > key)
                    {
                        idx[j + 1] = idx[j];
                        j--;
                    }
                    idx[j + 1] = cur;
                }
                for (int i = 0; i < n; i++) yield return maps[idx[i]].AsyncTime();
            }
        }

        // O(1) id lookup. Cache is keyed on the actual sequence of map uniqueIDs, not just the
        // count. Streaming reload, Rejoiner.DoRejoin, and replay scrubbing can swap maps without
        // changing Find.Maps.Count — keying on count alone returns stale AsyncTimeComp instances
        // and routes commands to ghosts. The per-call sentinel walk is N int comparisons (N≤~10
        // in practice), still much cheaper than the original FirstOrDefault+lambda. Reset() clears
        // the cache so a session reload starts fresh.
        private static readonly Dictionary<int, ITickable> tickableLookup = new();
        private static int[] tickableLookupKey = Array.Empty<int>();

        static Stopwatch updateTimer = Stopwatch.StartNew();
        public static Stopwatch tickTimer = Stopwatch.StartNew();

        [TweakValue("Multiplayer")]
        public static bool doSimulate = true;

        static bool Prefix()
        {
            if (Multiplayer.Client == null) return true;
            if (!ShouldHandle) return false;
            if (Frozen) return false;

            int ticksBehind = tickUntil - Timer;
            realTime += Time.deltaTime * 1000f;

            // Slow down when few ticksBehind to accumulate a buffer
            // Try to speed up when many ticksBehind
            // Else run at the speed from the server
            float stpt = ticksBehind <= 3 ? serverTimePerTick * 1.2f : ticksBehind >= 7 ? serverTimePerTick * 0.8f : serverTimePerTick;

            if (Multiplayer.IsReplay)
                stpt = StandardTimePerFrame * ReplayMultiplier();

            if (Timer >= tickUntil)
            {
                ticksToRun = 0;
            }
            else if (realTime > 0 && stpt > 0)
            {
                avgFrameTime = (avgFrameTime + Time.deltaTime * 1000f) / 2f;

                ticksToRun = Multiplayer.IsReplay ? Mathf.CeilToInt(realTime / stpt) : 1;
                realTime -= ticksToRun * stpt;
            }

            if (realTime > 0)
                realTime = 0;

            if (Time.time - frameTimeSentAt > 32f/1000f)
            {
                Multiplayer.Client.Send(new ClientFrameTimePacket(avgFrameTime));
                frameTimeSentAt = Time.time;
            }

            if (Multiplayer.IsReplay && replayTimeSpeed == TimeSpeed.Paused || !doSimulate)
                ticksToRun = 0;

            if (simulating is { targetIsTickUntil: true })
                simulating.target = tickUntil;

            CheckFinishSimulating();

            if (MpVersion.IsDebug)
                SimpleProfiler.Start();

            RunCmds();
            if  (LongEventHandler.eventQueue.Count == 0)
            {
                DoUpdate(out var worked);
                if (worked) workTicks++;
            }

            if (MpVersion.IsDebug)
                SimpleProfiler.Pause();

            CheckFinishSimulating();

            return false;
        }

        private static void CheckFinishSimulating()
        {
            if (simulating?.target != null && Timer >= simulating.target)
            {
                simulating.onFinish?.Invoke();
                ClearSimulating();
            }
        }

        public static void SetSimulation(int ticks = 0, bool toTickUntil = false, Action onFinish = null, Action onCancel = null, string cancelButtonKey = null, bool canEsc = false, string simTextKey = null)
        {
            simulating = new SimulatingData
            {
                target = ticks,
                targetIsTickUntil = toTickUntil,
                onFinish = onFinish,
                onCancel = onCancel,
                canEsc = canEsc,
                cancelButtonKey = cancelButtonKey ?? "CancelButton",
                simTextKey = simTextKey ?? "MpSimulating"
            };
        }

        static ITickable CurrentTickable()
        {
            if (WorldRendererUtility.WorldSelected)
                return Multiplayer.AsyncWorldTime;

            if (Find.CurrentMap != null)
                return Find.CurrentMap.AsyncTime();

            return null;
        }

        static void Postfix()
        {
            if (Multiplayer.Client == null || Find.CurrentMap == null) return;
            Shader.SetGlobalFloat(ShaderPropertyIDs.GameSeconds, Find.CurrentMap.AsyncTime().mapTicks.TicksToSeconds());
        }

        // Defensive buffer for cmds that arrive before their target tickable exists. With the
        // streaming protocol the server only sends map-scoped cmds after Client_MapLoaded ack, so
        // this should always be empty during normal play. If it grows we have a server bug — the
        // log line below makes that loud rather than silently dropping the cmd.
        private static readonly List<ScheduledCommand> deferredCmds = new();

        private static bool RunCmds()
        {
            int curTimer = Timer;

            // Fail-fast: detect FP rounding-mode drift on every tick instead of waiting up to
            // 30 ticks for the next sync-opinion exchange. Latches the mode we started in;
            // can't catch initial peer mismatch but does catch the actual common bug pattern
            // (native DLL or Unity setting flipping mode mid-session).
            var currentRound = RoundMode.GetCurrentRoundMode();
            if (RoundMode.Expected is { } expected)
            {
                if (currentRound != expected)
                {
                    Multiplayer.session.TriggerProtocolDesync(
                        $"Round mode drift: expected {expected}, got {currentRound}");
                    return true;
                }
            }
            else
            {
                RoundMode.Expected = currentRound;
            }

            // Re-attempt deferred cmds first: target might have appeared since last call. We can only
            // execute a deferred cmd when its tick matches the current Timer — running it on a later
            // tick would diverge from peers (deterministic desync). If the tick has already passed,
            // there is no recovery: drop with a loud error so the bug is investigated, and rely on
            // SyncCoordinator's hash check to flag the resulting state divergence as a desync.
            if (deferredCmds.Count > 0)
            {
                for (int i = deferredCmds.Count - 1; i >= 0; i--)
                {
                    var cmd = deferredCmds[i];
                    var target = TickableById(cmd.mapId);
                    if (target == null) continue;          // still missing; keep waiting
                    if (cmd.ticks > curTimer) continue;    // not yet time; keep deferred
                    deferredCmds.RemoveAt(i);
                    if (cmd.ticks < curTimer)
                    {
                        // Deterministic desync waiting to happen. Drop and let SyncCoordinator catch
                        // the divergence on the next opinion exchange rather than silently applying
                        // it on the wrong tick.
                        Log.Error($"!!! Late deferred cmd dropped (cmd.ticks={cmd.ticks}, curTimer={curTimer}, mapId={cmd.mapId}, type={cmd.type})");
                        continue;
                    }
                    target.ExecuteCmd(cmd);
                }
            }

            foreach (ITickable tickable in AllTickables)
            {
                while (tickable.Cmds.Count > 0)
                {
                    int peekTicks = tickable.Cmds.Peek().ticks;

                    // Future cmd: standard wait. Run when curTimer catches up.
                    if (peekTicks > curTimer) break;

                    if (peekTicks < curTimer)
                    {
                        // The head cmd's tick is already in the past. Executing it now would
                        // mutate state on a tick where peers had no such mutation — a guaranteed,
                        // silent desync. There is no recovery from this state in the local sim, so
                        // halt explicitly and let the user rejoin from the server's authoritative
                        // state rather than letting RunCmds spin forever (Peek().ticks==curTimer
                        // would never become true since curTimer only advances).
                        var stale = tickable.Cmds.Peek();
                        Multiplayer.session.TriggerProtocolDesync(
                            $"Late command at queue head: cmd.ticks={stale.ticks}, curTimer={curTimer}, mapId={stale.mapId}, type={stale.type}");
                        return true;
                    }

                    ScheduledCommand cmd = tickable.Cmds.Dequeue();
                    // Minimal code impact fix for #733. Having all the commands be added to a single queue gets rid of
                    // the out-of-order execution problem. With a proper fix, this can be reverted to tickable.ExecuteCmd
                    var target = TickableById(cmd.mapId);
                    if (target == null)
                    {
                        if (cmd.mapId >= 0)
                        {
                            // Streaming server bug or map not yet loaded race. Stash and retry later
                            // rather than dropping silently. ScheduleCommand on the streaming path
                            // shouldn't deliver cmds for unloaded maps, so this is defense-in-depth.
                            Log.Warning($"Tickable for mapId {cmd.mapId} not found, deferring cmd: {cmd}");
                            deferredCmds.Add(cmd);
                        }
                        else
                        {
                            Log.Error($"!!! Tickable of {cmd.mapId} not found! {cmd}");
                        }
                    } else target.ExecuteCmd(cmd);

                    if (LongEventHandler.eventQueue.Count > 0) return true; // Yield to e.g. join-point creation
                }
            }

            return false;
        }

        public static void DoUpdate(out bool worked)
        {
            worked = false;
            updateTimer.Restart();

            while (Simulating ? (Timer < simulating.target && updateTimer.ElapsedMilliseconds < 25) : (ticksToRun > 0))
            {
                if (RunCmds())
                    return;
                if (DoTick(ref worked))
                    return;
            }
        }

        public static void DoTicks(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                bool worked = false;
                DoTick(ref worked);
            }
        }

        // Returns whether the tick loop should stop
        public static bool DoTick(ref bool worked)
        {
            tickTimer.Restart();

            foreach (ITickable tickable in AllTickables)
            {
                if (tickable.TimePerTick(tickable.DesiredTimeSpeed) == 0) continue;
                tickable.TimeToTickThrough += 1f;

                worked = true;
                TickTickable(tickable);
            }

            ConstantTicker.Tick();

            ticksToRun -= 1;
            Timer += 1;

            tickTimer.Stop();

            if (Multiplayer.session.desynced || Timer >= tickUntil || LongEventHandler.eventQueue.Count > 0)
            {
                ticksToRun = 0;
                return true;
            }

            return false;
        }

        private static void TickTickable(ITickable tickable)
        {
            while (tickable.TimeToTickThrough >= 0)
            {
                float timePerTick = tickable.TimePerTick(tickable.DesiredTimeSpeed);
                if (timePerTick == 0) break;

                tickable.TimeToTickThrough -= timePerTick;

                try
                {
                    tickable.Tick();
                }
                catch (Exception e)
                {
                    Log.Error($"Exception during ticking {tickable}: {e}");
                }
            }
        }

        private static float ReplayMultiplier()
        {
            if (!Multiplayer.IsReplay || Simulating) return 1f;

            if (replayTimeSpeed == TimeSpeed.Paused)
                return 0f;

            ITickable tickable = CurrentTickable();
            if (tickable.TimePerTick(tickable.DesiredTimeSpeed) == 0f)
                return 1 / 100f; // So paused sections of the timeline are skipped through

            return tickable.ActualRateMultiplier(tickable.DesiredTimeSpeed) / tickable.ActualRateMultiplier(replayTimeSpeed);
        }

        public static float TimePerTick(this ITickable tickable, TimeSpeed speed)
        {
            if (tickable.ActualRateMultiplier(speed) == 0f)
                return 0f;
            return 1f / tickable.ActualRateMultiplier(speed);
        }

        public static float ActualRateMultiplier(this ITickable tickable, TimeSpeed speed)
        {
            if (Multiplayer.GameComp.asyncTime)
                return tickable.TickRateMultiplier(speed);

            var rate = Multiplayer.AsyncWorldTime.TickRateMultiplier(speed);
            foreach (var map in Find.Maps)
                rate = Math.Min(rate, map.AsyncTime().TickRateMultiplier(speed));

            return rate;
        }

        public static void ClearSimulating() => simulating = null;

        public static void Reset()
        {
            ClearSimulating();
            Timer = 0;
            tickUntil = 0;
            ticksToRun = 0;
            serverFrozen = false;
            workTicks = 0;
            serverTimePerTick = 0;
            avgFrameTime = StandardTimePerFrame;
            realTime = 0;
            deferredCmds.Clear();
            tickableLookup.Clear();
            tickableLookupKey = Array.Empty<int>();
            TimeControlPatch.prePauseTimeSpeed = null;
            RoundMode.Reset();
        }

        public static void SetTimer(int value) => Timer = value;

        public static ITickable TickableById(int tickableId)
        {
            var maps = Find.Maps;

            // Identity check: same length AND same sequence of uniqueIDs. Maps remove+add in a
            // single load would leak a stale cache entry under a count-only check, so verify the
            // identity sequence matches.
            bool identityMatch = tickableLookupKey.Length == maps.Count;
            if (identityMatch)
            {
                for (int i = 0; i < maps.Count; i++)
                {
                    if (tickableLookupKey[i] != maps[i].uniqueID)
                    {
                        identityMatch = false;
                        break;
                    }
                }
            }

            if (!identityMatch)
            {
                tickableLookup.Clear();
                tickableLookup[Multiplayer.AsyncWorldTime.TickableId] = Multiplayer.AsyncWorldTime;
                var newKey = new int[maps.Count];
                for (int i = 0; i < maps.Count; i++)
                {
                    var map = maps[i];
                    newKey[i] = map.uniqueID;
                    var atc = map.AsyncTime();
                    tickableLookup[atc.TickableId] = atc;
                }
                tickableLookupKey = newKey;
            }
            return tickableLookup.GetValueOrDefault(tickableId);
        }
    }

    public class SimulatingData
    {
        public int? target;
        public bool targetIsTickUntil; // When true, the target field is always up-to-date with TickPatch.tickUntil
        public Action onFinish;
        public bool canEsc;
        public Action onCancel;
        public string cancelButtonKey;
        public string simTextKey;
    }
}
