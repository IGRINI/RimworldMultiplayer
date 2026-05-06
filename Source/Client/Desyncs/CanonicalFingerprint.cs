using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Common.Util;
using Verse;

namespace Multiplayer.Client.Desyncs;

// World-level fingerprint combined every 30 ticks alongside the existing RNG/trace opinion in
// ClientSyncOpinion. Random states already cover behavioural drift; this catches structural
// world-state cases random states can't see (a peer silently lost a faction, world clock
// drifted) so a desync surfaces with a categorical message instead of just "trace hashes
// don't match" hundreds of ticks later.
//
// CRITICAL CORRECTNESS RULE: every component must be IDENTICAL between two in-sync peers
// REGARDLESS of which maps they have loaded. Standalone map streaming is allowed to keep
// different loaded-map subsets per client (one player on map 5, another in world view, an
// arbiter with nothing streamed) — they're all "in sync". Anything that varies with the
// loaded-map subset (Find.Maps.Count, per-map pawn/thing counts, lazy-inflated map state)
// MUST NOT enter this hash. Per-map fingerprints belong in a separate intersection-compared
// dictionary, not here. Same goes for receive-side counters (receivedCmds): a peer can have
// already received a future cmd that hasn't executed yet while another receives it a moment
// later, both in-sync at the same simulation tick.
//
// Components MUST be deterministic across processes: FNV-1a 64 from StableHash; every
// collection iterated in id-sorted order — never dictionary insertion order.
public static class CanonicalFingerprint
{
    public static ulong Compute()
    {
        // Format tag — bump if components change so a mid-rollout opinion exchange can't
        // produce a false hash match between old and new builds.
        ulong hash = StableHash.String("canonical-v2");

        // World tick. Multiplayer.AsyncWorldTime.worldTicks is the simulation clock that drives
        // every peer; a divergence here is the clearest possible structural desync.
        hash = StableHash.Combine(hash, MixUInt64((ulong)(uint)Multiplayer.AsyncWorldTime.worldTicks));

        // Factions are world-level state — replicated to every client regardless of which maps
        // are streamed in. Count + (loadID, defName) tuples in loadID order. defName is hashed
        // so a mod-renamed faction surfaces here rather than only at faction-data serialization.
        var factions = Find.FactionManager?.AllFactionsListForReading;
        if (factions != null)
        {
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
