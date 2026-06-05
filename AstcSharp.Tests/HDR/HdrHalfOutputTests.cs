using AstcSharp.IO;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Tests.HDR;

/// <summary>
/// Verifies the FP16 (<see cref="Half"/>) HDR output path. The <see cref="Half"/> result is
/// produced by narrowing the validated float HDR output, so it must equal <c>(Half)</c> of the
/// float output channel-for-channel — exactly, no tolerance (ASTC spec §C.2.15, §C.2.23).
/// </summary>
public class HdrHalfOutputTests
{
    [Theory]
    [InlineData(TestData.Astc.Hdr.Hdr_A_1x1)]
    [InlineData(TestData.Astc.Hdr.Hdr_Tile)]
    [InlineData(TestData.Astc.Hdr.Ldr_A_1x1)]
    [InlineData(TestData.Astc.Hdr.Ldr_Tile)]
    [InlineData(TestData.Astc.Hdr.Hdr_Mixed_256_4x4)]
    [InlineData(TestData.Astc.Hdr.Hdr_Mixed_256_8x8)]
    public void DecompressHdrImageHalf_MatchesNarrowedFloatOutput(string inputFile)
    {
        byte[] astcData = File.ReadAllBytes(TestFile.GetInputFileFullPath(Path.Combine("Astc", inputFile)));
        AstcFile astcFile = AstcFile.FromMemory(astcData);

        float[] floatResult = StreamCodec.DecodeHdr(
            astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint);
        Half[] halfResult = StreamCodec.DecodeHdrHalf(
            astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint);

        Assert.Equal(floatResult.Length, halfResult.Length);
        for (int i = 0; i < floatResult.Length; i++)
        {
            // Bit-exact: narrowing is the only operation between the two outputs.
            Assert.Equal(BitConverter.HalfToUInt16Bits((Half)floatResult[i]), BitConverter.HalfToUInt16Bits(halfResult[i]));
        }
    }
}
