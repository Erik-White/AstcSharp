using System.Buffers.Binary;
using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.BlockDecoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.Encoding;
using AstcSharp.IO;
using AstcSharp.Reference.Tests.Utils;
using AstcSharp.Tests.Utils;
using AwesomeAssertions;
using Xunit.Abstractions;

namespace AstcSharp.Reference.Tests;

/// <summary>
/// Quality validation of the HDR encoder on real (non-synthetic) content: a real mixed LDR/HDR
/// fixture is decoded to FP16, a crop is re-encoded with both our encoder and ARM's, and the
/// reconstructions are compared. Unlike the synthetic-archetype quality test, this measures the
/// encoder against the mix of block content a real image contains.
/// </summary>
public class HdrRealImageQualityTests
{
    // A 64×64 crop of the 256×256 fixture: enough real block-content variety to be representative
    // while keeping the per-block search (≈1 ms/block) to a few hundred blocks per footprint.
    private const int CropSize = 64;

    // The real mixed LDR/HDR fixture (256×256). Decoded to FP16 once per test.
    private const string Fixture = "mixed-256-4x4";

    // Per-footprint log-PSNR floors for this real fixture: set ~1 dB below the achieved level of the
    // shipped encoder (endpoint refinement, bounded fixed-weight-proxy descent, the finer CEM 7
    // base+scale sub-modes, and the finer CEM 11 direct sub-modes), so a genuine quality regression
    // fails the build while normal variation does not. Achieved at time of recording: 4×4 85.53,
    // 6×6 66.66, 8×8 60.19, 10×10 59.52, 12×12 58.68 (4×4/6×6 lifted sharply by the finer CEM 11
    // sub-modes). Re-record when the encoder legitimately improves. The detailed our-vs-ARM report
    // lives in ProfileRealHdrImageQualityAndModeUsage.
    private static readonly Dictionary<FootprintType, double> MinLogPsnrDbByFootprint = new()
    {
        [FootprintType.Footprint4x4] = 84.0,
        [FootprintType.Footprint6x6] = 65.5,
        [FootprintType.Footprint8x8] = 59.0,
        [FootprintType.Footprint10x10] = 58.5,
        [FootprintType.Footprint12x12] = 57.5,
    };

    private readonly ITestOutputHelper output;

    public HdrRealImageQualityTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [MemberData(nameof(Footprints))]
    public void ReencodedRealHdrImage_AchievesQualityFloor(FootprintType footprintType)
    {
        // Re-encode a real mixed LDR/HDR crop and require our own decode to reconstruct it above the
        // footprint's known log-PSNR floor — guarding the shipped endpoint-refinement wins against
        // silent regression. The ARM comparison lives in ProfileRealHdrImageQualityAndModeUsage.
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        Half[] source = LoadFixtureCrop();

        double ourPsnr = OurRoundTripPsnr(source, footprint);
        double floor = MinLogPsnrDbByFootprint[footprintType];

        ourPsnr.Should().BeGreaterThanOrEqualTo(
            floor,
            because: $"[{footprintType}] our log-PSNR {ourPsnr:F2} dB should stay above the {floor} dB floor on real content");
    }

    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to measure HDR encode " +
        "wall-time (iterative-refinement + endpoint-polish cost).")]
    public void ProfileHdrEncodeTime()
    {
        Half[] source = LoadFixtureCrop();
        this.output.WriteLine($"fixture={Fixture} crop={CropSize}x{CropSize}");
        this.output.WriteLine("  footprint      encode(ms)   blocks   ms/block");

        foreach (FootprintType footprintType in ProfiledFootprints)
        {
            Footprint footprint = Footprint.FromFootprintType(footprintType);
            int blocksWide = (CropSize + footprint.Width - 1) / footprint.Width;
            int blocks = blocksWide * ((CropSize + footprint.Height - 1) / footprint.Height);

            // Warm up (JIT + caches), then time several encodes and take the best to reduce noise.
            StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
            double bestMs = double.MaxValue;
            for (int rep = 0; rep < 5; rep++)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
                double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                bestMs = Math.Min(bestMs, ms);
            }

            this.output.WriteLine($"  {footprintType,-14} {bestMs,9:F2}   {blocks,6}   {bestMs / blocks,8:F3}");
        }
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): reports, per footprint, our vs. ARM log-PSNR and the gap
    /// on the real fixture crop, plus the per-block endpoint-mode / layout histogram of our encode.
    /// Run explicitly to see where the encoder loses quality and which modes it actually uses.
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly and read its output to " +
        "profile HDR encode quality and mode usage on real content.")]
    public void ProfileRealHdrImageQualityAndModeUsage()
    {
        Half[] source = LoadFixtureCrop();
        this.output.WriteLine($"fixture={Fixture} crop={CropSize}x{CropSize}");
        this.output.WriteLine("  footprint      ourPSNR   armPSNR    gap   modeUsage");

        foreach (FootprintType footprintType in ProfiledFootprints)
        {
            var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
            Footprint footprint = Footprint.FromFootprintType(footprintType);

            byte[] ourEncoded = StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
            double ourPsnr = LogPsnr(source, StreamCodec.DecodeHdr(ourEncoded, CropSize, CropSize, footprint));
            double armPsnr = ArmRoundTripPsnr(source, blockX, blockY);
            string usage = SummariseModeUsage(ourEncoded);

            this.output.WriteLine($"  {footprintType,-14} {ourPsnr,7:F2}   {armPsnr,7:F2}   {ourPsnr - armPsnr,5:F2}   {usage}");
        }
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): for each footprint, encodes the real fixture crop with
    /// both our encoder and ARM's, decodes each block's config from both bitstreams, and reports how
    /// ARM's per-block choices differ from ours — average weight-grid size, weight range (weight
    /// precision), and layout mix. Answers whether ARM's higher PSNR comes from weight decisions our
    /// search does not reach (a fixable search gap) rather than from format features we lack.
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly and read its output to " +
        "compare our vs. ARM per-block weight/grid/layout choices on real content.")]
    public void CompareWeightConfigsAgainstReferenceEncoder()
    {
        Half[] source = LoadFixtureCrop();
        this.output.WriteLine($"fixture={Fixture} crop={CropSize}x{CropSize}");
        this.output.WriteLine("  footprint      | ours: avgGridWts avgWtRange layout | arm: avgGridWts avgWtRange layout");

        foreach (FootprintType footprintType in ProfiledFootprints)
        {
            var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
            Footprint footprint = Footprint.FromFootprintType(footprintType);

            byte[] ourEncoded = StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
            byte[] armEncoded = ReferenceDecoder.CompressHdr(source, CropSize, CropSize, blockX, blockY);

            this.output.WriteLine($"  {footprintType,-14} | ours: {SummariseWeightConfigs(ourEncoded)} | arm: {SummariseWeightConfigs(armEncoded)}");
        }
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): at 4×4, computes per-block PSNR for both our encode and
    /// ARM's over the fixture crop, and reports the distribution (how many blocks each encoder places
    /// in each PSNR bucket) plus the mean. Shows whether ARM's whole-crop advantage is uniform or
    /// concentrated in specific blocks — the question the aggregate PSNR hides.
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to see the per-block " +
        "PSNR distribution of our encode vs. ARM's on real content.")]
    public void ComparePerBlockPsnrDistribution()
    {
        Half[] source = LoadFixtureCrop();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        float[] oursDecoded = StreamCodec.DecodeHdr(StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint), CropSize, CropSize, footprint);
        byte[] armEncoded = ReferenceDecoder.CompressHdr(source, CropSize, CropSize, 4, 4);
        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(armEncoded, CropSize, CropSize, 4, 4));

        this.output.WriteLine($"fixture={Fixture} crop={CropSize}x{CropSize} 4x4 per-block PSNR distribution");
        this.output.WriteLine("  bucket(dB)   ours   arm");
        int[] oursHist = new int[6];
        int[] armHist = new int[6];
        double oursSum = 0, armSum = 0;
        int blocks = 0;
        for (int by = 0; by < CropSize; by += 4)
        {
            for (int bx = 0; bx < CropSize; bx += 4)
            {
                double ourP = BlockPsnr(source, oursDecoded, bx, by);
                double armP = BlockPsnr(source, armDecoded, bx, by);
                oursHist[Bucket(ourP)]++;
                armHist[Bucket(armP)]++;
                oursSum += ourP;
                armSum += armP;
                blocks++;
            }
        }

        string[] labels = ["<40", "40-50", "50-60", "60-70", "70-90", ">=90/inf"];
        for (int b = 0; b < labels.Length; b++)
        {
            this.output.WriteLine($"  {labels[b],-10} {oursHist[b],5}  {armHist[b],5}");
        }

        this.output.WriteLine($"  mean per-block: ours={oursSum / blocks:F2} arm={armSum / blocks:F2}");
    }

    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to confirm endpoint " +
        "coordinate-descent reduces reconstruction error (endpoint-search Stage 0).")]
    public void ConfirmEndpointSearch()
    {
        // Endpoint-search Stage 0 confirm: over every 4×4 block, run a bounded coordinate-descent over
        // the quantised endpoint colour values (single-partition, fine grid) and report baseline vs
        // searched reconstruction error, plus how many blocks the search improves and by how much. If
        // the search meaningfully lowers error on the worst blocks, endpoint search is the lever.
        Half[] source = LoadFixtureCrop();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        Span<RgbaHdrColor> texels = new RgbaHdrColor[footprint.PixelCount];
        int improved = 0, total = 0;
        long baseSum = 0, searchedSum = 0;
        long worstBaseline = 0, worstSearched = 0;
        for (int by = 0; by < CropSize; by += 4)
        {
            for (int bx = 0; bx < CropSize; bx += 4)
            {
                GatherLnsBlock(source, footprint, bx, by, texels);
                (long baseline, long searched) = BlockEncoderCore.EndpointSearchError<RgbaHdrColor, HdrColorStrategy>(
                    texels, footprint, gridWidth: 4, gridHeight: 4, weightRange: 3, radius: 2);
                total++;
                baseSum += baseline;
                searchedSum += searched;
                if (searched < baseline)
                {
                    improved++;
                }

                if (baseline > worstBaseline)
                {
                    worstBaseline = baseline;
                    worstSearched = searched;
                }
            }
        }

        this.output.WriteLine($"fixture={Fixture} 4x4 blocks={total} (single-partition, 4x4@r3, radius=2)");
        this.output.WriteLine($"  improved {improved}/{total}   baseSumSSE={baseSum:E3}   searchedSumSSE={searchedSum:E3}   reduction={100.0 * (baseSum - searchedSum) / baseSum:F1}%");
        this.output.WriteLine($"  worst block: baseline={worstBaseline:E3}  searched={worstSearched:E3}");
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): per-4×4-block SSE, ours vs ARM, sorted by our excess SSE
    /// over ARM. Reports the worst 20 blocks with their cumulative share of the total excess MSE, plus
    /// how many blocks account for 50% / 80% / 95% of it — quantifying how concentrated the aggregate
    /// PSNR gap is, and what layout ours/ARM use on the worst offenders.
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to see the per-4×4-block " +
        "SSE distribution ours vs ARM (gap concentration).")]
    public void ReportPerBlockSseDistribution()
    {
        Half[] source = LoadFixtureCrop();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        byte[] ourEncoded = StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
        float[] oursDecoded = StreamCodec.DecodeHdr(ourEncoded, CropSize, CropSize, footprint);
        byte[] armEncoded = ReferenceDecoder.CompressHdr(source, CropSize, CropSize, 4, 4);
        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(armEncoded, CropSize, CropSize, 4, 4));

        var rows = new List<(double Excess, double OurSse, double ArmSse, string Ours, string Arm)>();
        double ourTotalSse = 0, armTotalSse = 0;
        int blockIndex = 0;
        for (int by = 0; by < CropSize; by += 4)
        {
            for (int bx = 0; bx < CropSize; bx += 4)
            {
                double ourSse = BlockSse(source, oursDecoded, bx, by);
                double armSse = BlockSse(source, armDecoded, bx, by);
                ourTotalSse += ourSse;
                armTotalSse += armSse;
                rows.Add((ourSse - armSse, ourSse, armSse, LayoutLabel(ourEncoded, blockIndex), LayoutLabel(armEncoded, blockIndex)));
                blockIndex++;
            }
        }

        double totalExcess = ourTotalSse - armTotalSse;
        rows.Sort((a, b) => b.Excess.CompareTo(a.Excess));

        int channels = BlockInfo.ChannelsPerPixel;
        long samplesPerBlock = 16L * channels;
        double peakSq = (double)0x7BFF * 0x7BFF;
        double ourPsnr = 10.0 * Math.Log10(peakSq / (ourTotalSse / (blockIndex * samplesPerBlock)));
        double armPsnr = 10.0 * Math.Log10(peakSq / (armTotalSse / (blockIndex * samplesPerBlock)));
        this.output.WriteLine($"fixture={Fixture} 4x4 blocks={blockIndex}  ourPSNR={ourPsnr:F2}  armPSNR={armPsnr:F2}  gap={ourPsnr - armPsnr:F2}");
        this.output.WriteLine($"  total excess MSE ours-over-ARM: {totalExcess:E3}");

        // Concentration: how many worst blocks account for 50/80/95% of the excess.
        double cum = 0;
        int b50 = -1, b80 = -1, b95 = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            cum += Math.Max(0, rows[i].Excess);
            double frac = cum / totalExcess;
            if (b50 < 0 && frac >= 0.50) { b50 = i + 1; }
            if (b80 < 0 && frac >= 0.80) { b80 = i + 1; }
            if (b95 < 0 && frac >= 0.95) { b95 = i + 1; }
        }

        this.output.WriteLine($"  blocks accounting for 50%/80%/95% of excess MSE: {b50} / {b80} / {b95}  (of {blockIndex})");
        this.output.WriteLine("  worst 20 by excess SSE:   ourSSE       armSSE     ourLayout            armLayout");
        cum = 0;
        foreach ((double excess, double ourSse, double armSse, string ours, string arm) in rows.Take(20))
        {
            cum += Math.Max(0, excess);
            this.output.WriteLine($"    {ourSse,12:E2} {armSse,12:E2}  ({100 * cum / totalExcess,4:F0}% cum)  {ours,-20} {arm}");
        }
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): per 4×4 block, dumps ARM's PSNR and exact layout
    /// (partition count, dual-plane, endpoint mode, grid, range) alongside ours, bucketed by ARM PSNR.
    /// Answers where ARM's high 4×4 PSNR comes from — single-partition blocks or a partitioned/
    /// dual-plane minority that dominates the aggregate — since a single endpoint line alone cannot
    /// reach ARM's ~82 dB at 4×4.
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to see ARM's per-4×4-block " +
        "layout vs PSNR.")]
    public void ProfileReferenceFourByFourHardBlocks()
    {
        Half[] source = LoadFixtureCrop();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        byte[] armEncoded = ReferenceDecoder.CompressHdr(source, CropSize, CropSize, 4, 4);
        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(armEncoded, CropSize, CropSize, 4, 4));
        float[] oursDecoded = StreamCodec.DecodeHdr(StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint), CropSize, CropSize, footprint);

        // Tally ARM's layout usage split by whether the block is "hard" (ARM PSNR < 70) or "easy".
        var layoutByBucket = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        int blockIndex = 0;
        int hard = 0;
        double armHardSum = 0, oursHardSum = 0;
        for (int by = 0; by < CropSize; by += 4)
        {
            for (int bx = 0; bx < CropSize; bx += 4)
            {
                UInt128 armBits = BinaryPrimitives.ReadUInt128LittleEndian(armEncoded.AsSpan(blockIndex * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes));
                BlockInfo armInfo = BlockModeDecoder.Decode(armBits);
                double armP = BlockPsnr(source, armDecoded, bx, by);
                double ourP = BlockPsnr(source, oursDecoded, bx, by);

                string layout = armInfo.IsVoidExtent ? "void"
                    : armInfo.PartitionCount > 1 ? $"multi{armInfo.PartitionCount}"
                    : armInfo.DualPlane.Enabled ? "dual"
                    : $"single:{armInfo.Weights.Width}x{armInfo.Weights.Height}@r{armInfo.Weights.Range}";
                string bucket = armP >= 70 ? "easy(>=70)" : "hard(<70)";
                string key = $"{bucket} {layout}";
                if (!layoutByBucket.TryGetValue(key, out int[]? counts))
                {
                    layoutByBucket[key] = counts = new int[1];
                }

                counts[0]++;

                if (armP < 70)
                {
                    hard++;
                    armHardSum += armP;
                    oursHardSum += ourP;
                }

                blockIndex++;
            }
        }

        this.output.WriteLine($"fixture={Fixture} 4x4 blocks={blockIndex} hard(ARM<70dB)={hard}");
        this.output.WriteLine("  ARM layout usage (bucket / layout : count):");
        foreach ((string key, int[] counts) in layoutByBucket)
        {
            this.output.WriteLine($"    {key} : {counts[0]}");
        }

        if (hard > 0)
        {
            this.output.WriteLine($"  on ARM's hard blocks: ARM mean={armHardSum / hard:F2} dB, ours mean={oursHardSum / hard:F2} dB");
        }
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): attributes our 4×4 deficit vs ARM. Sorts blocks by our
    /// per-block PSNR deficit (armP − ourP), prints the worst 20 with both encoders' layout, and reports
    /// how the total squared error we lose relative to ARM is distributed across our own layout choices.
    /// Answers whether the above-single-line-floor gap is a concentrated hard-block minority (a real
    /// modelling gap that partitioning/dual-plane would close) or spread across all blocks (endpoints/
    /// weights still, not layout).
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to attribute our 4×4 deficit " +
        "vs ARM by per-block layout.")]
    public void AttributeFourByFourDeficit()
    {
        Half[] source = LoadFixtureCrop();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        byte[] ourEncoded = StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
        float[] oursDecoded = StreamCodec.DecodeHdr(ourEncoded, CropSize, CropSize, footprint);
        byte[] armEncoded = ReferenceDecoder.CompressHdr(source, CropSize, CropSize, 4, 4);
        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(armEncoded, CropSize, CropSize, 4, 4));

        var blocks = new List<(double Deficit, double OurP, double ArmP, string Ours, string Arm)>();
        double totalDeficitSse = 0;
        var deficitByOurLayout = new SortedDictionary<string, double>(StringComparer.Ordinal);

        int blockIndex = 0;
        for (int by = 0; by < CropSize; by += 4)
        {
            for (int bx = 0; bx < CropSize; bx += 4)
            {
                double ourP = BlockPsnr(source, oursDecoded, bx, by);
                double armP = BlockPsnr(source, armDecoded, bx, by);
                string ourLayout = LayoutLabel(ourEncoded, blockIndex);
                string armLayout = LayoutLabel(armEncoded, blockIndex);
                blocks.Add((armP - ourP, ourP, armP, ourLayout, armLayout));

                // Excess squared error we emit over ARM on this block, attributed to our layout.
                double ourSse = BlockSse(source, oursDecoded, bx, by);
                double armSse = BlockSse(source, armDecoded, bx, by);
                double excess = Math.Max(0, ourSse - armSse);
                totalDeficitSse += excess;
                deficitByOurLayout[ourLayout] = deficitByOurLayout.GetValueOrDefault(ourLayout) + excess;

                blockIndex++;
            }
        }

        blocks.Sort((a, b) => b.Deficit.CompareTo(a.Deficit));
        this.output.WriteLine($"fixture={Fixture} 4x4 blocks={blockIndex}  worst-20 by deficit (armP - ourP):");
        this.output.WriteLine("    ourP    armP   ourLayout            armLayout");
        foreach ((double deficit, double ourP, double armP, string ours, string arm) in blocks.Take(20))
        {
            this.output.WriteLine($"    {ourP,6:F1}  {armP,6:F1}   {ours,-18}  {arm}");
        }

        this.output.WriteLine("  excess-SSE-vs-ARM attributed to OUR layout:");
        foreach ((string layout, double sse) in deficitByOurLayout.OrderByDescending(kv => kv.Value))
        {
            this.output.WriteLine($"    {layout,-18} {100.0 * sse / totalDeficitSse,5:F1}%");
        }
    }

    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to decode ARM's stored " +
        "endpoints on the worst 4×4 block and test whether our CEM 11 encoder can represent them.")]
    public void ProbeArmCem11EndpointsOnWorstFourByFourBlock()
    {
        Half[] source = LoadFixtureCrop();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        byte[] ourEncoded = StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
        float[] oursDecoded = StreamCodec.DecodeHdr(ourEncoded, CropSize, CropSize, footprint);
        byte[] armEncoded = ReferenceDecoder.CompressHdr(source, CropSize, CropSize, 4, 4);
        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(armEncoded, CropSize, CropSize, 4, 4));

        // Find the block where we lose the most SSE to ARM.
        int worstBlock = -1, worstBx = 0, worstBy = 0;
        double worstExcess = -1;
        int blockIndex = 0;
        for (int by = 0; by < CropSize; by += 4)
        {
            for (int bx = 0; bx < CropSize; bx += 4)
            {
                double excess = BlockSse(source, oursDecoded, bx, by) - BlockSse(source, armDecoded, bx, by);
                if (excess > worstExcess)
                {
                    worstExcess = excess;
                    worstBlock = blockIndex;
                    worstBx = bx;
                    worstBy = by;
                }

                blockIndex++;
            }
        }

        UInt128 armBits = BinaryPrimitives.ReadUInt128LittleEndian(armEncoded.AsSpan(worstBlock * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes));
        BlockInfo armInfo = BlockModeDecoder.Decode(armBits);
        this.output.WriteLine($"worst block idx={worstBlock} excessSSE={worstExcess:E3} arm layout={LayoutLabel(armEncoded, worstBlock)}");
        this.output.WriteLine($"  arm grid={armInfo.Weights.Width}x{armInfo.Weights.Height}@r{armInfo.Weights.Range} colorRange={armInfo.Colors.Range} mode={armInfo.EndpointMode0}");

        // Decode ARM's stored endpoints for this block, then re-encode them through our encoder for the
        // same mode and check whether we can represent them (the Lever-1 probe pattern, for CEM 11).
        int colorCount = armInfo.EndpointMode0.GetColorValuesCount();
        Span<int> colors = stackalloc int[colorCount];
        FusedBlockDecoder.DecodeBiseValues(armBits, armInfo.Colors.StartBit, armInfo.Colors.BitCount, armInfo.Colors.Range, colorCount, colors);
        Quantization.UnquantizeCEValuesBatch(colors, armInfo.Colors.Range);
        ColorEndpointPair armPair = EndpointCodec.Decode(colors, armInfo.EndpointMode0);

        Span<int> reencoded = stackalloc int[colorCount];
        HdrEndpointEncoder.Encode(armInfo.EndpointMode0, armPair.HdrLow, armPair.HdrHigh, armInfo.Colors.Range, reencoded);
        Quantization.UnquantizeCEValuesBatch(reencoded, armInfo.Colors.Range);
        ColorEndpointPair ourPair = EndpointCodec.Decode(reencoded, armInfo.EndpointMode0);

        this.output.WriteLine($"  arm  low=({armPair.HdrLow.R},{armPair.HdrLow.G},{armPair.HdrLow.B}) high=({armPair.HdrHigh.R},{armPair.HdrHigh.G},{armPair.HdrHigh.B})");
        this.output.WriteLine($"  ours low=({ourPair.HdrLow.R},{ourPair.HdrLow.G},{ourPair.HdrLow.B}) high=({ourPair.HdrHigh.R},{ourPair.HdrHigh.G},{ourPair.HdrHigh.B})");
        bool representable = armPair.HdrLow.Equals(ourPair.HdrLow) && armPair.HdrHigh.Equals(ourPair.HdrHigh);
        this.output.WriteLine($"  representable={representable}");
    }

    private static string LayoutLabel(byte[] encoded, int blockIndex)
    {
        UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(blockIndex * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        return info.IsVoidExtent ? "void"
            : info.PartitionCount > 1 ? $"multi{info.PartitionCount}:{info.EndpointMode0}"
            : info.DualPlane.Enabled ? $"dual:{info.EndpointMode0}"
            : $"single:{info.EndpointMode0}";
    }

    private static double BlockSse(ReadOnlySpan<Half> source, ReadOnlySpan<float> decoded, int ox, int oy)
    {
        double sse = 0;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int idx = (((oy + y) * CropSize) + (ox + x)) * BlockInfo.ChannelsPerPixel;
                for (int c = 0; c < BlockInfo.ChannelsPerPixel; c++)
                {
                    double o = BitConverter.HalfToUInt16Bits(source[idx + c]);
                    double d = BitConverter.HalfToUInt16Bits((Half)decoded[idx + c]);
                    sse += (o - d) * (o - d);
                }
            }
        }

        return sse;
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): at 4×4 with optimal lattice weights, sweeps *idealised*
    /// endpoint precision — each 16-bit LNS endpoint channel rounded to the top <c>bits</c> bits — from
    /// the ~8-ish precision the current HDR RGB-direct fields give up to full 16-bit, reporting the
    /// resulting FP16 PSNR per precision. Confirms whether the 11 dB endpoint-quantisation loss is
    /// genuinely recoverable by finer endpoint fields (and how many bits it needs) — i.e. whether
    /// implementing a finer HDR endpoint sub-mode is the lever, before any is built. This models an
    /// idealised finer field; a real sub-mode would land at or below the curve for its bit width.
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to see the endpoint-precision " +
        "recovery curve at 4×4.")]
    public void ReportEndpointPrecisionRecovery()
    {
        Half[] source = LoadFixtureCrop();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        const int channels = 4;
        const int weightRange = 31;

        this.output.WriteLine($"fixture={Fixture} crop={CropSize}x{CropSize} footprint=4x4 range={weightRange}");
        this.output.WriteLine("  endpointBits   PSNR(dB)");
        foreach (int bits in new[] { 8, 9, 10, 11, 12, 14, 16 })
        {
            double sse = EndpointPrecisionSweepSse(source, footprint, weightRange, channels, bits, out long samples);
            this.output.WriteLine($"  {bits,10}     {Fp16Psnr(sse, samples),8:F2}");
        }
    }

    /// <summary>
    /// FP16-domain squared error over the 4×4 crop at optimal lattice weights, with each endpoint
    /// channel rounded to the top <paramref name="bits"/> bits of its 16-bit LNS value — an idealised
    /// model of a finer endpoint field. The endpoints are the block's min/max projection on the LNS
    /// line, rounded to the given precision.
    /// </summary>
    private static double EndpointPrecisionSweepSse(
        Half[] source, Footprint footprint, int weightRange, int channels, int bits, out long samples)
    {
        double sse = 0;
        samples = 0;
        int dropBits = 16 - bits;
        int roundHalf = dropBits > 0 ? 1 << (dropBits - 1) : 0;

        Span<int> unquant = stackalloc int[weightRange + 1];
        for (int q = 0; q <= weightRange; q++)
        {
            unquant[q] = AstcSharp.BiseEncoding.Quantize.Quantization.UnquantizeWeightFromRange(q, weightRange);
        }

        Span<RgbaHdrColor> texels = new RgbaHdrColor[footprint.PixelCount];
        Span<double> mean = stackalloc double[channels];
        Span<double> axis = stackalloc double[channels];
        Span<int> low = stackalloc int[channels];
        Span<int> high = stackalloc int[channels];
        for (int by = 0; by < CropSize; by += footprint.Height)
        {
            for (int bx = 0; bx < CropSize; bx += footprint.Width)
            {
                GatherLnsBlock(source, footprint, bx, by, texels);
                FitLnsLine(texels, channels, mean, axis);

                double minDot = double.MaxValue, maxDot = double.MinValue;
                foreach (RgbaHdrColor t in texels)
                {
                    double dot = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        dot += (LnsChannel(t, c) - mean[c]) * axis[c];
                    }

                    minDot = Math.Min(minDot, dot);
                    maxDot = Math.Max(maxDot, dot);
                }

                for (int c = 0; c < channels; c++)
                {
                    low[c] = RoundToBits((int)Math.Clamp(Math.Round(mean[c] + (minDot * axis[c])), 0, 0xFFFF), dropBits, roundHalf);
                    high[c] = RoundToBits((int)Math.Clamp(Math.Round(mean[c] + (maxDot * axis[c])), 0, 0xFFFF), dropBits, roundHalf);
                }

                foreach (RgbaHdrColor t in texels)
                {
                    long best = long.MaxValue;
                    for (int q = 0; q <= weightRange; q++)
                    {
                        int w = unquant[q];
                        long err = 0;
                        for (int c = 0; c < channels; c++)
                        {
                            int reconLns = Interpolation.BlendWeighted(low[c], high[c], w);
                            double df = Fp16.FromLns(Math.Clamp(reconLns, 0, 0xFFFF)) - Fp16.FromLns((int)LnsChannel(t, c));
                            err += (long)(df * df);
                        }

                        best = Math.Min(best, err);
                    }

                    sse += best;
                    samples += channels;
                }
            }
        }

        return sse;
    }

    private static int RoundToBits(int value, int dropBits, int roundHalf)
    {
        if (dropBits <= 0)
        {
            return value;
        }

        int rounded = ((value + roundHalf) >> dropBits) << dropBits;
        return Math.Min(rounded, 0xFFFF);
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): at 4×4 (undecimated — each texel owns one grid weight, so
    /// weight selection is independent per texel and free of grid coupling), isolates the loss below
    /// the single-line floor into weight quantisation vs. error-metric domain. All PSNRs use the ideal
    /// continuous endpoint line (endpoint quantisation excluded) and are reported in the FP16 domain:
    /// <list type="bullet">
    /// <item>contFloor — continuous projection weight (the line's ceiling).</item>
    /// <item>lnsPick — each texel's lattice weight chosen to minimise LNS-domain error (what our search
    /// optimises).</item>
    /// <item>fp16Pick — each texel's lattice weight chosen to minimise FP16-domain error (what PSNR
    /// rewards).</item>
    /// </list>
    /// <c>fp16Pick − lnsPick</c> is the recoverable headroom from scoring the search in FP16 instead of
    /// LNS — the metric-mismatch hypothesis. If it is near zero, the metric is not the lever.
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to decompose the below-floor " +
        "loss into weight quantisation, error-metric domain, and endpoint quantisation (4×4).")]
    public void DecomposeWeightQuantisationAndMetricDomain()
    {
        Half[] source = LoadFixtureCrop();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        const int channels = 4;
        const int weightRange = 31; // ARM's chosen 4×4 range; the finest single-plane lattice.

        (double lnsSse, double fp16Sse) = LatticeWeightErrors(source, footprint, weightRange, channels, out double contSse, out double quantEpSse, out long samples);

        this.output.WriteLine($"fixture={Fixture} crop={CropSize}x{CropSize} footprint=4x4 range={weightRange}");
        this.output.WriteLine($"  contFloor = {Fp16Psnr(contSse, samples):F2} dB  (continuous weight + continuous endpoints)");
        this.output.WriteLine($"  lnsPick   = {Fp16Psnr(lnsSse, samples):F2} dB  (optimal lattice weight, LNS criterion — our search)");
        this.output.WriteLine($"  fp16Pick  = {Fp16Psnr(fp16Sse, samples):F2} dB  (optimal lattice weight, FP16 criterion — PSNR-optimal)");
        this.output.WriteLine($"  quantEp   = {Fp16Psnr(quantEpSse, samples):F2} dB  (optimal lattice weight + CEM-quantised endpoints)");
        this.output.WriteLine($"  metric headroom (fp16Pick - lnsPick) = {Fp16Psnr(fp16Sse, samples) - Fp16Psnr(lnsSse, samples):F2} dB");
        this.output.WriteLine($"  endpoint-quant cost (fp16Pick - quantEp) = {Fp16Psnr(fp16Sse, samples) - Fp16Psnr(quantEpSse, samples):F2} dB");
    }

    /// <summary>
    /// Second pass for <see cref="DecomposeWeightQuantisationAndMetricDomain"/>: for each 4×4 block,
    /// fit the continuous LNS line, take its endpoint pair (min/max projection), and for each texel try
    /// every lattice weight in [0, <paramref name="weightRange"/>], accumulating FP16-domain squared
    /// error for the continuous-weight floor and for the LNS- and FP16-error-minimising lattice picks.
    /// </summary>
    private static (double LnsSse, double Fp16Sse) LatticeWeightErrors(
        Half[] source, Footprint footprint, int weightRange, int channels, out double contSse, out long samples)
        => LatticeWeightErrors(source, footprint, weightRange, channels, out contSse, out _, out samples);

    private static (double LnsSse, double Fp16Sse) LatticeWeightErrors(
        Half[] source, Footprint footprint, int weightRange, int channels, out double contSse, out double quantEpSse, out long samples)
    {
        contSse = 0;
        quantEpSse = 0;
        double lnsSse = 0, fp16Sse = 0;
        samples = 0;

        // Widest CE range: 8-bit-exact endpoint quantisation, so quantEp isolates the CEM 15 field-width
        // representation limit (not range quantisation) from weight selection.
        const int colorRange = 255;

        Span<int> unquant = stackalloc int[weightRange + 1];
        for (int q = 0; q <= weightRange; q++)
        {
            unquant[q] = AstcSharp.BiseEncoding.Quantize.Quantization.UnquantizeWeightFromRange(q, weightRange);
        }

        Span<RgbaHdrColor> texels = new RgbaHdrColor[footprint.PixelCount];
        Span<double> mean = stackalloc double[channels];
        Span<double> axis = stackalloc double[channels];
        Span<int> low = stackalloc int[channels];
        Span<int> high = stackalloc int[channels];
        Span<int> qLow = stackalloc int[channels];
        Span<int> qHigh = stackalloc int[channels];
        for (int by = 0; by < CropSize; by += footprint.Height)
        {
            for (int bx = 0; bx < CropSize; bx += footprint.Width)
            {
                GatherLnsBlock(source, footprint, bx, by, texels);
                FitLnsLine(texels, channels, mean, axis);

                // Endpoint pair = the min/max projection extents on the line, in LNS channels.
                double minDot = double.MaxValue, maxDot = double.MinValue;
                foreach (RgbaHdrColor t in texels)
                {
                    double dot = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        dot += (LnsChannel(t, c) - mean[c]) * axis[c];
                    }

                    minDot = Math.Min(minDot, dot);
                    maxDot = Math.Max(maxDot, dot);
                }

                for (int c = 0; c < channels; c++)
                {
                    low[c] = (int)Math.Clamp(Math.Round(mean[c] + (minDot * axis[c])), 0, 0xFFFF);
                    high[c] = (int)Math.Clamp(Math.Round(mean[c] + (maxDot * axis[c])), 0, 0xFFFF);
                }

                // Quantised endpoints: encode the same pair through the mode the encoder would pick for
                // this block (CEM 11 for opaque, CEM 15 when alpha varies) and decode it back — the
                // effective endpoints a real block would interpolate at colorRange 255.
                bool opaque = true;
                foreach (RgbaHdrColor t in texels)
                {
                    opaque &= t.A == Fp16.One;
                }

                ColorEndpointMode epMode = opaque ? ColorEndpointMode.HdrRgbDirect : ColorEndpointMode.HdrRgbDirectHdrAlpha;
                QuantizeEndpoints(epMode, low, high, colorRange, qLow, qHigh);

                foreach (RgbaHdrColor t in texels)
                {
                    // Continuous-weight floor: exact projection parameter mapped to [0,64].
                    double dot = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        dot += (LnsChannel(t, c) - mean[c]) * axis[c];
                    }

                    for (int c = 0; c < channels; c++)
                    {
                        double s = Fp16.FromLns((int)LnsChannel(t, c));
                        double cont = Fp16.FromLns((int)Math.Clamp(Math.Round(mean[c] + (dot * axis[c])), 0, 0xFFFF)) - s;
                        contSse += cont * cont;
                    }

                    // Try every lattice weight; track the LNS-error and FP16-error minimisers.
                    long bestLnsErr = long.MaxValue, bestFp16Err = long.MaxValue;
                    long lnsErrAtLnsPick = 0, fp16ErrAtFp16Pick = 0;
                    for (int q = 0; q <= weightRange; q++)
                    {
                        int w = unquant[q];
                        long lnsErr = 0, fp16Err = 0;
                        for (int c = 0; c < channels; c++)
                        {
                            int reconLns = Interpolation.BlendWeighted(low[c], high[c], w);
                            long dl = reconLns - (long)LnsChannel(t, c);
                            lnsErr += dl * dl;
                            double df = Fp16.FromLns(Math.Clamp(reconLns, 0, 0xFFFF)) - Fp16.FromLns((int)LnsChannel(t, c));
                            fp16Err += (long)(df * df);
                        }

                        if (lnsErr < bestLnsErr)
                        {
                            bestLnsErr = lnsErr;
                            lnsErrAtLnsPick = fp16Err; // FP16 error incurred by the LNS-optimal choice.
                        }

                        if (fp16Err < bestFp16Err)
                        {
                            bestFp16Err = fp16Err;
                            fp16ErrAtFp16Pick = fp16Err;
                        }
                    }

                    lnsSse += lnsErrAtLnsPick;
                    fp16Sse += fp16ErrAtFp16Pick;

                    // quantEp: per-texel-optimal lattice weight (FP16 criterion) against the CEM-
                    // quantised endpoints — isolates endpoint quantisation from weight selection.
                    long bestQuantErr = long.MaxValue;
                    for (int q = 0; q <= weightRange; q++)
                    {
                        int w = unquant[q];
                        long fp16Err = 0;
                        for (int c = 0; c < channels; c++)
                        {
                            int reconLns = Interpolation.BlendWeighted(qLow[c], qHigh[c], w);
                            double df = Fp16.FromLns(Math.Clamp(reconLns, 0, 0xFFFF)) - Fp16.FromLns((int)LnsChannel(t, c));
                            fp16Err += (long)(df * df);
                        }

                        bestQuantErr = Math.Min(bestQuantErr, fp16Err);
                    }

                    quantEpSse += bestQuantErr;
                    samples += channels;
                }
            }
        }

        return (lnsSse, fp16Sse);
    }

    /// <summary>
    /// Encodes an LNS endpoint pair through the given HDR endpoint <paramref name="mode"/> at
    /// <paramref name="colorRange"/> and decodes it back, returning the effective endpoint channels a
    /// real block would interpolate — so the caller can measure the cost of endpoint quantisation.
    /// </summary>
    private static void QuantizeEndpoints(ColorEndpointMode mode, ReadOnlySpan<int> low, ReadOnlySpan<int> high, int colorRange, Span<int> qLow, Span<int> qHigh)
    {
        var lowColor = new RgbaHdrColor((ushort)low[0], (ushort)low[1], (ushort)low[2], (ushort)low[3]);
        var highColor = new RgbaHdrColor((ushort)high[0], (ushort)high[1], (ushort)high[2], (ushort)high[3]);

        int count = mode.GetColorValuesCount();
        Span<int> values = stackalloc int[count];
        HdrEndpointEncoder.Encode(mode, lowColor, highColor, colorRange, values);
        AstcSharp.BiseEncoding.Quantize.Quantization.UnquantizeCEValuesBatch(values, colorRange);
        ColorEndpointPair pair = EndpointCodec.Decode(values, mode);

        for (int c = 0; c < 4; c++)
        {
            qLow[c] = pair.HdrLow.GetChannel(c);
            qHigh[c] = pair.HdrHigh.GetChannel(c);
        }
    }

    private static double Fp16Psnr(double sse, long samples)
    {
        if (sse <= 0)
        {
            return double.PositiveInfinity;
        }

        const double peak = 0x7BFF;
        return 10.0 * Math.Log10((peak * peak) / (sse / samples));
    }

    /// <summary>
    /// Fits the continuous LNS-domain least-squares line (centroid <paramref name="mean"/> + unit
    /// principal <paramref name="axis"/>) to <paramref name="texels"/> via power iteration.
    /// </summary>
    private static void FitLnsLine(ReadOnlySpan<RgbaHdrColor> texels, int channels, Span<double> mean, Span<double> axis)
    {
        mean.Clear();
        foreach (RgbaHdrColor t in texels)
        {
            for (int c = 0; c < channels; c++)
            {
                mean[c] += LnsChannel(t, c);
            }
        }

        for (int c = 0; c < channels; c++)
        {
            mean[c] /= texels.Length;
            axis[c] = 1.0;
        }

        Span<double> next = stackalloc double[channels];
        for (int iter = 0; iter < 24; iter++)
        {
            next.Clear();
            foreach (RgbaHdrColor t in texels)
            {
                double dot = 0;
                for (int c = 0; c < channels; c++)
                {
                    dot += (LnsChannel(t, c) - mean[c]) * axis[c];
                }

                for (int c = 0; c < channels; c++)
                {
                    next[c] += dot * (LnsChannel(t, c) - mean[c]);
                }
            }

            double norm = 0;
            for (int c = 0; c < channels; c++)
            {
                norm += next[c] * next[c];
            }

            norm = Math.Sqrt(norm);
            if (norm < 1e-12)
            {
                break;
            }

            for (int c = 0; c < channels; c++)
            {
                axis[c] = next[c] / norm;
            }
        }
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): for each footprint reports the PSNR of the
    /// *endpoint-line floor* — the best any single-partition encoding could achieve on this content,
    /// using continuous (unquantised) endpoints fitted to each block's least-squares principal axis and
    /// each texel's exact continuous projection weight. This is the ceiling the endpoint line imposes,
    /// independent of weight/endpoint quantisation or search. If the floor already sits near our emitted
    /// PSNR, the bottleneck is the single endpoint line itself (a modelling limit, not a search gap); if
    /// the floor is far above, the loss is in quantisation/assignment (which iterating could recover).
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to see the endpoint-line " +
        "PSNR floor vs our emitted PSNR.")]
    public void ReportEndpointLineFloor()
    {
        Half[] source = LoadFixtureCrop();
        this.output.WriteLine($"fixture={Fixture} crop={CropSize}x{CropSize}");
        this.output.WriteLine("  footprint      ourFull   lineFloor   arm     floor-vs-arm");

        foreach (FootprintType footprintType in ProfiledFootprints)
        {
            var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
            Footprint footprint = Footprint.FromFootprintType(footprintType);

            double ourFull = LogPsnr(source, StreamCodec.DecodeHdr(StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint), CropSize, CropSize, footprint));
            double lineFloor = EndpointLineFloorPsnr(source, footprint);
            double arm = ArmRoundTripPsnr(source, blockX, blockY);

            this.output.WriteLine($"  {footprintType,-14} {ourFull,7:F2}   {lineFloor,7:F2}   {arm,7:F2}   {lineFloor - arm,6:F2}");
        }
    }

    /// <summary>
    /// Whole-crop LNS-domain log-PSNR of the per-block endpoint-line floor: for each block, fit a
    /// least-squares line (centroid + principal axis) to the LNS texels, project each texel onto it
    /// (continuous, clamped to the segment), and measure against the source. No quantisation, no grid
    /// decimation — the theoretical best a single straight endpoint line can do on this content.
    /// </summary>
    private static double EndpointLineFloorPsnr(Half[] source, Footprint footprint)
    {
        int channels = BlockInfo.ChannelsPerPixel;
        int blocksWide = (CropSize + footprint.Width - 1) / footprint.Width;
        int blocksHigh = (CropSize + footprint.Height - 1) / footprint.Height;

        Span<RgbaHdrColor> texels = new RgbaHdrColor[footprint.PixelCount];
        double sumSquaredError = 0;
        long sampleCount = 0;
        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                GatherLnsBlock(source, footprint, bx * footprint.Width, by * footprint.Height, texels);
                sumSquaredError += BlockLineFloorSquaredError(texels, channels);
                sampleCount += texels.Length * channels;
            }
        }

        if (sumSquaredError == 0)
        {
            return double.PositiveInfinity;
        }

        const double peak = 0x7BFF;
        double meanSquaredError = sumSquaredError / sampleCount;
        return 10.0 * Math.Log10((peak * peak) / meanSquaredError);
    }

    /// <summary>
    /// Squared reconstruction error of one block against its own least-squares endpoint line, in the
    /// LNS domain, with continuous endpoints and continuous per-texel projection weights.
    /// </summary>
    private static double BlockLineFloorSquaredError(ReadOnlySpan<RgbaHdrColor> texels, int channels)
    {
        // Centroid.
        Span<double> mean = stackalloc double[channels];
        foreach (RgbaHdrColor t in texels)
        {
            for (int c = 0; c < channels; c++)
            {
                mean[c] += LnsChannel(t, c);
            }
        }

        for (int c = 0; c < channels; c++)
        {
            mean[c] /= texels.Length;
        }

        // Principal axis via power iteration on the covariance matrix (a few iterations suffice).
        Span<double> axis = stackalloc double[channels];
        for (int c = 0; c < channels; c++)
        {
            axis[c] = 1.0;
        }

        Span<double> next = stackalloc double[channels];
        for (int iter = 0; iter < 24; iter++)
        {
            next.Clear();
            foreach (RgbaHdrColor t in texels)
            {
                double dot = 0;
                for (int c = 0; c < channels; c++)
                {
                    dot += (LnsChannel(t, c) - mean[c]) * axis[c];
                }

                for (int c = 0; c < channels; c++)
                {
                    next[c] += dot * (LnsChannel(t, c) - mean[c]);
                }
            }

            double norm = 0;
            for (int c = 0; c < channels; c++)
            {
                norm += next[c] * next[c];
            }

            norm = Math.Sqrt(norm);
            if (norm < 1e-12)
            {
                break;
            }

            for (int c = 0; c < channels; c++)
            {
                axis[c] = next[c] / norm;
            }
        }

        // Each texel projects onto the line through mean along axis; reconstruction is the projection.
        // Error is measured in the FP16 domain (like every PSNR here): the reconstructed LNS value and
        // the source both pass through Fp16.FromLns to FP16 bit patterns before differencing, so the
        // floor is directly comparable to ARM's FP16-domain PSNR.
        double error = 0;
        foreach (RgbaHdrColor t in texels)
        {
            double dot = 0;
            for (int c = 0; c < channels; c++)
            {
                dot += (LnsChannel(t, c) - mean[c]) * axis[c];
            }

            for (int c = 0; c < channels; c++)
            {
                int reconstructedLns = (int)Math.Clamp(Math.Round(mean[c] + (dot * axis[c])), 0, 0xFFFF);
                double reconstructedFp16 = Fp16.FromLns(reconstructedLns);
                double sourceFp16 = Fp16.FromLns((int)LnsChannel(t, c));
                double diff = reconstructedFp16 - sourceFp16;
                error += diff * diff;
            }
        }

        return error;
    }

    private static double LnsChannel(RgbaHdrColor texel, int channel) => channel switch
    {
        0 => texel.R,
        1 => texel.G,
        2 => texel.B,
        _ => texel.A,
    };

    /// <summary>
    /// Diagnostic harness (not an assertion): reports, per footprint, our full-search PSNR, our
    /// single-partition-only PSNR (bypassing the multi-partition and dual-plane candidates), and ARM's
    /// PSNR. ARM encodes the real fixture almost entirely single-partition, so if our
    /// single-partition-only quality already approaches ARM's, the gap is layout *selection*
    /// (tractable); if single-partition-only stays far below ARM, the gap is within-layout search
    /// quality (ARM's endpoint/weight co-refinement).
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to compare our full vs. " +
        "single-partition-only vs. ARM PSNR.")]
    public void CompareSinglePartitionOnlyAgainstReferenceEncoder()
    {
        Half[] source = LoadFixtureCrop();
        this.output.WriteLine($"fixture={Fixture} crop={CropSize}x{CropSize}");
        this.output.WriteLine("  footprint      ourFull   ourSingle   arm    single-vs-arm");

        foreach (FootprintType footprintType in ProfiledFootprints)
        {
            var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
            Footprint footprint = Footprint.FromFootprintType(footprintType);

            double ourFull = LogPsnr(source, StreamCodec.DecodeHdr(StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint), CropSize, CropSize, footprint));
            double ourSingle = LogPsnr(source, SinglePartitionOnlyRoundTrip(source, footprint));
            double arm = ArmRoundTripPsnr(source, blockX, blockY);

            this.output.WriteLine($"  {footprintType,-14} {ourFull,7:F2}   {ourSingle,7:F2}   {arm,7:F2}   {ourSingle - arm,6:F2}");
        }
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): for each footprint forces our encoder to ARM's exact
    /// per-block (grid, range) — for the blocks ARM encoded single-partition, non-dual — and reports
    /// the resulting PSNR against our full search and ARM. This separates two causes of the gap: if
    /// forcing ARM's config closes it, our search was *rejecting* the fine config it can otherwise fit
    /// (a selection gap — tractable); if the forced PSNR stays near our full PSNR, our *fit at that
    /// config* is genuinely worse (a within-config fit gap — needs iterative endpoint/weight
    /// co-refinement). Blocks ARM did not encode as plain single-partition fall back to our full encode
    /// so the crop is complete; the forced-block count is reported so the coverage is explicit.
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly to force our encoder to " +
        "ARM's per-block (grid,range) and compare PSNR.")]
    public void ForceReferenceConfigPerBlock()
    {
        Half[] source = LoadFixtureCrop();
        this.output.WriteLine($"fixture={Fixture} crop={CropSize}x{CropSize}");
        this.output.WriteLine("  footprint      ourFull   forced    arm     forced-vs-arm   forcedBlocks");

        foreach (FootprintType footprintType in ProfiledFootprints)
        {
            var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
            Footprint footprint = Footprint.FromFootprintType(footprintType);

            byte[] armEncoded = ReferenceDecoder.CompressHdr(source, CropSize, CropSize, blockX, blockY);
            float[] forcedDecoded = ForceReferenceConfigRoundTrip(source, footprint, armEncoded, out int forcedBlocks, out int totalBlocks);

            double ourFull = LogPsnr(source, StreamCodec.DecodeHdr(StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint), CropSize, CropSize, footprint));
            double forced = LogPsnr(source, forcedDecoded);
            double arm = ArmRoundTripPsnr(source, blockX, blockY);

            this.output.WriteLine($"  {footprintType,-14} {ourFull,7:F2}   {forced,7:F2}   {arm,7:F2}   {forced - arm,10:F2}   {forcedBlocks}/{totalBlocks}");
        }
    }

    /// <summary>
    /// Encodes every block of <paramref name="source"/>, forcing our single-partition encoder to the
    /// (grid, range) ARM used for that block where ARM chose a plain single-partition layout; other
    /// blocks (dual-plane / multi-partition / void, or a config we cannot legally fit) fall back to our
    /// full encode. Reports how many blocks were actually forced.
    /// </summary>
    private static float[] ForceReferenceConfigRoundTrip(
        Half[] source, Footprint footprint, byte[] armEncoded, out int forcedBlocks, out int totalBlocks)
    {
        int blocksWide = (CropSize + footprint.Width - 1) / footprint.Width;
        int blocksHigh = (CropSize + footprint.Height - 1) / footprint.Height;
        byte[] encoded = new byte[blocksWide * blocksHigh * BlockInfo.SizeInBytes];
        forcedBlocks = 0;
        totalBlocks = blocksWide * blocksHigh;

        Span<RgbaHdrColor> texels = new RgbaHdrColor[footprint.PixelCount];
        int blockIndex = 0;
        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                GatherLnsBlock(source, footprint, bx * footprint.Width, by * footprint.Height, texels);

                UInt128 armBits = BinaryPrimitives.ReadUInt128LittleEndian(armEncoded.AsSpan(blockIndex * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes));
                BlockInfo armInfo = BlockModeDecoder.Decode(armBits);

                UInt128 block;
                bool armIsPlainSingle = !armInfo.IsVoidExtent && armInfo.PartitionCount == 1 && !armInfo.DualPlane.Enabled;
                if (armIsPlainSingle
                    && BlockEncoderCore.EncodeForcedConfig<RgbaHdrColor, HdrColorStrategy>(
                        texels, footprint, armInfo.Weights.Width, armInfo.Weights.Height, armInfo.Weights.Range, out block))
                {
                    forcedBlocks++;
                }
                else
                {
                    block = BlockEncoderCore.EncodeSinglePartitionOnly<RgbaHdrColor, HdrColorStrategy>(texels, footprint);
                }

                BinaryPrimitives.WriteUInt128LittleEndian(encoded.AsSpan(blockIndex * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes), block);
                blockIndex++;
            }
        }

        return StreamCodec.DecodeHdr(encoded, CropSize, CropSize, footprint);
    }

    /// <summary>
    /// Encodes every block of <paramref name="source"/> with the single-partition-only search
    /// (<see cref="BlockEncoderCore.EncodeSinglePartitionOnly{TTexel, TStrategy}"/>), assembles the
    /// block stream, and decodes it back to FP16 floats — the single-partition-only analogue of
    /// <see cref="StreamCodec.EncodeHdr"/> + <see cref="StreamCodec.DecodeHdr"/>.
    /// </summary>
    private static float[] SinglePartitionOnlyRoundTrip(Half[] source, Footprint footprint)
    {
        int blocksWide = (CropSize + footprint.Width - 1) / footprint.Width;
        int blocksHigh = (CropSize + footprint.Height - 1) / footprint.Height;
        byte[] encoded = new byte[blocksWide * blocksHigh * BlockInfo.SizeInBytes];

        Span<RgbaHdrColor> texels = new RgbaHdrColor[footprint.PixelCount];
        int blockIndex = 0;
        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                GatherLnsBlock(source, footprint, bx * footprint.Width, by * footprint.Height, texels);
                UInt128 block = BlockEncoderCore.EncodeSinglePartitionOnly<RgbaHdrColor, HdrColorStrategy>(texels, footprint);
                BinaryPrimitives.WriteUInt128LittleEndian(encoded.AsSpan(blockIndex * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes), block);
                blockIndex++;
            }
        }

        return StreamCodec.DecodeHdr(encoded, CropSize, CropSize, footprint);
    }

    /// <summary>
    /// Gathers one footprint-sized block of LNS-domain texels from the FP16 crop at pixel origin
    /// (<paramref name="originX"/>, <paramref name="originY"/>), clamping right/bottom overhang to the
    /// nearest in-crop texel — matching <see cref="AstcSharp.AstcEncoder"/>'s HDR gather.
    /// </summary>
    private static void GatherLnsBlock(ReadOnlySpan<Half> source, Footprint footprint, int originX, int originY, Span<RgbaHdrColor> texels)
    {
        int channels = BlockInfo.ChannelsPerPixel;
        for (int y = 0; y < footprint.Height; y++)
        {
            int srcY = Math.Min(originY + y, CropSize - 1);
            for (int x = 0; x < footprint.Width; x++)
            {
                int srcX = Math.Min(originX + x, CropSize - 1);
                int idx = ((srcY * CropSize) + srcX) * channels;
                texels[(y * footprint.Width) + x] = new RgbaHdrColor(
                    (ushort)Fp16.ToLns(BitConverter.HalfToUInt16Bits(source[idx])),
                    (ushort)Fp16.ToLns(BitConverter.HalfToUInt16Bits(source[idx + 1])),
                    (ushort)Fp16.ToLns(BitConverter.HalfToUInt16Bits(source[idx + 2])),
                    (ushort)Fp16.ToLns(BitConverter.HalfToUInt16Bits(source[idx + 3])));
            }
        }
    }

    private static int Bucket(double psnr) => psnr switch
    {
        < 40 => 0,
        < 50 => 1,
        < 60 => 2,
        < 70 => 3,
        < 90 => 4,
        _ => 5,
    };

    private static double BlockPsnr(ReadOnlySpan<Half> source, ReadOnlySpan<float> decoded, int ox, int oy)
    {
        double sse = 0;
        int n = 0;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int idx = (((oy + y) * CropSize) + (ox + x)) * BlockInfo.ChannelsPerPixel;
                for (int c = 0; c < BlockInfo.ChannelsPerPixel; c++)
                {
                    double o = BitConverter.HalfToUInt16Bits(source[idx + c]);
                    double d = BitConverter.HalfToUInt16Bits((Half)decoded[idx + c]);
                    sse += (o - d) * (o - d);
                    n++;
                }
            }
        }

        double mse = sse / n;
        return mse == 0 ? double.PositiveInfinity : 10.0 * Math.Log10((double)0x7BFF * 0x7BFF / mse);
    }

    /// <summary>
    /// Averages the weight-grid point count and weight range (weight precision) across all
    /// non-void-extent blocks of an encoded image, and summarises the layout mix (single / dual /
    /// multi). Higher grid points or weight range = finer weight precision spent on the block.
    /// </summary>
    private static string SummariseWeightConfigs(byte[] encoded)
    {
        int blockCount = encoded.Length / BlockInfo.SizeInBytes;
        long gridPointSum = 0, weightRangeSum = 0;
        int weightBearing = 0, single = 0, dual = 0, multi = 0, voids = 0;
        for (int i = 0; i < blockCount; i++)
        {
            UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(i * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes));
            BlockInfo info = BlockModeDecoder.Decode(bits);
            if (info.IsVoidExtent)
            {
                voids++;
                continue;
            }

            weightBearing++;
            gridPointSum += info.Weights.Width * info.Weights.Height;
            weightRangeSum += info.Weights.Range;
            if (info.PartitionCount > 1)
            {
                multi++;
            }
            else if (info.DualPlane.Enabled)
            {
                dual++;
            }
            else
            {
                single++;
            }
        }

        double avgGrid = weightBearing == 0 ? 0 : (double)gridPointSum / weightBearing;
        double avgRange = weightBearing == 0 ? 0 : (double)weightRangeSum / weightBearing;
        return $"{avgGrid,5:F1}      {avgRange,5:F1}     single×{single} dual×{dual} multi×{multi} void×{voids}";
    }

    private static readonly FootprintType[] ProfiledFootprints =
    [
        FootprintType.Footprint4x4, FootprintType.Footprint6x6, FootprintType.Footprint8x8,
        FootprintType.Footprint10x10, FootprintType.Footprint12x12,
    ];

    public static TheoryData<FootprintType> Footprints
    {
        get
        {
            var data = new TheoryData<FootprintType>();
            foreach (FootprintType footprint in ProfiledFootprints)
            {
                data.Add(footprint);
            }

            return data;
        }
    }

    private static Half[] LoadFixtureCrop()
    {
        string filePath = Path.Combine("TestData", "Input", "Astc", "HdrPipeline", Fixture + ".astc");
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(filePath));
        Half[] full = StreamCodec.DecodeHdrHalf(file.Blocks, file.Width, file.Height, file.Footprint);
        return CropTopLeft(full, file.Width, CropSize, CropSize);
    }

    private static double OurRoundTripPsnr(Half[] source, Footprint footprint)
    {
        byte[] encoded = StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
        return LogPsnr(source, StreamCodec.DecodeHdr(encoded, CropSize, CropSize, footprint));
    }

    private static double ArmRoundTripPsnr(Half[] source, int blockX, int blockY)
    {
        byte[] armEncoded = ReferenceDecoder.CompressHdr(source, CropSize, CropSize, blockX, blockY);
        Half[] armDecoded = ReferenceDecoder.DecompressHdr(armEncoded, CropSize, CropSize, blockX, blockY);
        return LogPsnr(source, HalvesToFloats(armDecoded));
    }

    /// <summary>
    /// Per-block histogram of the endpoint mode and layout our encoder chose across the encoded image,
    /// e.g. <c>"RgbDirect×180 dual×40 multi×36"</c> — shows which modes/layouts real content elicits.
    /// </summary>
    private static string SummariseModeUsage(byte[] encoded)
    {
        var modeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int blockCount = encoded.Length / BlockInfo.SizeInBytes;
        for (int i = 0; i < blockCount; i++)
        {
            UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(i * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes));
            BlockInfo info = BlockModeDecoder.Decode(bits);
            string key = info.IsVoidExtent ? "void"
                : info.PartitionCount > 1 ? $"multi{info.PartitionCount}"
                : info.DualPlane.Enabled ? $"dual:{info.EndpointMode0}"
                : info.EndpointMode0.ToString();
            modeCounts[key] = modeCounts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        return string.Join(" ", modeCounts.Select(kv => $"{kv.Key}×{kv.Value}"));
    }

    /// <summary>
    /// Log-space PSNR on the FP16 bit patterns (the domain HDR error is perceived in), comparable
    /// between the two encoders. Peak is the largest finite FP16 pattern.
    /// </summary>
    private static double LogPsnr(ReadOnlySpan<Half> original, ReadOnlySpan<float> decoded)
    {
        const double peak = 0x7BFF;
        double sumSquaredError = 0;
        for (int i = 0; i < original.Length; i++)
        {
            double o = BitConverter.HalfToUInt16Bits(original[i]);
            double d = BitConverter.HalfToUInt16Bits((Half)decoded[i]);
            double diff = d - o;
            sumSquaredError += diff * diff;
        }

        if (sumSquaredError == 0)
        {
            return double.PositiveInfinity;
        }

        double meanSquaredError = sumSquaredError / original.Length;
        return 10.0 * Math.Log10((peak * peak) / meanSquaredError);
    }

    private static Half[] CropTopLeft(ReadOnlySpan<Half> rgba, int sourceWidth, int cropWidth, int cropHeight)
    {
        int channels = BlockInfo.ChannelsPerPixel;
        Half[] cropped = new Half[cropWidth * cropHeight * channels];
        for (int y = 0; y < cropHeight; y++)
        {
            ReadOnlySpan<Half> sourceRow = rgba.Slice(y * sourceWidth * channels, cropWidth * channels);
            sourceRow.CopyTo(cropped.AsSpan(y * cropWidth * channels, cropWidth * channels));
        }

        return cropped;
    }

    private static float[] HalvesToFloats(Half[] halves)
    {
        float[] floats = new float[halves.Length];
        for (int i = 0; i < halves.Length; i++)
        {
            floats[i] = (float)halves[i];
        }

        return floats;
    }
}
