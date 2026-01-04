using AstcSharp.IO;

namespace AstcSharp.BiseEncoding;

internal class BoundedIntegerSequenceEncoder : BoundedIntegerSequenceCodec
{
    private readonly List<int> _values = [];

    public BoundedIntegerSequenceEncoder(int range) : base(range) { }
    public void AddValue(int val) => _values.Add(val);

    public void Encode(ref BitStream bitSink)
    {
        int tritsCount = (_encoding == EncodingMode.TritEncoding) ? 1 : 0;
        int quintsCount = (_encoding == EncodingMode.QuintEncoding) ? 1 : 0;
        int totalBitCount = GetBitCount(_values.Count, tritsCount, quintsCount, _bits);

        int index = 0;
        int bitsWrittenCount = 0;
        while (index < _values.Count)
        {
            switch (_encoding)
            {
                case EncodingMode.TritEncoding:
                    var trits = new List<int>();
                    for (int i = 0; i < 5; ++i)
                    {
                        if (index < _values.Count) trits.Add(_values[index++]);
                        else trits.Add(0);
                    }
                    EncodeISEBlock<int>(trits, _bits, ref bitSink, ref bitsWrittenCount, totalBitCount);
                    break;
                case EncodingMode.QuintEncoding:
                    var quints = new List<int>();
                    for (int i = 0; i < 3; ++i)
                    {
                        var value = index < _values.Count ? _values[index++] : 0;
                        quints.Add(value);
                    }
                    EncodeISEBlock<int>(quints, _bits, ref bitSink, ref bitsWrittenCount, totalBitCount);
                    break;
                case EncodingMode.BitEncoding:
                    bitSink.PutBits((uint)_values[index++], GetEncodedBlockSize());
                    break;
            }
        }
    }

    public void Reset() => _values.Clear();
}
