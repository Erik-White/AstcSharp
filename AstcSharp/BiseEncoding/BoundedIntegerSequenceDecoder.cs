using AstcSharp.IO;

namespace AstcSharp.BiseEncoding;

internal class BoundedIntegerSequenceDecoder : BoundedIntegerSequenceCodec
{
    public BoundedIntegerSequenceDecoder(int range) : base(range) { }

    public List<int> Decode(int valuesCount, ref BitStream bitSource)
    {
        int tritsCount = (_encoding == EncodingMode.TritEncoding) ? 1 : 0;
        int quintsCount = (_encoding == EncodingMode.QuintEncoding) ? 1 : 0;
        int totalBitsCount = GetBitCount(valuesCount, tritsCount, quintsCount, _bits);
        int bitsPerBlock = EncodedBlockSize();
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
                case EncodingMode.TritEncoding:
                    result.AddRange(DecodeISEBlock(3, blockBits, _bits));
                    break;
                case EncodingMode.QuintEncoding:
                    result.AddRange(DecodeISEBlock(5, blockBits, _bits));
                    break;
                case EncodingMode.BitEncoding:
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
