
using AstcSharp.Core;

// Port of astc-codec/src/base/bit_stream.h
namespace AstcSharp.IO;

// A simple bit stream used for reading/writing arbitrary-sized chunks.
internal class BitStream
{
    private ulong _low;
    private ulong _high;
    private uint _dataSize; // number of valid bits in the 128-bit buffer

    public BitStream(ulong data = 0, uint dataSize = 0)
    {
        _low = data;
        _high = 0;
        _dataSize = dataSize;
    }

    // New overload: initialize BitStream with a 128-bit value
    public BitStream(UInt128 data, uint dataSize)
    {
        _low = data.Low();
        _high = data.High();
        _dataSize = dataSize;
    }

    public uint Bits => _dataSize;

    private static ulong MaskFor(int bits)
        => bits == 64 ? ~0UL : ((1UL << bits) - 1UL);

    public void PutBits<T>(T x, int size) where T : unmanaged
    {
        // Convert to ulong via bit-cast using generic constraints.
        ulong value = 0;
        if (typeof(T) == typeof(uint)) value = (uint)(object)x;
        else if (typeof(T) == typeof(ulong)) value = (ulong)(object)x;
        else if (typeof(T) == typeof(ushort)) value = (ushort)(object)x;
        else if (typeof(T) == typeof(byte)) value = (byte)(object)x;
        else value = Convert.ToUInt64(x);

        if (_dataSize + (uint)size > 128)
            throw new InvalidOperationException("Not enough space in BitStream");

        // If all new bits fit into the low part
        if (_dataSize < 64)
        {
            int lowFree = (int)(64 - _dataSize);
            if (size <= lowFree)
            {
                _low |= (value & MaskFor(size)) << (int)_dataSize;
            }
            else
            {
                // split between low and high
                _low |= (value & MaskFor(lowFree)) << (int)_dataSize;
                _high |= (value >> lowFree) & MaskFor(size - lowFree);
            }
        }
        else
        {
            // all goes into high part
            int shift = (int)(_dataSize - 64);
            _high |= (value & MaskFor(size)) << shift;
        }

        _dataSize += (uint)size;
    }

    private UInt128? GetBitsUInt128(int count)
    {
        if (count > _dataSize)
            return null;

        UInt128 result = count switch
        {
            0 => UInt128.Zero,
            <= 64 => (UInt128)(_low & MaskFor(count)),
            128 => new UInt128(_high, _low),
            _ => new UInt128(
                (count - 64 == 64) ? _high : (_high & MaskFor(count - 64)),
                _low)
        };

        ShiftBuffer(count);
        return result;
    }

    public T? GetBits<T>(int count) where T : unmanaged
    {
        if (typeof(T) == typeof(UInt128))
        {
            var result = GetBitsUInt128(count);
            return result.HasValue ? (T)(object)result.Value : null;
        }

        if (count > _dataSize)
            return null;

        ulong value = count switch
        {
            0 => 0,
            <= 64 => _low & MaskFor(count),
            _ => _low
        };

        ShiftBuffer(count);
        object boxed = Convert.ChangeType(value, typeof(T));
        return (T)boxed;
    }

    private void ShiftBuffer(int count)
    {
        if (count < 64)
        {
            _low = (_low >> count) | (_high << (64 - count));
            _high = _high >> count;
        }
        else if (count == 64)
        {
            _low = _high;
            _high = 0;
        }
        else
        {
            _low = _high >> (count - 64);
            _high = 0;
        }

        _dataSize -= (uint)count;
    }
}
