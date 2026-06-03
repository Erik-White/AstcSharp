// Portions of this file are derived from Basis Universal
// (https://github.com/BinomialLLC/basis_universal), Copyright (c) 2019-2024
// Binomial LLC, licensed under the Apache License, Version 2.0

namespace AstcSharp.Uastc;

/// <summary>
/// UASTC subset partition patterns (texel -> subset index) and per-subset anchor texel indices,
/// selected by the per-mode 4- or 5-bit pattern field. UASTC uses its own small pattern tables
/// (shared with BC7), not the ASTC partition hash.
/// </summary>
internal static class UastcPartitionTables
{
    public const int Patterns2Count = 30;
    public const int Patterns3Count = 11;
    public const int Bc73Astc2Patterns2Count = 19;

    /// <summary>
    /// 2-subset patterns (modes 2, 4, 9, 16): 30 entries of 16 texel-subset indices.
    /// </summary>
    public static readonly byte[][] Patterns2 =
    [
        [0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1], [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1], [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1],
        [1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0, 0], [0, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1], [1, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0], [1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 0],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1], [1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1], [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0],
        [1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0],
        [1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1], [1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 1], [0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0], [0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0],
        [0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0], [1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, 0, 1, 1], [1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0], [0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0],
        [1, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 0, 1, 1], [0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0], [1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1], [1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0],
        [1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0], [1, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 0],
    ];

    /// <summary>
    /// 3-subset patterns (mode 3): 11 entries.
    /// </summary>
    public static readonly byte[][] Patterns3 =
    [
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2], [1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 2, 2, 2, 2], [1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2], [1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0],
        [1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0], [0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2], [0, 2, 1, 1, 0, 2, 1, 1, 0, 2, 1, 1, 0, 2, 1, 1], [2, 0, 0, 0, 2, 0, 0, 0, 2, 1, 1, 1, 2, 1, 1, 1],
        [2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2], [1, 1, 1, 1, 0, 0, 0, 0, 2, 2, 2, 2, 1, 1, 1, 1], [0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2],
    ];

    /// <summary>
    /// 2-subset patterns for mode 7 (BC7 3-subset / ASTC 2-subset shared): 19 entries.
    /// </summary>
    public static readonly byte[][] Bc73Astc2Patterns2 =
    [
        [0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0], [1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1],
        [1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1], [0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0], [0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], [0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1],
        [1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0], [0, 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 0], [1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0],
        [0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1], [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0], [1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0],
        [1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0], [0, 0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 0], [1, 1, 1, 1, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0],
    ];

    /// <summary>
    /// Per-subset anchor texel indices for <see cref="Patterns2"/>.
    /// </summary>
    public static readonly byte[][] Pattern2Anchors =
    [
        [0, 2], [0, 3], [1, 0], [0, 3], [7, 0], [0, 2], [3, 0], [7, 0], [0, 11], [2, 0], [0, 7], [11, 0], [3, 0], [8, 0], [0, 4], [12, 0],
        [1, 0], [8, 0], [0, 1], [0, 2], [0, 4], [8, 0], [1, 0], [0, 2], [4, 0], [0, 1], [4, 0], [1, 0], [4, 0], [1, 0],
    ];

    /// <summary>
    /// Per-subset anchor texel indices for <see cref="Patterns3"/>.
    /// </summary>
    public static readonly byte[][] Pattern3Anchors =
    [
        [0, 8, 10], [8, 0, 12], [4, 0, 12], [8, 0, 4], [3, 0, 2], [0, 1, 3], [0, 2, 1], [1, 9, 0], [1, 2, 0], [4, 0, 8], [0, 6, 2],
    ];

    /// <summary>
    /// Per-subset anchor texel indices for <see cref="Bc73Astc2Patterns2"/>.
    /// </summary>
    public static readonly byte[][] Bc73Astc2Patterns2Anchors =
    [
        [0, 4], [0, 2], [2, 0], [0, 7], [8, 0], [0, 1], [0, 3], [0, 1], [2, 0], [0, 1], [0, 8], [2, 0], [0, 1], [0, 7], [12, 0], [2, 0], [9, 0], [0, 2], [4, 0],
    ];

    // Per-texel subset maps as int[] (the shape the pixel-write seam consumes), precomputed once
    // from the byte tables so the hot decode path passes them directly with no per-block widening.
    private static readonly int[][] Patterns2Int = ToInt(Patterns2);
    private static readonly int[][] Patterns3Int = ToInt(Patterns3);
    private static readonly int[][] Bc73Astc2Patterns2Int = ToInt(Bc73Astc2Patterns2);

    /// <summary>
    /// Single-subset map: every texel in subset 0 (shared, never mutated).
    /// </summary>
    public static readonly int[] SingleSubset = new int[16];

    /// <summary>
    /// Single-subset anchor texel indices (texel 0).
    /// </summary>
    public static readonly byte[] SingleSubsetAnchors = [0];

    /// <summary>
    /// Resolves the partition pattern (texel to subset, as an int map for the pixel-write seam)
    /// and per-subset anchor indices for a partitioned mode. Returns false if the pattern index
    /// is out of range.
    /// </summary>
    public static bool TryGet(int mode, int subsets, int patternIndex, out int[] pattern, out byte[] anchors)
    {
        // Pick the partition set: 3-subset (mode 3), mode 7's BC7-derived 2-subset set, or the
        // standard 2-subset set (modes 2, 4, 9, 16).
        (int[][] patterns, byte[][] patternAnchors) = (subsets, mode) switch
        {
            (3, _) => (Patterns3Int, Pattern3Anchors),
            (_, 7) => (Bc73Astc2Patterns2Int, Bc73Astc2Patterns2Anchors),
            _ => (Patterns2Int, Pattern2Anchors),
        };

        if ((uint)patternIndex >= (uint)patterns.Length)
        {
            pattern = [];
            anchors = [];
            return false;
        }

        pattern = patterns[patternIndex];
        anchors = patternAnchors[patternIndex];
        return true;
    }

    private static int[][] ToInt(byte[][] patterns)
    {
        int[][] result = new int[patterns.Length][];
        for (int p = 0; p < patterns.Length; p++)
        {
            result[p] = Array.ConvertAll(patterns[p], b => (int)b);
        }

        return result;
    }
}
