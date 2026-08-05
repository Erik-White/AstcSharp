// Portions of this file are derived from Basis Universal
// (https://github.com/BinomialLLC/basis_universal), Copyright (c) 2016-2026
// Binomial LLC, licensed under the Apache License, Version 2.0.

namespace AstcSharp.Uastc;

/// <summary>
/// LSB-first bit reader over a 16-byte UASTC block. Bits are consumed least-significant-bit
/// first within each byte, bytes in ascending order — matching the Basis Universal transcoder's
/// block bit layout (which differs from ASTC's weight bit order).
/// </summary>
internal ref struct UastcBitReader
{
    private readonly ReadOnlySpan<byte> bytes;
    private int bitOffset;

    public UastcBitReader(ReadOnlySpan<byte> block)
    {
        bytes = block;
        bitOffset = 0;
    }

    /// <summary>
    /// Gets the current bit position.
    /// </summary>
    public readonly int BitOffset => bitOffset;

    /// <summary>
    /// Reads a single bit.
    /// </summary>
    public int ReadBit()
    {
        int b = (bytes[bitOffset >> 3] >> (bitOffset & 7)) & 1;
        bitOffset++;
        return b;
    }

    /// <summary>
    /// Reads up to 32 bits LSB-first. Spans byte boundaries.
    /// </summary>
    public uint ReadBits(int count)
    {
        uint result = 0;
        int read = 0;
        while (read < count)
        {
            int byteBitOffset = bitOffset & 7;
            int bitsToRead = Math.Min(count - read, 8 - byteBitOffset);
            uint byteBits = (uint)(bytes[bitOffset >> 3] >> byteBitOffset) & ((1u << bitsToRead) - 1u);
            result |= byteBits << read;
            read += bitsToRead;
            bitOffset += bitsToRead;
        }

        return result;
    }

    /// <summary>
    /// Advances the read position by <paramref name="count"/> bits without returning them.
    /// </summary>
    public void Skip(int count) => bitOffset += count;
}
