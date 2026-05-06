using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Multiplayer.Common.Util;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    public class SyncCoordinator
    {
        public bool ShouldCollect => !Multiplayer.IsReplay;

        private ClientSyncOpinion OpinionInBuilding =>
            currentOpinion ??= new ClientSyncOpinion(TickPatch.Timer)
            {
                isLocalClientsOpinion = true
            };

        // Contains both local and remote opinions. The first opinion is the oldest one, and the last is the newest one.
        // The host player has only local opinions.
        public readonly List<ClientSyncOpinion> knownClientOpinions = [];

        private ClientSyncOpinion currentOpinion;

        public int lastValidTick = -1;
        public bool arbiterWasPlayingOnLastValidTick;

        private const int MaxBacklog = 30;

        public ClientSyncOpinion FinishLocalOpinion()
        {
            if (!ShouldCollect || currentOpinion == null) return null;
            currentOpinion.roundMode = RoundMode.GetCurrentRoundMode();

            // Compute structural fingerprint on the main thread (this is called from
            // ConstantTicker.TickSyncCoordinator). Reading Find.Maps / Find.FactionManager
            // outside the main thread is unsafe, so don't move this off-thread without
            // also snapshotting the inputs first. A failure here MUST NOT crash the sync
            // coordinator — zero is treated as "fingerprint not available" by CheckForDesync,
            // which then falls back to the existing RNG/trace comparisons.
            try
            {
                currentOpinion.canonicalFingerprint = CanonicalFingerprint.Compute();
            }
            catch (Exception e)
            {
                Log.Warning($"CanonicalFingerprint.Compute threw: {e}");
                currentOpinion.canonicalFingerprint = 0;
            }

            var opinion = currentOpinion;
            currentOpinion = null;
            return opinion;
        }

        /// <summary>
        /// Adds a client opinion to the <see cref="knownClientOpinions"/> list and checks that it matches the most recent currently in there. If not, a desync event is fired.
        /// </summary>
        /// <param name="newOpinion">The <see cref="ClientSyncOpinion"/> to add and check.</param>
        public void AddClientOpinionAndCheckDesync(ClientSyncOpinion newOpinion)
        {
            //If we've already desynced, don't even bother
            if (Multiplayer.session.desynced) return;

            //If this is the first client opinion we have nothing to compare it with, so just add it
            if (knownClientOpinions.Count == 0)
            {
                knownClientOpinions.Add(newOpinion);
                return;
            }

            if (knownClientOpinions[0].isLocalClientsOpinion == newOpinion.isLocalClientsOpinion)
            {
                knownClientOpinions.Add(newOpinion);
                if (knownClientOpinions.Count > MaxBacklog)
                    RemoveAndClearFirst();
                return;
            }

            // Remove all opinions that started before this one, as it's the most up-to-date one
            while (knownClientOpinions.Count > 0 && knownClientOpinions[0].startTick < newOpinion.startTick)
                RemoveAndClearFirst();

            // If there are none left, we don't need to compare this new one
            if (knownClientOpinions.Count == 0)
            {
                knownClientOpinions.Add(newOpinion);
                return;
            }

            if (knownClientOpinions.First().startTick != newOpinion.startTick)
            {
                // Ignore this opinion
                newOpinion.Clear();
                return;
            }

            // If these two contain the same tick range - i.e., they start at the same time, because they should
            // continue to the current tick, then do a comparison.
            var oldOpinion = knownClientOpinions.RemoveFirst();

            // Actually do the comparison to find any desync
            var desyncMessage = oldOpinion.CheckForDesync(newOpinion);

            if (desyncMessage != null)
            {
                MpLog.Log($"Desynced after last valid tick {lastValidTick}: {desyncMessage}");
                Multiplayer.session.desynced = true;
                TickPatch.ClearSimulating();
                OnMainThread.Enqueue(() => HandleDesync(oldOpinion, newOpinion, desyncMessage));
            }
            else
            {
                // Update fields
                lastValidTick = oldOpinion.startTick;
                arbiterWasPlayingOnLastValidTick = Multiplayer.session.ArbiterPlaying;

                // Return inner data to the pool
                oldOpinion.Clear();
            }
        }

        private void RemoveAndClearFirst()
        {
            var opinion = knownClientOpinions.RemoveFirst();
            opinion.Clear();
        }

        /// <summary>
        /// Called by <see cref="AddClientOpinionAndCheckDesync"/> if the newly added opinion doesn't match with what other ones.
        /// </summary>
        /// <param name="oldOpinion">The first up-to-date client opinion present in <see cref="knownClientOpinions"/>, that disagreed with the new one</param>
        /// <param name="newOpinion">The opinion passed to <see cref="AddClientOpinionAndCheckDesync"/> that disagreed with the currently known opinions.</param>
        /// <param name="desyncMessage">The error message that explains exactly what desynced.</param>
        private void HandleDesync(ClientSyncOpinion oldOpinion, ClientSyncOpinion newOpinion, string desyncMessage)
        {
            // Identify which of the two sync infos is local and which is the remote.
            var local = oldOpinion.isLocalClientsOpinion ? oldOpinion : newOpinion;
            var remote = !oldOpinion.isLocalClientsOpinion ? oldOpinion : newOpinion;

            var diffAt = FindTraceHashesDiffTick(local, remote, out var found);
            Multiplayer.Client.Send(new ClientDesyncedPacket(local.startTick, diffAt));

            MpUI.ClearWindowStack();
            Find.WindowStack.Add(new DesyncedWindow(
                desyncMessage,
                new SaveableDesyncInfo(this, local, remote, diffAt, found)
            ));

            // Section 8 auto-rejoin opt-in. Window stays up; if the setting is on, rejoin fires
            // after the report has had a chance to write. DesyncedWindow.WindowUpdate flushes the
            // zip after at most maxWait (5s) — wait one more second on top so the file actually
            // lands before MemoryUtility.ClearAllMapsAndWorld kicks in and the window is torn
            // down. Set autosaveOnDesync (or whatever future flag) to write earlier without this
            // wait if needed.
            if (Multiplayer.settings.autoRejoinOnDesync)
                OnMainThread.Schedule(static () =>
                {
                    if (Multiplayer.Client != null && Multiplayer.session != null && Multiplayer.session.desynced)
                        Rejoiner.DoRejoin();
                }, 6f);
        }

        private static int FindTraceHashesDiffTick(ClientSyncOpinion local, ClientSyncOpinion remote, out bool found)
        {
            found = true;
            //Find the length of whichever stack trace is shorter.
            var localCount = local.desyncStackTraceHashes.Count;
            var remoteCount = remote.desyncStackTraceHashes.Count;
            int count = Math.Min(localCount, remoteCount);

            //Find the point at which the hashes differ - this is where the desync occurred.
            for (int i = 0; i < count; i++)
                if (local.desyncStackTraceHashes[i] != remote.desyncStackTraceHashes[i])
                    return i;

            found = false;
            if (localCount != remoteCount)
                return count - 1;

            return -1;
        }

        /// <summary>
        /// Adds a random state to the commandRandomStates list. World-scoped (global) cmds only —
        /// every peer sees these. Map-scoped cmds use TryAddMapCommandRandomState because in
        /// standalone streaming peers can have different loaded-map subsets and a flat list
        /// would produce false desyncs across them.
        /// </summary>
        /// <param name="state">The state to add</param>
        public void TryAddCommandRandomState(ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.commandRandomStates.Add((uint) (state >> 32));
        }

        /// <summary>
        /// Adds a random state captured during execution of a map-scoped command. Routed into
        /// the per-map random state list keyed by mapId so CheckForDesync can compare it only
        /// across peers that have the same map loaded.
        /// </summary>
        public void TryAddMapCommandRandomState(int mapId, ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.GetRandomStatesForMap(mapId).Add((uint) (state >> 32));
        }

        /// <summary>
        /// Adds a random state to the worldRandomStates list
        /// </summary>
        /// <param name="state">The state to add</param>
        public void TryAddWorldRandomState(ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.worldRandomStates.Add((uint) (state >> 32));
        }

        /// <summary>
        /// Adds a random state to the list of the map random state handler for the map with the given id
        /// </summary>
        /// <param name="map">The map id to add the state to</param>
        /// <param name="state">The state to add</param>
        public void TryAddMapRandomState(int map, ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.GetRandomStatesForMap(map).Add((uint) (state >> 32));
        }

        /// <summary>
        /// Logs an item to aid in desync debugging.
        /// </summary>
        /// <param name="info1">Information to be logged</param>
        /// <param name="info2">Information to be logged</param>
        public void TryAddInfoForDesyncLog(string info1, string info2)
        {
            if (!ShouldCollect) return;

            OpinionInBuilding.TryMarkSimulating();

            // Was string.GetHashCode() — randomised per process so it produced spurious
            // desyncs when peers happened to disagree on the same string. StableHash is
            // deterministic across processes/.NET versions.
            int hash = Gen.HashCombineInt(StableHash.StringInt32(info1), StableHash.StringInt32(info2));

            OpinionInBuilding.desyncStackTraces.Add(new StackTraceLogItemObj {
                tick = TickPatch.Timer,
                hash = hash,
                info1 = info1,
                info2 = info2,
            });

            OpinionInBuilding.desyncStackTraceHashes.Add(hash);
        }

        public void TryAddStackTraceForDesyncLogRaw(StackTraceLogItemRaw item, int depth, int hashIn, string moreInfo = null)
        {
            if (!ShouldCollect) return;

            OpinionInBuilding.TryMarkSimulating();

            item.depth = depth;
            item.ticksGame = Find.TickManager.ticksGameInt;
            item.rngState = Rand.StateCompressed;
            item.tick = TickPatch.Timer;
            item.factionName = Faction.OfPlayer?.Name ?? string.Empty;
            item.moreInfo = moreInfo;

            var thing = ThingContext.Current;
            if (thing != null)
            {
                item.thingDef = thing.def;
                item.thingId = thing.thingIDNumber;
            }

            var hash = Gen.HashCombineInt(hashIn, depth, (int)(item.rngState >> 32), (int)item.rngState);
            item.hash = hash;

            OpinionInBuilding.desyncStackTraces.Add(item);

            // Track & network trace hash, for comparison with other opinions.
            OpinionInBuilding.desyncStackTraceHashes.Add(hash);
        }

        public static string MethodNameWithIL(string rawName)
        {
            // Note: The names currently don't include IL locations so the code is commented out

            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000] in <c847e073cda54790b59d58357cc8cf98>:0
            // =>
            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000]
            // rawName = rawName.Substring(0, rawName.LastIndexOf(']') + 1);

            return rawName;
        }

        public static string MethodNameWithoutIL(string rawName)
        {
            // Note: The names currently don't include IL locations so the code is commented out

            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000] in <c847e073cda54790b59d58357cc8cf98>:0
            // =>
            // at Verse.AI.JobDriver.ReadyForNextToil ()
            // rawName = rawName.Substring(0, rawName.LastIndexOf('['));

            return rawName;
        }
    }
}
