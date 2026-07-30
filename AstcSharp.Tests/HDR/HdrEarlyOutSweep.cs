using System.Buffers.Binary;
using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.Encoding;
using AstcSharp.Tests.Utils;
using Xunit.Abstractions;

namespace AstcSharp.Tests.HDR;

/// <summary>
/// Measurement harness (not an assertion test) for calibrating the HDR early-out threshold
/// (<see cref="HdrColorStrategy.EarlyOutPerSampleError"/>). For a set of thresholds and content
/// archetypes it encodes single blocks through <see cref="BlockEncoderCore"/> with a
/// <see cref="TunedHdrStrategy{TThreshold}"/>, then reports the reconstruction PSNR and whether the
/// multi-partition / dual-plane search actually ran (inferred from the chosen layout).
/// </summary>
public class HdrEarlyOutSweep
{
    private readonly ITestOutputHelper output;

    public HdrEarlyOutSweep(ITestOutputHelper output) => this.output = output;

    // Candidate thresholds spanning the plausible LNS-domain range, from "always run the full search"
    // (0) to the current shipped guess (4 * 256^2). Each needs its own type (see TunedHdrStrategy).
    // Full search (never early-out) vs. single-partition only (always early-out): the gap between the
    // two PSNRs is exactly what the multi-partition / dual-plane search buys for that content.
    private struct TFull : IEarlyOutThreshold { public static long Value => 0; }
    private struct TSingleOnly : IEarlyOutThreshold { public static long Value => long.MaxValue / 1024; }

    [Fact(Skip = "Calibration harness, not an assertion test. Run explicitly to re-tune " +
        "HdrColorStrategy.EarlyOutPerSampleError; read its ITestOutputHelper output.")]
    public void Sweep_ReportsFullVsSinglePartitionQuality()
    {
        FootprintType footprintType = FootprintType.Footprint8x8;
        Footprint footprint = Footprint.FromFootprintType(footprintType);

        (string Name, Half[] Pixels)[] content =
        [
            ("near-lossless", NearLossless(footprint.Width, footprint.Height)),
            ("gentle-ramp", GentleRamp(footprint.Width, footprint.Height)),
            ("smooth-gradient", SmoothGradient(footprint.Width, footprint.Height)),
            ("subtle-two-region", SubtleTwoRegion(footprint.Width, footprint.Height)),
            ("two-region", TwoRegion(footprint.Width, footprint.Height)),
            ("four-quadrant", FourQuadrant(footprint.Width, footprint.Height)),
            ("decorrelated-alpha", DecorrelatedAlpha(footprint.Width, footprint.Height)),
        ];

        this.output.WriteLine($"footprint={footprintType} (earlyOutError = threshold * {footprint.PixelCount * 4})");
        this.output.WriteLine("  content              singleErr        singlePSNR   fullPSNR   gain   fullLayout");
        foreach ((string name, Half[] pixels) in content)
        {
            Span<RgbaHdrColor> texels = GatherLnsTexels(pixels, footprint);

            UInt128 singleBlock = BlockEncoderCore.Encode<RgbaHdrColor, TunedHdrStrategy<TSingleOnly>>(texels, footprint);
            UInt128 fullBlock = BlockEncoderCore.Encode<RgbaHdrColor, TunedHdrStrategy<TFull>>(texels, footprint);

            double singlePsnr = Psnr(singleBlock, pixels, footprint);
            double fullPsnr = Psnr(fullBlock, pixels, footprint);
            long singleErr = ReconstructionError(singleBlock, texels, footprint);
            BlockInfo fullInfo = BlockModeDecoder.Decode(fullBlock);
            string layout = fullInfo.PartitionCount > 1 ? $"multi({fullInfo.PartitionCount})" : fullInfo.DualPlane.Enabled ? "dual-plane" : "single";

            this.output.WriteLine(
                $"  {name,-18} {singleErr,14:N0}   {singlePsnr,8:F2}   {fullPsnr,8:F2}   {fullPsnr - singlePsnr,5:F2}   {layout}");
        }
    }

    /// <summary>
    /// Total squared reconstruction error of an encoded block against the LNS-domain texels — the
    /// same quantity the search's early-out compares against <c>threshold * texelCount * 4</c>.
    /// </summary>
    private static long ReconstructionError(UInt128 block, ReadOnlySpan<RgbaHdrColor> lnsTexels, Footprint footprint)
    {
        byte[] blockBytes = new byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(blockBytes, block);
        float[] decoded = StreamCodec.DecodeHdr(blockBytes, footprint.Width, footprint.Height, footprint);

        long error = 0;
        for (int t = 0; t < lnsTexels.Length; t++)
        {
            for (int c = 0; c < 4; c++)
            {
                int decodedLns = Fp16.ToLns(BitConverter.HalfToUInt16Bits((Half)decoded[(t * 4) + c]));
                int sourceLns = c switch { 0 => lnsTexels[t].R, 1 => lnsTexels[t].G, 2 => lnsTexels[t].B, _ => lnsTexels[t].A };
                long diff = decodedLns - sourceLns;
                error += diff * diff;
            }
        }

        return error;
    }

    /// <summary>
    /// Gathers footprint texels from an FP16 image and converts each channel to the LNS domain the
    /// HDR search operates in — the same conversion <see cref="HdrBlockEncoder"/> performs.
    /// </summary>
    private static RgbaHdrColor[] GatherLnsTexels(Half[] pixels, Footprint footprint)
    {
        RgbaHdrColor[] texels = new RgbaHdrColor[footprint.PixelCount];
        for (int i = 0; i < texels.Length; i++)
        {
            ushort r = BitConverter.HalfToUInt16Bits(pixels[(i * 4) + 0]);
            ushort g = BitConverter.HalfToUInt16Bits(pixels[(i * 4) + 1]);
            ushort b = BitConverter.HalfToUInt16Bits(pixels[(i * 4) + 2]);
            ushort a = BitConverter.HalfToUInt16Bits(pixels[(i * 4) + 3]);
            texels[i] = new RgbaHdrColor(
                (ushort)Fp16.ToLns(r), (ushort)Fp16.ToLns(g), (ushort)Fp16.ToLns(b), (ushort)Fp16.ToLns(a));
        }

        return texels;
    }

    /// <summary>
    /// Decodes a single encoded block through the real decoder and returns log-space PSNR (on FP16
    /// bit patterns) against the source pixels.
    /// </summary>
    private static double Psnr(UInt128 block, Half[] pixels, Footprint footprint)
    {
        byte[] blockBytes = new byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(blockBytes, block);
        float[] decoded = StreamCodec.DecodeHdr(blockBytes, footprint.Width, footprint.Height, footprint);

        const double peak = 0x7BFF;
        double sumSquaredError = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            double o = BitConverter.HalfToUInt16Bits(pixels[i]);
            double d = BitConverter.HalfToUInt16Bits((Half)decoded[i]);
            double diff = d - o;
            sumSquaredError += diff * diff;
        }

        if (sumSquaredError == 0)
        {
            return double.PositiveInfinity;
        }

        double meanSquaredError = sumSquaredError / pixels.Length;
        return 10.0 * Math.Log10((peak * peak) / meanSquaredError);
    }

    // A single flat HDR colour with one LSB of dither: single-partition already fits to near-zero
    // error, so the extra search should add essentially nothing (the early-out's ideal case).
    private static Half[] NearLossless(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float d = ((x + y) & 1) * 0.01f;
                pixels[idx] = (Half)(2.0f + d);
                pixels[idx + 1] = (Half)(1.5f + d);
                pixels[idx + 2] = (Half)(1.0f + d);
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    // A very shallow chromatic ramp: low single-partition error, a mid-range data point between
    // near-lossless and the smooth gradient.
    private static Half[] GentleRamp(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)(x + y) / Math.Max(1, width + height - 2);
                pixels[idx] = (Half)(2.0f + (0.5f * t));
                pixels[idx + 1] = (Half)(1.5f + (0.25f * t));
                pixels[idx + 2] = (Half)(1.0f + (0.5f * t));
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    // A smooth chromatic HDR gradient: a single endpoint line fits well, so multi-partition should
    // add little — the case where the early-out should fire.
    private static Half[] SmoothGradient(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)(x + y) / Math.Max(1, width + height - 2);
                pixels[idx] = (Half)(1.0f + (3.0f * t));
                pixels[idx + 1] = (Half)(2.0f + (1.0f * t));
                pixels[idx + 2] = (Half)(3.0f - (2.0f * t));
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    // Two HDR regions that differ only slightly: single-partition error is moderate, so this is where
    // the threshold's exact value could plausibly decide whether the multi-partition search runs.
    private static Half[] SubtleTwoRegion(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                bool left = x < width / 2;
                pixels[idx] = (Half)(left ? 2.0f : 2.4f);
                pixels[idx + 1] = (Half)(left ? 1.5f : 1.3f);
                pixels[idx + 2] = (Half)(left ? 1.0f : 1.2f);
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    private static Half[] TwoRegion(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)y / Math.Max(1, height - 1);
                if (x < width / 2)
                {
                    pixels[idx] = (Half)4.0f; pixels[idx + 1] = (Half)(0.5f + (3.0f * t)); pixels[idx + 2] = (Half)0.5f;
                }
                else
                {
                    pixels[idx] = (Half)0.5f; pixels[idx + 1] = (Half)(0.5f + (3.0f * t)); pixels[idx + 2] = (Half)4.0f;
                }

                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    private static Half[] FourQuadrant(int width, int height)
    {
        (float R, float G, float B)[] quadrant = [(4.0f, 0.25f, 0.25f), (0.25f, 4.0f, 0.25f), (0.25f, 0.25f, 4.0f), (4.0f, 4.0f, 0.25f)];
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                int cell = ((y < height / 2) ? 0 : 2) + ((x < width / 2) ? 0 : 1);
                (float r, float g, float b) = quadrant[cell];
                pixels[idx] = (Half)r; pixels[idx + 1] = (Half)g; pixels[idx + 2] = (Half)b; pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    private static Half[] DecorrelatedAlpha(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)(x + y) / Math.Max(1, width + height - 2);
                Half up = (Half)(4.0f * t);
                pixels[idx] = up; pixels[idx + 1] = up; pixels[idx + 2] = up; pixels[idx + 3] = (Half)(4.0f * (1.0f - t));
            }
        }

        return pixels;
    }
}
