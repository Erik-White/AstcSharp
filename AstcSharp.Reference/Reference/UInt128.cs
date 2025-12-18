// Minimal UInt128Ex helper used by PhysicalAstcBlock parsing.
using System;

namespace AstcSharp.Reference
{
    public readonly struct UInt128Ex : IEquatable<UInt128Ex>
    {
        public readonly ulong Low;
        public readonly ulong High;

        public static readonly UInt128Ex Zero = new UInt128Ex(0UL);

        public UInt128Ex(ulong low)
        {
            Low = low;
            High = 0UL;
        }

        public UInt128Ex(ulong low, ulong high)
        {
            Low = low;
            High = high;
        }

        public static UInt128Ex FromBytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 16) throw new ArgumentException("Need 16 bytes");
            // Restore original mapping: first 8 bytes -> Low, next 8 bytes -> High
            ulong low = BitConverter.ToUInt64(bytes.Slice(0, 8));
            ulong high = BitConverter.ToUInt64(bytes.Slice(8, 8));
            return new UInt128Ex(low, high);
        }

        public override string ToString() => $"0x{High:X16}{Low:X16}";

        public bool Equals(UInt128Ex other) => Low == other.Low && High == other.High;
        public override bool Equals(object? obj) => obj is UInt128Ex o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Low, High);

        public static bool operator ==(UInt128Ex a, UInt128Ex b) => a.Equals(b);
        public static bool operator !=(UInt128Ex a, UInt128Ex b) => !a.Equals(b);

        public static UInt128Ex operator |(UInt128Ex a, UInt128Ex b) => new UInt128Ex(a.Low | b.Low, a.High | b.High);
        public static UInt128Ex operator &(UInt128Ex a, UInt128Ex b) => new UInt128Ex(a.Low & b.Low, a.High & b.High);
        public static UInt128Ex operator ^(UInt128Ex a, UInt128Ex b) => new UInt128Ex(a.Low ^ b.Low, a.High ^ b.High);

        public static UInt128Ex operator <<(UInt128Ex value, int shift)
        {
            shift &= 127;
            if (shift == 0) return value;
            if (shift < 64)
            {
                ulong newHigh = (value.High << shift) | (value.Low >> (64 - shift));
                ulong newLow = value.Low << shift;
                return new UInt128Ex(newLow, newHigh);
            }
            else
            {
                int s = shift - 64;
                ulong newHigh = value.Low << s;
                return new UInt128Ex(0UL, newHigh);
            }
        }

        public static UInt128Ex operator >>(UInt128Ex value, int shift)
        {
            shift &= 127;
            if (shift == 0) return value;
            if (shift < 64)
            {
                ulong newLow = (value.Low >> shift) | (value.High << (64 - shift));
                ulong newHigh = value.High >> shift;
                return new UInt128Ex(newLow, newHigh);
            }
            else
            {
                int s = shift - 64;
                ulong newLow = value.High >> s;
                return new UInt128Ex(newLow, 0UL);
            }
        }

        public static UInt128Ex FromUlong(ulong v) => new UInt128Ex(v);

        // Return mask with lowest 'n' bits set to 1.
        public static UInt128Ex OnesMask(int n)
        {
            if (n <= 0) return new UInt128Ex(0UL, 0UL);
            if (n >= 128) return new UInt128Ex(~0UL, ~0UL);
            if (n <= 64)
            {
                ulong low = (n == 64) ? ~0UL : ((1UL << n) - 1UL);
                return new UInt128Ex(low, 0UL);
            }
            else
            {
                int highBits = n - 64;
                ulong low = ~0UL;
                ulong high = (highBits == 64) ? ~0UL : ((1UL << highBits) - 1UL);
                return new UInt128Ex(low, high);
            }
        }

        private static ulong ReverseBits64(ulong x)
        {
            // Standard bit-twiddling reverse for 64-bit
            x = ((x >> 1) & 0x5555555555555555UL) | ((x & 0x5555555555555555UL) << 1);
            x = ((x >> 2) & 0x3333333333333333UL) | ((x & 0x3333333333333333UL) << 2);
            x = ((x >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((x & 0x0F0F0F0F0F0F0F0FUL) << 4);
            x = ((x >> 8) & 0x00FF00FF00FF00FFUL) | ((x & 0x00FF00FF00FF00FFUL) << 8);
            x = ((x >> 16) & 0x0000FFFF0000FFFFUL) | ((x & 0x0000FFFF0000FFFFUL) << 16);
            x = (x >> 32) | (x << 32);
            return x;
        }

        // Reverse bits across the full 128-bit value
        public static UInt128Ex ReverseBits(UInt128Ex v)
        {
            // Reverse each 64-bit word then swap positions
            ulong revLow = ReverseBits64(v.Low);
            ulong revHigh = ReverseBits64(v.High);
            return new UInt128Ex(revHigh, revLow);
        }
    }
}
