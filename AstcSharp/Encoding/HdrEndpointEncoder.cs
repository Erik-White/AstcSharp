using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Encodes a pair of HDR endpoints into the quantised colour values for a given HDR colour endpoint
/// mode (spec §C.2.15) — the inverse of the HDR path in <see cref="EndpointCodec.Decode"/>. Endpoint
/// channels are the 16-bit LNS-domain values the decoder produces and interpolates (a 12-bit
/// intermediate shifted left by 4), so encode and decode share one representation.
/// </summary>
/// <remarks>
/// Correctness is guaranteed by construction, as for <see cref="EndpointEncoder"/>: the caller
/// decodes the produced colour values back through <see cref="EndpointCodec.Decode"/> to measure
/// reconstruction error, so an imperfect inverse only costs quality (the mode loses the search) and
/// can never make an illegal block.
/// </remarks>
internal static class HdrEndpointEncoder
{
    // CEM 11 direct sub-mode is selected when both major-component bits (v4[7], v5[7]) are set.
    private const int MajorComponentDirectFlag = 0x80;

    // CEM 11 direct blue is a 7-bit field: value = channel >> 9, low 7 bits kept.
    private const int BlueFieldMask = 0x7F;

    // CEM 15 simple alpha sub-mode is selected when both v6[7] and v7[7] are set; each alpha value is
    // then a 7-bit field (channel >> 9) in the low bits.
    private const int SimpleAlphaSelectorFlag = 0x80;
    private const int AlphaFieldMask = 0x7F;

    // CEM 7 mode 5 is selected by the 4-bit mode value 0xF: v0[7:6] = 11, v1[7] = v2[7] = 1. It is the
    // only base+scale sub-mode that stores R/G/B independently (no green = red - delta) and applies no
    // major-component swizzle, so it inverts cleanly.
    private const int BaseScaleMode5V0Selector = 0xC0;
    private const int BaseScaleModeBitFlag = 0x80;
    private const int SevenBitFieldMask = 0x7F;
    private const int SixBitFieldMask = 0x3F;
    private const int FiveBitFieldMask = 0x1F;

    // CEM 7 channel/scale fields are 7-bit: value = channel >> 9.
    private const int BaseScaleFieldShift = 9;
    private const int MaxSevenBitField = 0x7F;

    // CEM 7 has six sub-modes (0..5) and four output slots (Red, Green, Blue, Scale) matching the
    // decoder's BaseScaleTarget order, endpoint channels are 12-bit intermediates (FP16 >> 4).
    private const int BaseScaleSubModeCount = 6;
    private const int BaseScaleSlotCount = 4;
    private const int Fp16ToTwelveBitShift = 4;
    private const int SlotRed = 0;
    private const int SlotGreen = 1;
    private const int SlotBlue = 2;
    private const int SlotScale = 3;

    // The two high selector bits (v0[7:6] = 11) that mark sub-modes 4 and 5; sub-mode 4 then clears
    // v2[7] (mode value 0xC|major), sub-mode 5 sets it (mode value 0xF).
    private const int BaseScaleMode4SelectorBase = 0xC;

    // CEM 3 (HDR luma, small range) packs a base luma and a delta into two values, choosing between
    // two layouts by the mode bit v0[7]. Luma is a 12-bit intermediate: value = channel >> 4.
    private const int SmallRangeLumaShift = 4;
    private const int SmallRangeModeBitFlag = 0x80;

    // Mode-clear layout (v0[7] = 0): base is stored at step 2 (finer), delta at step 2, max delta 30.
    private const int SmallRangeFineDeltaMax = 30;

    // Mode-set layout (v0[7] = 1): delta is a 5-bit field at step 4, so the largest representable
    // delta is 0x1F << 2 = 124. Deltas above this are clamped to it (rather than masked, which would
    // wrap to an arbitrary small value). CEM 2 covers wider luma spans anyway.
    private const int SmallRangeWideDeltaMax = 124;

    // Largest 12-bit luma the decoder produces (spec §C.2.14 clamps y1 to this).
    private const int MaxLuma12Bit = 0xFFF;

    /// <summary>
    /// Encodes <paramref name="low"/>/<paramref name="high"/> for <paramref name="mode"/> into
    /// quantised colour values written to <paramref name="colorValues"/> (length must be at least
    /// <c>mode.GetColorValuesCount()</c>). Endpoints are assumed ordered low-to-high in the LNS
    /// domain, matching the decoder's non-swapping paths.
    /// </summary>
    public static void Encode(ColorEndpointMode mode, RgbaHdrColor low, RgbaHdrColor high, int colorRange, Span<int> colorValues)
    {
        switch (mode)
        {
            case ColorEndpointMode.HdrLumaLargeRange: EncodeLumaLargeRange(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.HdrLumaSmallRange: EncodeLumaSmallRange(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.HdrRgbBaseScale: EncodeRgbBaseScale(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.HdrRgbDirect: EncodeRgbDirect(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.HdrRgbDirectHdrAlpha: EncodeRgbDirectHdrAlpha(colorRange, colorValues, low, high); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported HDR endpoint mode for encoding");
        }
    }

    /// <summary>
    /// CEM 2 (HDR luminance, large range): two 8-bit luma values. The decoder's <c>v1 &gt;= v0</c>
    /// branch reconstructs each channel as <c>v &lt;&lt; 8</c>, so the top byte of the LNS luma is the
    /// stored value. Endpoints ordered low-to-high keep the decoder on that exact branch.
    /// </summary>
    private static void EncodeLumaLargeRange(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        int lumaLow = LnsLuma(low);
        int lumaHigh = LnsLuma(high);
        colorValues[0] = QuantizeByte(lumaLow >> 8, colorRange);
        colorValues[1] = QuantizeByte(lumaHigh >> 8, colorRange);
    }

    /// <summary>
    /// CEM 3 (HDR luminance, small range): a base luma plus a non-negative delta, giving a much finer
    /// base step than CEM 2 (large range) over a narrow luma span. Two layouts are selected by the
    /// mode bit v0[7]: the mode-clear layout stores an 11-bit base at step 2 with delta ≤ 30, the
    /// mode-set layout a 10-bit base at step 4 with delta ≤ 124. This picks the finer (mode-clear)
    /// layout whenever the luma delta fits it, matching the decoder's reconstruction exactly.
    /// </summary>
    private static void EncodeLumaSmallRange(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        // 12-bit intermediate luma; endpoints ordered low-to-high so the delta is non-negative.
        int baseLuma = Math.Clamp(LnsLuma(low) >> SmallRangeLumaShift, 0, MaxLuma12Bit);
        int highLuma = Math.Clamp(LnsLuma(high) >> SmallRangeLumaShift, 0, MaxLuma12Bit);
        int delta = highLuma - baseLuma;

        int v0, v1;
        if (delta <= SmallRangeFineDeltaMax)
        {
            // Mode-clear: base and delta are at step 2, so drop the low bit of each.
            v0 = (baseLuma >> 1) & 0x7F;
            v1 = (((baseLuma >> 8) & 0x0F) << 4) | ((delta >> 1) & 0x0F);
        }
        else
        {
            // Mode-set: base and delta are at step 4, so drop the low two bits of each.
            // Clamp the delta to the field's max (124) so an over-range delta lands on
            // the nearest representable value rather than wrapping to an arbitrary small one.
            int clampedDelta = Math.Min(delta, SmallRangeWideDeltaMax);
            v0 = SmallRangeModeBitFlag | ((baseLuma >> 2) & 0x7F);
            v1 = (((baseLuma >> 9) & 0x07) << 5) | ((clampedDelta >> 2) & 0x1F);
        }

        colorValues[0] = QuantizeByte(v0, colorRange);
        colorValues[1] = QuantizeByte(v1, colorRange);
    }

    // CEM 11 has eight sub-modes (spec §C.2.14, modeValue 0..7) and six field slots (A, B0, B1, C, D0, D1)
    // matching the decoder's DirectTarget order. Endpoint channels are 12-bit intermediates (FP16 >> 4).
    // The passthrough sub-mode is selected by major-component value 3.
    private const int DirectSubModeCount = 8;
    private const int DirectSlotCount = 6;
    private const int SlotA = 0;
    private const int SlotB0 = 1;
    private const int SlotB1 = 2;
    private const int SlotC = 3;
    private const int SlotD0 = 4;
    private const int SlotD1 = 5;

    /// <summary>
    /// CEM 11 (HDR RGB, direct), a major-component anchor plus per-channel deltas (spec §C.2.14). The
    /// eight sub-modes trade anchor/delta field precision (the decoder's per-mode value shift) against
    /// delta field width, finer modes represent tightly-clustered endpoints the coarse ones round away.
    /// This emits every representable sub-mode plus the passthrough fallback and keeps whichever
    /// reconstructs the endpoints with the least error. Also used for the RGB half of CEM 15, whose
    /// decoder decodes v0..v5 identically.
    /// </summary>
    private static void EncodeRgbDirect(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        EncodeRgbDirectPassthrough(colorRange, colorValues, low, high);

        // The anchor+delta sub-modes are not always representable (their delta fields are narrower than
        // the passthrough's independent channels), so each is scored by real reconstruction error and
        // kept only when it beats the passthrough. Additive, since passthrough is always in the running.
        Span<int> trial = stackalloc int[DirectSlotCount];
        Span<int> scoreBuffer = stackalloc int[DirectSlotCount];
        int major = MajorComponent(high);
        long bestError = DirectReconstructionError(colorValues, colorRange, scoreBuffer, low, high);

        for (int mode = 0; mode < DirectSubModeCount; mode++)
        {
            if (!TryEncodeRgbDirectFine(mode, major, colorRange, trial, low, high))
            {
                continue;
            }

            long error = DirectReconstructionError(trial, colorRange, scoreBuffer, low, high);
            if (error < bestError)
            {
                bestError = error;
                trial.CopyTo(colorValues);
            }
        }
    }

    /// <summary>
    /// Encodes the CEM 11 passthrough sub-mode (major-component value 3).
    /// </summary>
    /// <remarks>
    /// R/G store the top byte of the channel (<c>v &lt;&lt; 8</c>), B stores a 7-bit field (<c>(v &amp; 0x7F) &lt;&lt; 9</c>),
    /// and both v4/v5 carry the major-component flag bit that selects this sub-mode. Always representable, so it
    /// is the fallback the finer sub-modes are scored against.
    /// </remarks>
    private static void EncodeRgbDirectPassthrough(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        colorValues[0] = QuantizeByte(low.R >> 8, colorRange);
        colorValues[1] = QuantizeByte(high.R >> 8, colorRange);
        colorValues[2] = QuantizeByte(low.G >> 8, colorRange);
        colorValues[3] = QuantizeByte(high.G >> 8, colorRange);
        colorValues[4] = QuantizeByte(MajorComponentDirectFlag | ((low.B >> 9) & BlueFieldMask), colorRange);
        colorValues[5] = QuantizeByte(MajorComponentDirectFlag | ((high.B >> 9) & BlueFieldMask), colorRange);
    }

    /// <summary>
    /// Tries to encode <paramref name="low"/>/<paramref name="high"/> as CEM 11 anchor+delta sub-mode.
    /// </summary>
    /// <remarks>
    /// <paramref name="mode"/> (0..7) for major component <paramref name="major"/> into
    /// <paramref name="colorValues"/>[0..5] — the inverse of <see cref="HdrEndpointDecoder"/>'s direct
    /// path. The channels are un-swizzled so the major component is the anchor, the anchor and deltas
    /// are quantised to the mode's value shift, and the scattered field bits are routed through the
    /// decoder's own placement table (<see cref="HdrEndpointDecoder.DirectPlacements"/>).
    /// </remarks>
    /// <returns>
    /// Returns false — leaving <paramref name="colorValues"/> untouched — when any field falls outside
    /// its range for this mode (so the caller keeps a representable candidate instead).
    /// </returns>
    private static bool TryEncodeRgbDirectFine(int mode, int major, int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        // The decoder expands each stored field by valueShift = (mode >> 1) ^ 3 (spec §C.2.14).
        int valueShift = (mode >> 1) ^ 3;

        // Un-swizzle into the decoder's (anchor, green, blue) order, in 12-bit intermediates, then round
        // each field to the mode's value shift so the stored value expands back exactly.
        (int anchorHigh, int greenHigh, int blueHigh) = SwapToMajor(high.R >> Fp16ToTwelveBitShift, high.G >> Fp16ToTwelveBitShift, high.B >> Fp16ToTwelveBitShift, major);
        (int anchorLow, int greenLow, int blueLow) = SwapToMajor(low.R >> Fp16ToTwelveBitShift, low.G >> Fp16ToTwelveBitShift, low.B >> Fp16ToTwelveBitShift, major);

        int a = RoundShift(anchorHigh, valueShift);
        int b0 = a - RoundShift(greenHigh, valueShift);
        int b1 = a - RoundShift(blueHigh, valueShift);
        int c = a - RoundShift(anchorLow, valueShift);
        int d0 = a - b0 - c - RoundShift(greenLow, valueShift);
        int d1 = a - b1 - c - RoundShift(blueLow, valueShift);

        // Field widths for this mode: the A anchor and the C/B/D deltas each occupy a mode-dependent
        // number of bits (the decoder's data-bit width plus whatever high bits the placement table
        // adds). d0/d1 are signed at the mode's data-bit width; the rest are unsigned.
        int dataBits = HdrEndpointDecoder.DirectDataBitsByMode[mode];
        int signedMin = -(1 << (dataBits - 1));
        int signedMax = (1 << (dataBits - 1)) - 1;

        Span<int> maxField = stackalloc int[DirectSlotCount];
        DirectFieldMaxima(mode, maxField);

        if ((uint)a > (uint)maxField[SlotA]
            || (uint)b0 > (uint)maxField[SlotB0] || (uint)b1 > (uint)maxField[SlotB1]
            || (uint)c > (uint)maxField[SlotC]
            || d0 < signedMin || d0 > signedMax
            || d1 < signedMin || d1 > signedMax)
        {
            return false;
        }

        PackDirectFields(mode, major, colorRange, colorValues, a, b0, b1, c, d0, d1);
        return true;
    }

    /// <summary>
    /// Packs the six field values into the six quantised colour values for CEM 11 sub-mode
    /// <paramref name="mode"/>. Base bits occupy the low bits of each slot, the scattered high bits are
    /// placed by inverting <see cref="HdrEndpointDecoder.DirectPlacements"/>, and the mode/major
    /// selector bits go into the top bits of v1/v2/v3 (mode) and v4/v5 (major).
    /// </summary>
    private static void PackDirectFields(int mode, int major, int colorRange, Span<int> colorValues, int a, int b0, int b1, int c, int d0, int d1)
    {
        int dataBits = HdrEndpointDecoder.DirectDataBitsByMode[mode];
        Span<int> fields = [a, b0, b1, c, d0 & ((1 << dataBits) - 1), d1 & ((1 << dataBits) - 1)];
        Span<int> v =
        [
            // Base field bits, mirroring the decoder's target assembly
            fields[SlotA] & 0xFF, // A holds v0 (8 bits) + v1[6]->bit8
            fields[SlotC] & 0x3F, // C holds v1[5:0]
            fields[SlotB0] & 0x3F, // B0/B1 hold v2/v3[5:0]
            fields[SlotB1] & 0x3F,
            fields[SlotD0] & 0x7F, // D0/D1 hold v4/v5[6:0]
            fields[SlotD1] & 0x7F,
        ];

        // The A bit-8 lives at v1[6] in every mode (decoder: v0 | ((v1 & 0x40) << 2)).
        v[1] |= ((fields[SlotA] >> 8) & 1) << 6;

        int oneHotMode = 1 << mode;
        foreach (HdrEndpointDecoder.BitPlacement placement in HdrEndpointDecoder.DirectPlacements)
        {
            if ((oneHotMode & placement.ModeMask) == 0)
            {
                continue;
            }

            int bit = (fields[placement.Slot] >> placement.TargetShift) & 1;
            (int vIndex, int vBit) = HdrEndpointDecoder.DirectSourceBitOrigins[placement.SourceBit];
            v[vIndex] |= bit << vBit;
        }

        // Mode selector (decoder reassembles from these)
        v[1] |= (mode & 1) << 7; // mode0
        v[2] |= ((mode >> 1) & 1) << 7; // mode1
        v[3] |= ((mode >> 2) & 1) << 7; // mode2

        // Major-component selector
        v[4] |= (major & 1) << 7; // major0
        v[5] |= ((major >> 1) & 1) << 7; // major1

        for (int i = 0; i < DirectSlotCount; i++)
        {
            colorValues[i] = QuantizeByte(v[i], colorRange);
        }
    }

    /// <summary>
    /// Fills <paramref name="maxField"/> with the largest representable unsigned value for each of the
    /// six CEM 11 slots under sub-mode <paramref name="mode"/> — the base bits plus whichever scattered
    /// high bits that mode enables (derived from the decoder's placement table). D0/D1 are handled as
    /// signed by the caller, so their entries here are unused.
    /// </summary>
    private static void DirectFieldMaxima(int mode, Span<int> maxField)
    {
        // Base bit counts (highest base bit index) from spec §C.2.14:
        // A is 8 bits (bit 8 via v1[6]),
        // C/B0/B1 are 6 bits,
        // D0/D1 are 7 bits
        Span<int> highestBit = [8, 5, 5, 5, 6, 6];
        int oneHotMode = 1 << mode;
        foreach (HdrEndpointDecoder.BitPlacement placement in HdrEndpointDecoder.DirectPlacements)
        {
            if ((oneHotMode & placement.ModeMask) != 0 && placement.TargetShift > highestBit[placement.Slot])
            {
                highestBit[placement.Slot] = placement.TargetShift;
            }
        }

        for (int i = 0; i < DirectSlotCount; i++)
        {
            maxField[i] = (1 << (highestBit[i] + 1)) - 1;
        }
    }

    /// <summary>
    /// Rounds <paramref name="value"/> (a 12-bit intermediate) to the nearest multiple of
    /// <c>1 &lt;&lt; shift</c> and divides, clamping negatives to zero. The decoder reconstructs each
    /// field as <c>stored &lt;&lt; shift</c>, so this is the encode inverse. At <paramref name="shift"/>
    /// 0 (the full-precision sub-modes 6/7) it is the identity — the nearest multiple of 1 is the value
    /// itself. The general formula's <c>1 &lt;&lt; (shift - 1)</c> rounding bias would underflow there.
    /// </summary>
    private static int RoundShift(int value, int shift)
    {
        if (value <= 0)
        {
            return 0;
        }

        return shift == 0
            ? value
            : (value + (1 << (shift - 1))) >> shift;
    }

    /// <summary>
    /// Scores a CEM 11 colour-value set by its real reconstruction error: unquantise and decode through
    /// the actual decoder, then sum the squared per-channel LNS-domain differences.
    /// <paramref name="scoreBuffer"/> is a reusable length-6 scratch span.
    /// </summary>
    private static long DirectReconstructionError(ReadOnlySpan<int> quantValues, int colorRange, Span<int> scoreBuffer, RgbaHdrColor low, RgbaHdrColor high)
    {
        quantValues[..DirectSlotCount].CopyTo(scoreBuffer);
        Quantization.UnquantizeCEValuesBatch(scoreBuffer, colorRange);
        (RgbaHdrColor decodedLow, RgbaHdrColor decodedHigh) = HdrEndpointDecoder.DecodeHdrModeUnquantized(scoreBuffer, ColorEndpointMode.HdrRgbDirect);
        return LnsSquaredError(low, decodedLow) + LnsSquaredError(high, decodedHigh);
    }

    /// <summary>
    /// Returns the major component (0 = R, 1 = G, 2 = B) — the channel with the largest high-endpoint
    /// value, which the anchor/delta layout assumes is largest so the deltas stay non-negative.
    /// </summary>
    private static int MajorComponent(RgbaHdrColor high)
    {
        if (high.G >= high.R && high.G >= high.B)
        {
            return 1;
        }

        return high.B >= high.R
            ? 2
            : 0;
    }

    /// <summary>
    /// Swaps channel <paramref name="major"/> into the first (anchor) position, mirroring the decoder's
    /// major-component channel swap (spec §C.2.14): major 1 exchanges R/G, major 2 exchanges R/B.
    /// </summary>
    private static (int Anchor, int Green, int Blue) SwapToMajor(int r, int g, int b, int major) => major switch
    {
        1 => (g, r, b),
        2 => (b, g, r),
        _ => (r, g, b),
    };

    /// <summary>
    /// CEM 7 (HDR RGB, base+scale): a base colour plus a shared scale, modelling a uniform log-space
    /// darkening (a uniform multiplicative dim in linear space). Uses 4 colour values rather than CEM 11's
    /// 6, leaving more of the 128-bit budget for weight precision. The spec defines six sub-modes
    /// trading field precision (shift) against how many bits the deltas get. The coarsest (mode 5,
    /// shift 9) stores R/G/B independently, the finer ones (0..4, shift 1..4) store green/blue as deltas
    /// from a major-component anchor. This emits every sub-mode and keeps the one that reconstructs the
    /// endpoints with the least error.
    /// </summary>
    private static void EncodeRgbBaseScale(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        EncodeBaseScaleMode5(colorRange, colorValues, low, high);

        // The finer sub-modes (0..4) shift by 1..4 instead of mode 5's 9 and carry per-channel deltas,
        // so they represent endpoints mode 5 rounds away. They are not always a better fit (the delta
        // fields are narrower), so each candidate is scored by real reconstruction error and kept only
        // when it beats mode 5 — additive by construction, since mode 5 is always in the running.
        Span<int> trial = stackalloc int[BaseScaleSlotCount];
        Span<int> scoreBuffer = stackalloc int[BaseScaleSlotCount];
        int major = MajorComponent(high);
        long bestError = BaseScaleReconstructionError(colorValues, colorRange, scoreBuffer, low, high);

        for (int mode = 0; mode < BaseScaleSubModeCount - 1; mode++)
        {
            EncodeBaseScaleFine(mode, major, colorRange, trial, low, high);
            long error = BaseScaleReconstructionError(trial, colorRange, scoreBuffer, low, high);
            if (error < bestError)
            {
                bestError = error;
                trial.CopyTo(colorValues);
            }
        }
    }

    /// <summary>
    /// Encodes CEM 7 mode 5, the coarsest base+scale sub-mode: R/G/B stored independently (no delta),
    /// no channel swizzle, fields <c>&gt;&gt; 9</c> with one shared 7-bit scale.
    /// </summary>
    private static void EncodeBaseScaleMode5(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        int redField = high.R >> BaseScaleFieldShift;
        int greenField = high.G >> BaseScaleFieldShift;
        int blueField = high.B >> BaseScaleFieldShift;

        // One scale serves all three channels; use the mean high-minus-low field difference, clamped
        // so no channel's low endpoint underflows the field range.
        int scale = ScaleField(low, redField, greenField, blueField);

        int v0 = BaseScaleMode5V0Selector | (redField & SixBitFieldMask);
        int v1 = BaseScaleModeBitFlag | (greenField & SevenBitFieldMask);
        int v2 = BaseScaleModeBitFlag | (blueField & SevenBitFieldMask);
        int v3 = ((redField >> 6) & 1) << 7 | (scale & SevenBitFieldMask);

        colorValues[0] = QuantizeByte(v0, colorRange);
        colorValues[1] = QuantizeByte(v1, colorRange);
        colorValues[2] = QuantizeByte(v2, colorRange);
        colorValues[3] = QuantizeByte(v3, colorRange);
    }

    /// <summary>
    /// Encodes CEM 7 sub-mode <paramref name="mode"/> (0..4) for major component
    /// <paramref name="major"/>. These sub-modes shift by 1..4 (finer than mode 5's 9) and store the
    /// green/blue channels as deltas from a per-mode "red" anchor (the major component), with a shared
    /// scale, the inverse of <see cref="HdrEndpointDecoder"/>'s base+scale path. The scattered,
    /// non-contiguous field bits are routed through the decoder's own placement table
    /// (<see cref="HdrEndpointDecoder.BaseScalePlacements"/>) so the two share one source of truth.
    /// </summary>
    private static void EncodeBaseScaleFine(int mode, int major, int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        int shift = HdrEndpointDecoder.BaseScaleShiftByMode[mode];
        int unit = 1 << shift;

        // Un-swizzle into the decoder's internal (anchor, green, blue) order: the major component is the
        // "red" anchor from which the other two channels are stored as deltas.
        (int anchorHigh, int greenHigh, int blueHigh) = SwapToMajor(high.R >> Fp16ToTwelveBitShift, high.G >> Fp16ToTwelveBitShift, high.B >> Fp16ToTwelveBitShift, major);
        (int anchorLow, int greenLow, int blueLow) = SwapToMajor(low.R >> Fp16ToTwelveBitShift, low.G >> Fp16ToTwelveBitShift, low.B >> Fp16ToTwelveBitShift, major);

        Span<int> maxField = stackalloc int[BaseScaleSlotCount];
        BaseScaleFieldMaxima(mode, maxField);

        int anchorField = Math.Clamp(RoundToUnit(anchorHigh, unit), 0, maxField[SlotRed]);
        int anchorReconstructed = anchorField << shift;

        // Decoder reconstructs green/blue as (anchor - deltaField << shift); derive each delta against
        // the reconstructed anchor so the round-trip matches.
        int greenField = Math.Clamp(RoundToUnit(anchorReconstructed - greenHigh, unit), 0, maxField[SlotGreen]);
        int blueField = Math.Clamp(RoundToUnit(anchorReconstructed - blueHigh, unit), 0, maxField[SlotBlue]);

        int meanDifference = ((anchorHigh - anchorLow) + (greenHigh - greenLow) + (blueHigh - blueLow) + 1) / 3;
        int scaleField = Math.Clamp(RoundToUnit(meanDifference, unit), 0, maxField[SlotScale]);

        PackBaseScaleFields(mode, major, colorRange, colorValues, anchorField, greenField, blueField, scaleField);
    }

    /// <summary>
    /// Packs the four field values into the four quantised colour values for CEM 7 sub-mode
    /// <paramref name="mode"/> (0..4). Base bits occupy the low bits of each value; the scattered high
    /// bits are placed by inverting <see cref="HdrEndpointDecoder.BaseScalePlacements"/>, and the
    /// mode/major selector bits go into the top bits of v0/v1/v2.
    /// </summary>
    private static void PackBaseScaleFields(int mode, int major, int colorRange, Span<int> colorValues, int anchorField, int greenField, int blueField, int scaleField)
    {
        Span<int> fields = [anchorField, greenField, blueField, scaleField];
        Span<int> v = stackalloc int[BaseScaleSlotCount];

        v[0] = anchorField & SixBitFieldMask;
        v[1] = greenField & FiveBitFieldMask;
        v[2] = blueField & FiveBitFieldMask;
        v[3] = scaleField & FiveBitFieldMask;

        int oneHotMode = 1 << mode;
        foreach (HdrEndpointDecoder.BitPlacement placement in HdrEndpointDecoder.BaseScalePlacements)
        {
            if ((oneHotMode & placement.ModeMask) == 0)
            {
                continue;
            }

            int bit = (fields[placement.Slot] >> placement.TargetShift) & 1;
            (int vIndex, int vBit) = HdrEndpointDecoder.BaseScaleSourceBitOrigins[placement.SourceBit];
            v[vIndex] |= bit << vBit;
        }

        int modeValue = ModeValueFor(mode, major);
        v[0] |= (modeValue & 3) << 6;
        v[1] |= ((modeValue >> 2) & 1) << 7;
        v[2] |= ((modeValue >> 3) & 1) << 7;

        for (int i = 0; i < BaseScaleSlotCount; i++)
        {
            colorValues[i] = QuantizeByte(v[i], colorRange);
        }
    }

    /// <summary>
    /// Fills <paramref name="maxField"/> with the largest representable value for each of the four
    /// CEM 7 slots under sub-mode <paramref name="mode"/>, the base bits plus whichever scattered high
    /// bits that mode enables (derived from the decoder's placement table).
    /// </summary>
    private static void BaseScaleFieldMaxima(int mode, Span<int> maxField)
    {
        // Base bit counts: red is 6 bits, the others 5 (spec §C.2.14 field widths).
        Span<int> highestBit = [5, 4, 4, 4];
        int oneHotMode = 1 << mode;
        foreach (HdrEndpointDecoder.BitPlacement placement in HdrEndpointDecoder.BaseScalePlacements)
        {
            if ((oneHotMode & placement.ModeMask) != 0 && placement.TargetShift > highestBit[placement.Slot])
            {
                highestBit[placement.Slot] = placement.TargetShift;
            }
        }

        for (int i = 0; i < BaseScaleSlotCount; i++)
        {
            maxField[i] = (1 << (highestBit[i] + 1)) - 1;
        }
    }

    /// <summary>
    /// Reassembles the 4-bit mode selector the decoder reads from v0[7:6]/v1[7]/v2[7] for sub-modes
    /// 0..4. Modes 0–3 store the mode in the low two bits and the major component in the next two.
    /// Mode 4 sets both high selector bits and stores the major component in the low two.
    /// </summary>
    private static int ModeValueFor(int mode, int major) => mode == 4
        ? BaseScaleMode4SelectorBase | major
        : (major << 2) | mode;

    /// <summary>
    /// Scores a base+scale colour-value set by its real reconstruction error: unquantise and decode
    /// through the actual decoder, then sum the squared per-channel differences in the LNS domain the
    /// block encoder measures in. <paramref name="scoreBuffer"/> is a reusable length-4 scratch span.
    /// </summary>
    private static long BaseScaleReconstructionError(ReadOnlySpan<int> quantValues, int colorRange, Span<int> scoreBuffer, RgbaHdrColor low, RgbaHdrColor high)
    {
        quantValues[..BaseScaleSlotCount].CopyTo(scoreBuffer);
        Quantization.UnquantizeCEValuesBatch(scoreBuffer, colorRange);
        (RgbaHdrColor decodedLow, RgbaHdrColor decodedHigh) = HdrEndpointDecoder.DecodeHdrModeUnquantized(scoreBuffer, ColorEndpointMode.HdrRgbBaseScale);
        return LnsSquaredError(low, decodedLow) + LnsSquaredError(high, decodedHigh);
    }

    /// <summary>
    /// Sum of squared per-channel (R/G/B) differences between two HDR colours. The channels are already
    /// LNS-domain values, so they are compared directly — matching the block-level metric
    /// in <see cref="ColorGeometry.ReconstructionError"/>. Applying <c>ToLns</c> again here would be a
    /// double conversion that corrupts the sub-mode selection. Alpha is excluded (opaque for these modes).
    /// </summary>
    private static long LnsSquaredError(RgbaHdrColor a, RgbaHdrColor b)
    {
        long deltaRed = a.R - b.R;
        long deltaGreen = a.G - b.G;
        long deltaBlue = a.B - b.B;
        return (deltaRed * deltaRed) + (deltaGreen * deltaGreen) + (deltaBlue * deltaBlue);
    }

    /// <summary>
    /// Rounds <paramref name="value"/> to the nearest multiple of <paramref name="unit"/> and divides,
    /// clamping negative inputs to zero (a delta can go slightly negative when the major component is
    /// not strictly the largest channel).
    /// </summary>
    private static int RoundToUnit(int value, int unit) => value <= 0
        ? 0
        : (value + (unit >> 1)) / unit;

    /// <summary>
    /// The shared 7-bit base+scale factor for CEM 7 mode 5: the mean per-channel high-minus-low field
    /// difference, clamped to <c>[0, 0x7F]</c>. This is a least-squares-style average, not a per-channel
    /// bound — a channel whose span is below the mean can have its low endpoint driven negative, which
    /// the decoder clamps to 0.
    /// </summary>
    private static int ScaleField(RgbaHdrColor low, int redField, int greenField, int blueField)
    {
        int lowRed = low.R >> BaseScaleFieldShift;
        int lowGreen = low.G >> BaseScaleFieldShift;
        int lowBlue = low.B >> BaseScaleFieldShift;
        int meanDifference = ((redField - lowRed) + (greenField - lowGreen) + (blueField - lowBlue) + 1) / 3;
        return Math.Clamp(meanDifference, 0, MaxSevenBitField);
    }

    /// <summary>
    /// CEM 15 (HDR RGB + HDR alpha): RGB in values 0–5 exactly as CEM 11 direct, then the alpha pair
    /// in v6/v7 via the simple alpha sub-mode. That sub-mode is selected when both v6[7] and v7[7] are
    /// set, after which each alpha value is a 7-bit <c>(channel &gt;&gt; 9)</c> field, decoded as
    /// <c>field &lt;&lt; 5</c> to the 12-bit intermediate (then <c>&lt;&lt; 4</c> to FP16). All four
    /// channels blend in the LNS domain, so this needs no special reconstruction handling.
    /// </summary>
    private static void EncodeRgbDirectHdrAlpha(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        EncodeRgbDirect(colorRange, colorValues, low, high);
        colorValues[6] = QuantizeByte(SimpleAlphaSelectorFlag | ((low.A >> 9) & AlphaFieldMask), colorRange);
        colorValues[7] = QuantizeByte(SimpleAlphaSelectorFlag | ((high.A >> 9) & AlphaFieldMask), colorRange);
    }

    /// <summary>
    /// Rounded LNS-domain luma (mean of the R/G/B channels). Luma modes are chosen only for grey
    /// content, where the three channels are equal, so the mean is exact there.
    /// </summary>
    private static int LnsLuma(RgbaHdrColor color) => (color.R + color.G + color.B + 1) / 3;

    private static int QuantizeByte(int value, int colorRange)
        => Quantization.QuantizeCEValueToRange(Math.Clamp(value, 0, byte.MaxValue), colorRange);
}
