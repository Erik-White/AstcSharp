using AstcSharp.IO;

namespace AstcSharp.BiseEncoding;

internal class BoundedIntegerSequenceDecoder : BoundedIntegerSequenceCodec
{
    public BoundedIntegerSequenceDecoder(int range) : base(range) { }

    public List<int> Decode(int valuesCount, ref BitStream bitSource)
    {
        int totalBitsCount = GetBitCount(_encoding, valuesCount, _bits);
        int bitsPerBlock = GetEncodedBlockSize();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bitsPerBlock, 64);

        int bitsRemaining = totalBitsCount;
        var result = new List<int>();

        while (bitsRemaining > 0)
        {
            int toRead = Math.Min(bitsRemaining, bitsPerBlock);
            bool ok = bitSource.GetBits<ulong>(toRead, out var blockBits);
            if (!ok) throw new InvalidOperationException();
            switch (_encoding)
            {
                case BiseEncodingMode.TritEncoding:
                    result.AddRange(DecodeISEBlock(3, blockBits, _bits));
                    break;
                case BiseEncodingMode.QuintEncoding:
                    result.AddRange(DecodeISEBlock(5, blockBits, _bits));
                    break;
                case BiseEncodingMode.BitEncoding:
                    result.Add((int)blockBits);
                    break;
            }
            bitsRemaining -= bitsPerBlock;
        }

        if (result.Count < valuesCount) throw new InvalidOperationException();
        result.RemoveRange(valuesCount, result.Count - valuesCount);
        
        return result;
    }
}
