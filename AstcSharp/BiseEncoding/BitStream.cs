using AstcSharp.Core;

namespace AstcSharp.BiseEncoding;

/// <summary>
/// A simple bit stream used for reading/writing arbitrary-sized chunks.
/// </summary>
internal struct BitStream
{
    private ulong low;
    private ulong high;
    private uint dataSize; // number of valid bits in the 128-bit buffer

    public BitStream(ulong data = 0, uint dataSize = 0)
    {
        this.low = data;
        this.high = 0;
        this.dataSize = dataSize;
    }

    public BitStream(UInt128 data, uint dataSize)
    {
        this.low = data.Low();
        this.high = data.High();
        this.dataSize = dataSize;
    }

    public readonly uint Bits => this.dataSize;

    public void PutBits(ulong value, int size)
    {
        if (this.dataSize + (uint)size > BlockInfo.SizeInBits)
        {
            throw new InvalidOperationException("Not enough space in BitStream");
        }

        if (this.dataSize < UInt128Extensions.HalfBits)
        {
            int lowFree = (int)(UInt128Extensions.HalfBits - this.dataSize);
            if (size <= lowFree)
            {
                this.low |= (value & MaskFor(size)) << (int)this.dataSize;
            }
            else
            {
                this.low |= (value & MaskFor(lowFree)) << (int)this.dataSize;
                this.high |= (value >> lowFree) & MaskFor(size - lowFree);
            }
        }
        else
        {
            int shift = (int)(this.dataSize - UInt128Extensions.HalfBits);
            this.high |= (value & MaskFor(size)) << shift;
        }

        this.dataSize += (uint)size;
    }

    /// <summary>
    /// Attempt to retrieve the specified number of bits from the buffer as a <see cref="UInt128"/>.
    /// The buffer is shifted accordingly if successful.
    /// </summary>
    public bool TryGetBits(int count, out UInt128 bits)
    {
        UInt128? result = this.GetBitsUInt128(count);
        bits = result ?? default;

        return result is not null;
    }

    public bool TryGetBits(int count, out ulong bits)
    {
        if (count > this.dataSize)
        {
            bits = 0;
            return false;
        }

        bits = count switch
        {
            0 => 0,
            <= UInt128Extensions.HalfBits => this.low & MaskFor(count),
            _ => this.low
        };
        this.ShiftBuffer(count);

        return true;
    }

    public bool TryGetBits(int count, out uint bits)
    {
        if (count > this.dataSize)
        {
            bits = 0;
            return false;
        }

        bits = (uint)(count switch
        {
            0 => 0UL,
            <= UInt128Extensions.HalfBits => this.low & MaskFor(count),
            _ => this.low
        });
        this.ShiftBuffer(count);

        return true;
    }

    private static ulong MaskFor(int bits)
        => bits == UInt128Extensions.HalfBits
            ? ~0UL
            : ((1UL << bits) - 1UL);

    private UInt128? GetBitsUInt128(int count)
    {
        if (count > this.dataSize)
        {
            return null;
        }

        UInt128 result = count switch
        {
            0 => UInt128.Zero,
            <= UInt128Extensions.HalfBits => (UInt128)(this.low & MaskFor(count)),
            BlockInfo.SizeInBits => new UInt128(this.high, this.low),
            _ => new UInt128((count - UInt128Extensions.HalfBits == UInt128Extensions.HalfBits) ? this.high : (this.high & MaskFor(count - UInt128Extensions.HalfBits)), this.low)
        };

        this.ShiftBuffer(count);

        return result;
    }

    private void ShiftBuffer(int count)
    {
        // C# masks shift amounts to the width of the operand, so `ulong << 64` and `ulong >> 64`
        // are identity, not zero. Reading zero bits is a no-op; count >= 128 is special-cased
        // below to avoid polluting the low/high halves on boundary shifts.
        if (count == 0)
        {
            return;
        }

        if (count < UInt128Extensions.HalfBits)
        {
            this.low = (this.low >> count) | (this.high << (UInt128Extensions.HalfBits - count));
            this.high >>= count;
        }
        else if (count == UInt128Extensions.HalfBits)
        {
            this.low = this.high;
            this.high = 0;
        }
        else if (count < BlockInfo.SizeInBits)
        {
            this.low = this.high >> (count - UInt128Extensions.HalfBits);
            this.high = 0;
        }
        else
        {
            this.low = 0;
            this.high = 0;
        }

        this.dataSize -= (uint)count;
    }
}
