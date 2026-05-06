using System;
using System.Runtime.CompilerServices;

namespace Multiplayer.Common.Util;

// Deterministic, allocation-free hashes for any value that's compared between clients
// or written to the wire. CLR `string.GetHashCode()` is randomised per process and
// changes between .NET versions — using it for desync trace hashes produces false
// positives. FNV-1a 64 is small, fast, and stable across processes/frameworks.
public static class StableHash
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong String(string s)
    {
        if (s == null) return FnvOffsetBasis;

        ulong hash = FnvOffsetBasis;
        unchecked
        {
            // Hash UTF-16 code units (two bytes each, little-endian order).
            for (int i = 0; i < s.Length; i++)
            {
                ushort c = s[i];
                hash ^= (byte)(c & 0xFF);
                hash *= FnvPrime;
                hash ^= (byte)(c >> 8);
                hash *= FnvPrime;
            }
        }
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bytes(ReadOnlySpan<byte> b)
    {
        ulong hash = FnvOffsetBasis;
        unchecked
        {
            for (int i = 0; i < b.Length; i++)
            {
                hash ^= b[i];
                hash *= FnvPrime;
            }
        }
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Combine(ulong a, ulong b)
    {
        unchecked { return (a ^ b) * FnvPrime; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int StringInt32(string s) => unchecked((int)String(s));
}
