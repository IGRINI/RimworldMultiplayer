using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client
{

    public class ClientSyncOpinion(int startTick)
    {
        public bool isLocalClientsOpinion;

        public int startTick = startTick;
        public List<uint> commandRandomStates = new();
        public List<uint> worldRandomStates = new();
        public List<MapRandomStateData> mapStates = new();

        // todo Unused for now
        public List<int> pawnCapacityHashes = new();
        public List<int> pawnStatHashes = new();
        public List<int> pawnNeedHashes = new();

        // Serialized only after a desync to reduce bandwidth usage in regular gameplay (only the hashes are used) and
        // help with debugging in case something goes wrong.
        public List<StackTraceLogItem> desyncStackTraces = new();
        public List<int> desyncStackTraceHashes = new();
        public bool simulating;
        public RoundModeEnum roundMode;

        // Layered structural fingerprint. Computed by CanonicalFingerprint.Compute() at
        // opinion finalization and compared in CheckForDesync BEFORE the heavier sequence
        // checks so a structural divergence (lost faction, pawn count drift, missing map)
        // surfaces with a categorical reason instead of "trace hashes don't match".
        public ulong canonicalFingerprint;

        public string CheckForDesync(ClientSyncOpinion other)
        {
            // Comparison is split into two scopes:
            //   GLOBAL — must be identical across all peers regardless of which maps they have
            //            streamed in. roundMode, canonicalFingerprint (already streaming-safe
            //            per its own design), worldRandomStates, commandRandomStates (now only
            //            populated for world-scoped cmds — map-scoped cmds route into mapStates
            //            via TryAddMapCommandRandomState). These all SequenceEqual cleanly.
            //   PER-MAP — compared only on the intersection of mapIds present in both opinions.
            //             A peer in world view, a peer on map 5, and an arbiter with nothing
            //             streamed are all "in sync" with valid server state — comparing the
            //             whole mapStates list flat (or even just the mapId lists) creates
            //             false desyncs as soon as opinions are exchanged.
            //
            // desyncStackTraceHashes is intentionally NOT load-bearing here. Traces are
            // recorded on every Rand.PushState/PopState across both world ticks and per-map
            // ticks; in streaming, the per-map ticks contribute scope-specific hashes that
            // a peer without that map loaded simply doesn't generate. Trace hashes are still
            // shipped on the wire and used by FindTraceHashesDiffTick in SyncCoordinator after
            // a desync is detected through other means, to locate the divergence point.
            if (roundMode != other.roundMode)
                return $"FP round mode doesn't match: {roundMode} != {other.roundMode}";

            // Cheap top-level structural check: if this trips, downstream checks WILL also
            // trip but with less informative messages. Only treat the fingerprint as load-
            // bearing when both sides actually computed one (zero means the local compute
            // threw and was zeroed by the catch in SyncCoordinator.FinishLocalOpinion).
            if (canonicalFingerprint != 0 && other.canonicalFingerprint != 0
                && canonicalFingerprint != other.canonicalFingerprint)
                return $"Canonical fingerprint mismatch: 0x{canonicalFingerprint:X16} vs 0x{other.canonicalFingerprint:X16}";

            if (!worldRandomStates.SequenceEqual(other.worldRandomStates))
                return "Wrong random state for the world";

            // Global cmds only after the per-map split. If two peers disagree here, they
            // disagreed on a globally-broadcast command — every peer sees those, so this is a
            // true desync irrespective of streaming.
            if (!commandRandomStates.SequenceEqual(other.commandRandomStates))
                return "Random state from commands doesn't match";

            // Per-map intersection. Build a quick lookup from the other opinion so we don't
            // scan its list for every entry on our side.
            Dictionary<int, List<uint>> otherByMapId = null;
            for (int i = 0; i < mapStates.Count; i++)
            {
                var localMap = mapStates[i];
                otherByMapId ??= BuildMapStateLookup(other);
                if (!otherByMapId.TryGetValue(localMap.mapId, out var otherStates))
                    continue; // peer didn't have this map streamed in — nothing to compare
                if (!localMap.randomStates.SequenceEqual(otherStates))
                    return $"Wrong random state on map {localMap.mapId}";
            }

            return null;
        }

        private static Dictionary<int, List<uint>> BuildMapStateLookup(ClientSyncOpinion op)
        {
            var dict = new Dictionary<int, List<uint>>(op.mapStates.Count);
            for (int i = 0; i < op.mapStates.Count; i++)
                dict[op.mapStates[i].mapId] = op.mapStates[i].randomStates;
            return dict;
        }

        public List<uint> GetRandomStatesForMap(int mapId)
        {
            var result = mapStates.Find(m => m.mapId == mapId);
            if (result != null) return result.randomStates;
            mapStates.Add(result = new MapRandomStateData(mapId));
            return result.randomStates;
        }

        public byte[] Serialize()
        {
            var writer = new ByteWriter();

            writer.WriteInt32(startTick);
            writer.WritePrefixedUInts(commandRandomStates);
            writer.WritePrefixedUInts(worldRandomStates);

            writer.WriteInt32(mapStates.Count);
            foreach (var map in mapStates)
            {
                writer.WriteInt32(map.mapId);
                writer.WritePrefixedUInts(map.randomStates);
            }

            writer.WritePrefixedInts(desyncStackTraceHashes);
            writer.WriteBool(simulating);
            writer.WriteShort((short)roundMode);
            writer.WriteULong(canonicalFingerprint);

            return writer.ToArray();
        }

        public static ClientSyncOpinion FromNet(SyncOpinion sync) => new(sync.startTick)
        {
            commandRandomStates = sync.commandRandomStates,
            worldRandomStates = sync.worldRandomStates,
            mapStates = sync.mapRandomStates.Select(state => new MapRandomStateData(state.mapId)
                { randomStates = state.randomStates }).ToList(),
            desyncStackTraceHashes = sync.traceHashes,
            simulating = sync.simulating,
            roundMode = sync.roundMode,
            canonicalFingerprint = sync.canonicalFingerprint
        };

        public SyncOpinion ToNet() => new()
        {
            startTick = startTick,
            commandRandomStates = commandRandomStates,
            worldRandomStates = worldRandomStates,
            mapRandomStates = mapStates.Select(state => new MapRandomState
                { mapId = state.mapId, randomStates = state.randomStates }).ToList(),
            traceHashes = desyncStackTraceHashes,
            simulating = simulating,
            roundMode = roundMode,
            canonicalFingerprint = canonicalFingerprint
        };

        public static ClientSyncOpinion Deserialize(ByteReader data)
        {
            var startTick = data.ReadInt32();

            var cmds = new List<uint>(data.ReadPrefixedUInts());
            var world = new List<uint>(data.ReadPrefixedUInts());

            var maps = new List<MapRandomStateData>();
            int mapCount = data.ReadInt32();
            for (int i = 0; i < mapCount; i++)
            {
                int mapId = data.ReadInt32();
                var mapData = new List<uint>(data.ReadPrefixedUInts());
                maps.Add(new MapRandomStateData(mapId) { randomStates = mapData });
            }

            var traceHashes = new List<int>(data.ReadPrefixedInts());
            var simulating = data.ReadBool();
            var roundMode = data.ReadShort();
            var canonicalFingerprint = data.ReadULong();

            return new ClientSyncOpinion(startTick)
            {
                commandRandomStates = cmds,
                worldRandomStates = world,
                mapStates = maps,
                desyncStackTraceHashes = traceHashes,
                simulating = simulating,
                roundMode = (RoundModeEnum)roundMode,
                canonicalFingerprint = canonicalFingerprint
            };
        }

        public void TryMarkSimulating()
        {
            if (TickPatch.Simulating)
                simulating = true;
        }

        public string GetFormattedStackTracesForRange(int diffAt)
        {
            var start = Math.Max(0, diffAt - Multiplayer.settings.desyncTracesRadius);
            var end = diffAt + Multiplayer.settings.desyncTracesRadius;
            var traceId = start;

            return
                $"Trace count: {desyncStackTraces.Count}\nTrace of first desynced map random state:\n{diffAt} {desyncStackTraces.ElementAtOrDefault(diffAt)}" +
                "\n\nContext traces:\n" +
                desyncStackTraces
                .Skip(start)
                .Take(end - start)
                .Join(a => traceId++ + " " + a, "\n\n");
        }

        public void Clear()
        {
            for (int i = 0; i < desyncStackTraces.Count; i++)
                desyncStackTraces[i].Dispose();
        }
    }
}
