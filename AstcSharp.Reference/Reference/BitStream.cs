// Port of astc-codec/src/base/bit_stream.h
namespace AstcSharp.Reference
{
    using System;

    // A simple bit stream used for reading/writing arbitrary-sized chunks.
    public class BitStream
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
        public BitStream(UInt128Ex data, uint dataSize)
        {
            _low = data.Low;
            _high = data.High;
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

        public bool GetBits<T>(int count, out T result) where T : unmanaged
        {
            // Special-case returning a UInt128Ex (the C# 128-bit helper struct)
            if (typeof(T) == typeof(UInt128Ex))
            {
                if (count <= _dataSize)
                {
                    UInt128Ex ures;
                    if (count == 0)
                    {
                        ures = UInt128Ex.Zero;
                    }
                    else if (count <= 64)
                    {
                        ulong lowPart = _low & MaskFor(count);
                        // Keep lowPart in Low for small counts
                        ures = new UInt128Ex(lowPart, 0UL);
                    }
                    else if (count == 128)
                    {
                        // Return natural ordering Low=_low, High=_high
                        ures = new UInt128Ex(_low, _high);
                    }
                    else
                    {
                        int highBits = count - 64;
                        ulong lowPart = _low;
                        ulong highPart = (highBits == 64) ? _high : (_high & MaskFor(highBits));
                        ures = new UInt128Ex(lowPart, highPart);
                    }

                    // shift the buffer right by `count` bits
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
                    else // count > 64
                    {
                        int c = count - 64;
                        _low = _high >> c;
                        _high = 0;
                    }

                    _dataSize -= (uint)count;
                    result = (T)(object)ures;
                    return true;
                }

                result = default;
                return false;
            }

            if (count <= _dataSize)
            {
                // extract the lowest `count` bits from the 128-bit buffer
                ulong value;
                if (count == 0)
                {
                    value = 0;
                }
                else if (count <= 64)
                {
                    value = _low & MaskFor(count);
                }
                else
                {
                    int highBits = count - 64;
                    ulong lowPart = _low;
                    ulong highPart = _high & MaskFor(highBits);
                    // When count > 64 we cannot represent the full range in a
                    // single ulong. We still return the low 64 bits in this
                    // implementation because callers only request up to 64 bits.
                    value = lowPart | (highPart << 0); // highPart contribution ignored beyond 64 bits
                }

                // shift the buffer right by `count` bits
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
                else // count > 64
                {
                    int c = count - 64;
                    _low = _high >> c;
                    _high = 0;
                }

                _dataSize -= (uint)count;
                object boxed = Convert.ChangeType(value, typeof(T));
                result = (T)boxed;
                return true;
            }

            result = default;
            return false;
        }
    }
}
