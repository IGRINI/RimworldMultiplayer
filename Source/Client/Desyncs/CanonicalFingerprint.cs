using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Common.Util;
using Verse;

namespace Multiplayer.Client.Desyncs;

// Layered fingerprint of game state combined every 30 ticks alongside the existing RNG/trace
// opinion in ClientSyncOpinion. Random states already cover behavioural drift; this catches
// the structural cases random states can't see (a peer silently lost a faction, a map id
// disappeared, a thing count diverged) so a desync surfaces with a categorical message instead
// of just "trace hashes don't match" hundreds of ticks later.
//
// Components MUST be deterministic across processes and identical between in-sync peers:
// FNV-1a 64 from StableHash, and every collection is iterated in id-sorted order — never
// dictionary insertion order, which is process-dependent.
public static class CanonicalFingerprint
{
    public static ulong Compute()
    {
        // World tick first so a divergence in world clock surfaces at the top of the layered
        // hash. Read once and reuse — Multiplayer.AsyncWorldTime.worldTicks is a plain int
        // field, no allocation.
        ulong hash = StableHash.String("canonical-v1");
        hash = StableHash.Combine(hash, MixUInt64((ulong)(uint)Multiplayer.AsyncWorldTime.worldTicks));

        // Map ids in canonical order: sort by uniqueID, never trust list-natural order.
        var maps = Find.Maps;
        var sortedMaps = maps != null
            ? maps.OrderBy(m => m.uniqueID).ToList()
            : new List<Map>();

        hash = StableHash.Combine(hash, MixUInt64((ulong)(uint)sortedMaps.Count));

        // Per-map cheap proxies. Avoid LINQ inside the loop — sortedMaps is already realised
        // and we only need indexed access.
        for (int i = 0; i < sortedMaps.Count; i++)
        {
            var map = sortedMaps[i];
            hash = StableHash.Combine(hash, MixUInt64((ulong)(uint)map.uniqueID));
            hash = StableHash.Combine(hash, MixUInt64((ulong)(uint)map.mapPawns.AllPawns.Count));
            hash = StableHash.Combine(hash, MixUInt64((ulong)(uint)map.listerThings.AllThings.Count));
        }

        // Factions: count + (id, defName) tuples in id order. defName is FNV-1a hashed so a
        // mod-renamed faction surfaces as a fingerprint diff rather than only at faction-data
        // serialization time.
        var factions = Find.FactionManager?.AllFactionsListForReading;
        if (factions != null)
        {
            // Snapshot to a local sorted array so we don't allocate inside the inner loop.
            var sortedFactions = factions.OrderBy(f => f.loadID).ToList();
            hash = StableHash.Combine(hash, MixUInt64((ulong)(uint)sortedFactions.Count));
            for (int i = 0; i < sortedFactions.Count; i++)
            {
                var f = sortedFactions[i];
                hash = StableHash.Combine(hash, MixUInt64((ulong)(uint)f.loadID));
                hash = StableHash.Combine(hash, StableHash.String(f.def?.defName));
            }
        }
        else
        {
            hash = StableHash.Combine(hash, MixUInt64(0));
        }

        // Command sequence head — a peer falling behind on receivedCmds would already trip
        // command random-state mismatches, but folding it in lets the top-level fingerprint
        // distinguish "real game-state divergence" from "I just haven't applied cmd N yet".
        var session = Multiplayer.session;
        hash = StableHash.Combine(hash, MixUInt64((ulong)(uint)(session?.receivedCmds ?? 0)));

        return hash;
    }

    // Per-component diagnostic snapshot. Called only on mismatch from the desync log path —
    // duplicates Compute()'s walk but emits each component instead of folding them. Keep in
    // lockstep with Compute(): if Compute() reads a new field, ComputeDetailed() should too,
    // otherwise the diagnostic loses the component that diverged.
    public static Dictionary<string, ulong> ComputeDetailed()
    {
        var dict = new Dictionary<string, ulong>();
        try
        {
            dict["worldTicks"] = (ulong)(uint)Multiplayer.AsyncWorldTime.worldTicks;

            var maps = Find.Maps;
            var sortedMaps = maps != null
                ? maps.OrderBy(m => m.uniqueID).ToList()
                : new List<Map>();
            dict["mapCount"] = (ulong)(uint)sortedMaps.Count;

            ulong mapsAcc = StableHash.String("maps");
            for (int i = 0; i < sortedMaps.Count; i++)
            {
                var map = sortedMaps[i];
                mapsAcc = StableHash.Combine(mapsAcc, MixUInt64((ulong)(uint)map.uniqueID));
                mapsAcc = StableHash.Combine(mapsAcc, MixUInt64((ulong)(uint)map.mapPawns.AllPawns.Count));
                mapsAcc = StableHash.Combine(mapsAcc, MixUInt64((ulong)(uint)map.listerThings.AllThings.Count));
                dict[$"map{map.uniqueID}.pawns"] = (ulong)(uint)map.mapPawns.AllPawns.Count;
                dict[$"map{map.uniqueID}.things"] = (ulong)(uint)map.listerThings.AllThings.Count;
            }
            dict["mapsHash"] = mapsAcc;

            var factions = Find.FactionManager?.AllFactionsListForReading;
            if (factions != null)
            {
                var sortedFactions = factions.OrderBy(f => f.loadID).ToList();
                dict["factionCount"] = (ulong)(uint)sortedFactions.Count;
                ulong factionsAcc = StableHash.String("factions");
                for (int i = 0; i < sortedFactions.Count; i++)
                {
                    var f = sortedFactions[i];
                    factionsAcc = StableHash.Combine(factionsAcc, MixUInt64((ulong)(uint)f.loadID));
                    factionsAcc = StableHash.Combine(factionsAcc, StableHash.String(f.def?.defName));
                }
                dict["factionsHash"] = factionsAcc;
            }

            var session = Multiplayer.session;
            dict["receivedCmds"] = (ulong)(uint)(session?.receivedCmds ?? 0);
        }
        catch (Exception e)
        {
            dict["error"] = StableHash.String(e.GetType().Name);
        }

        return dict;
    }

    // FNV-1a fold of a 64-bit value, big-endian byte order. Hand-rolled instead of routing
    // through StableHash.Bytes(ReadOnlySpan<byte>) because the Client project targets net48
    // and Harmony's bundled Span<T> shim creates an ambiguity at compile time. The algorithm
    // is identical to FNV-1a 64 over 8 bytes — big-endian chosen arbitrarily but fixed
    // forever; changing endianness would silently desync peers on older builds. Inlined to
    // avoid an allocation per call (this runs once per fingerprint component, every 30 ticks).
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ulong MixUInt64(ulong value)
    {
        ulong hash = FnvOffsetBasis;
        unchecked
        {
            hash ^= (byte)(value >> 56); hash *= FnvPrime;
            hash ^= (byte)(value >> 48); hash *= FnvPrime;
            hash ^= (byte)(value >> 40); hash *= FnvPrime;
            hash ^= (byte)(value >> 32); hash *= FnvPrime;
            hash ^= (byte)(value >> 24); hash *= FnvPrime;
            hash ^= (byte)(value >> 16); hash *= FnvPrime;
            hash ^= (byte)(value >>  8); hash *= FnvPrime;
            hash ^= (byte) value;        hash *= FnvPrime;
        }
        return hash;
    }
}
