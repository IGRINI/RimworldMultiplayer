using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Common.Util;

// One ulong that summarises everything the join-time compat check actually compares:
// mod set + file hashes + config text + RW build + MP build. Mismatch on this number
// means the per-file diff in the join window will find at least one delta — match means
// the diff would find none.
//
// Inputs use plain primitives (no Client types) so this can live in Common — useful both
// for the Client UI and for any future server-side pre-flight check that wants to
// broadcast its setup hash before clients pay the cost of pulling the full JoinData.
//
// FNV-1a 64 via StableHash.Combine. Stable across processes, .NET versions, machine
// reboots — same as the rest of the cross-process hash surface. Inputs that are
// semantically unordered (file traversal order, config order) are sorted before folding
// so dictionary insertion order can never affect the output.
public static class CompatibilityFingerprint
{
    public static ulong Compute(
        IEnumerable<(string packageId, string source)> mods,
        IEnumerable<(string modId, string relPath, long hash)> files,
        IEnumerable<(string modId, string fileName, string contents)> configs,
        string rwVersion,
        string mpVersion)
    {
        ulong hash = StableHash.String("compat-v1");
        hash = StableHash.Combine(hash, StableHash.String(rwVersion));
        hash = StableHash.Combine(hash, StableHash.String(mpVersion));

        // Mod list: order matters — load order is part of the setup. Don't sort.
        if (mods != null)
            foreach (var (id, source) in mods)
            {
                hash = StableHash.Combine(hash, StableHash.String(id));
                hash = StableHash.Combine(hash, StableHash.String(source));
            }

        // Files: sort by (modId, relPath) so two clients with the same content but different
        // traversal order produce the same fingerprint. Without this, fingerprint would flap
        // on filesystem-order differences.
        if (files != null)
        {
            var sortedFiles = files
                .OrderBy(f => f.modId, System.StringComparer.Ordinal)
                .ThenBy(f => f.relPath, System.StringComparer.Ordinal);

            foreach (var f in sortedFiles)
            {
                hash = StableHash.Combine(hash, StableHash.String(f.modId));
                hash = StableHash.Combine(hash, StableHash.String(f.relPath));
                hash = StableHash.Combine(hash, unchecked((ulong)f.hash));
            }
        }

        // Configs: same sort rule — (modId, fileName) — so reorderings don't move the hash.
        if (configs != null)
        {
            var sortedConfigs = configs
                .OrderBy(c => c.modId, System.StringComparer.Ordinal)
                .ThenBy(c => c.fileName, System.StringComparer.Ordinal);

            foreach (var c in sortedConfigs)
            {
                hash = StableHash.Combine(hash, StableHash.String(c.modId));
                hash = StableHash.Combine(hash, StableHash.String(c.fileName));
                hash = StableHash.Combine(hash, StableHash.String(c.contents));
            }
        }

        return hash;
    }
}
