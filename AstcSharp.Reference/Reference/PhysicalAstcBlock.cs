// Port of astc-codec/src/decoder/physical_astc_block.{h,cc}
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AstcSharp.Reference
{
    // A PhysicalASTCBlock contains all 128 bits and the logic for decoding the
    // various internals of an ASTC block. This is a C# port of the reference
    // implementation sufficient for the unit tests.
    public readonly struct PhysicalAstcBlock
    {
        public const int kSizeInBytes = 16;

        private readonly UInt128Ex astc_bits_;

        public PhysicalAstcBlock(UInt128Ex bits)
        {
            astc_bits_ = bits;
        }

        public PhysicalAstcBlock(ulong low)
        {
            astc_bits_ = new UInt128Ex(low, 0UL);
        }

        public PhysicalAstcBlock(ulong low, ulong high)
        {
            // Store as UInt128Ex(lowBits, highBits) where the first ctor arg is
            // the low 64-bit word.
            astc_bits_ = new UInt128Ex(low, high);
        }

        public UInt128Ex GetBlockBits() => astc_bits_;

        // Public API
        public (int, int)? WeightGridDims()
        {
            var maybe = DecodeWeightProps(astc_bits_, out var _);
            if (maybe == null) return null;
            if (IsIllegalEncoding() != null) return null;
            return (maybe.Value.width, maybe.Value.height);
        }

        public int? WeightRange()
        {
            var maybe = DecodeWeightProps(astc_bits_, out var _);
            if (maybe == null) return null;
            if (IsIllegalEncoding() != null) return null;
            return maybe.Value.range;
        }

        public bool IsVoidExtent()
        {
            // If it's illegal encoding, not void extent
            if (IsIllegalEncoding() != null) return false;
            return DecodeBlockMode(astc_bits_) == BlockMode.kVoidExtent;
        }

        public int[]? VoidExtentCoords()
        {
            if (IsIllegalEncoding() != null || !IsVoidExtent()) return null;

            // If void extent coords are all 1's then these are not valid void extent coords
            ulong ve_mask = 0xFFFFFFFFFFFFFDFFUL;
            ulong const_blk_mode = 0xFFFFFFFFFFFFFDFCUL;
            if ((ve_mask & astc_bits_.Low) == const_blk_mode)
            {
                return null;
            }

            return DecodeVoidExtentCoords(astc_bits_);
        }

        public bool IsDualPlane()
        {
            if (IsIllegalEncoding() != null) return false;
            return DecodeDualPlaneBit(astc_bits_);
        }

        public int? DualPlaneChannel()
        {
            if (!IsDualPlane()) return null;
            int dual_plane_start_pos = DecodeDualPlaneBitStartPos(astc_bits_);
            var plane_bits = BitOps.GetBits(astc_bits_, dual_plane_start_pos, 2);
            return (int)plane_bits.Low;
        }

        public string? IsIllegalEncoding()
        {
            // If the block is not a void extent block, then it must have
            // weights specified. DecodeWeightProps will return the weight specifications
            // if they exist and are legal according to C.2.24, and will otherwise be
            // empty.
            var block_mode = DecodeBlockMode(astc_bits_);
            if (block_mode != BlockMode.kVoidExtent)
            {
                var props = DecodeWeightProps(astc_bits_, out var error);
                if (props == null)
                {
                    return error;
                }
            }

            if (block_mode == BlockMode.kVoidExtent)
            {
                if (BitOps.GetBits(astc_bits_, 10, 2).Low != 0x3UL)
                {
                    return "Reserved bits set for void extent block";
                }

                var coords = DecodeVoidExtentCoords(astc_bits_);
                bool coords_all_1s = true;
                foreach (var coord in coords) coords_all_1s &= coord == ((1 << 13) - 1);

                if (!coords_all_1s && (coords[0] >= coords[1] || coords[2] >= coords[3]))
                {
                    return "Void extent texture coordinates are invalid";
                }
            }

            if (block_mode != BlockMode.kVoidExtent)
            {
                int num_color_vals = DecodeNumColorValues(astc_bits_);
                if (num_color_vals > 18) return "Too many color values";

                int num_partitions = DecodeNumPartitions(astc_bits_);
                int dual_plane_start_pos = DecodeDualPlaneBitStartPos(astc_bits_);
                int color_start_bit = (num_partitions == 1) ? 17 : 29;

                int required_color_bits = ((13 * num_color_vals) + 4) / 5;
                int available_color_bits = dual_plane_start_pos - color_start_bit;
                if (available_color_bits < required_color_bits) return "Not enough color bits";

                if (num_partitions == 4 && DecodeDualPlaneBit(astc_bits_)) return "Both four partitions and dual plane specified";
            }

            return null;
        }

        public int? NumWeightBits()
        {
            if (IsIllegalEncoding() != null) return null;
            if (IsVoidExtent()) return null;
            return DecodeNumWeightBits(astc_bits_);
        }

        public int? WeightStartBit()
        {
            if (IsIllegalEncoding() != null) return null;
            if (IsVoidExtent()) return null;
            return 128 - DecodeNumWeightBits(astc_bits_);
        }

        public int? NumPartitions()
        {
            if (IsIllegalEncoding() != null) return null;
            if (DecodeBlockMode(astc_bits_) == BlockMode.kVoidExtent) return null;
            return DecodeNumPartitions(astc_bits_);
        }

        public int? PartitionID()
        {
            var num_partitions = NumPartitions();
            if (!num_partitions.HasValue || num_partitions.Value == 1) return null;
            ulong low_bits = astc_bits_.Low;
            return (int)BitOps.GetBits(low_bits, 13, 10);
        }

        public ColorEndpointMode? GetEndpointMode(int partition)
        {
            if (IsIllegalEncoding() != null) return null;
            if (DecodeBlockMode(astc_bits_) == BlockMode.kVoidExtent) return null;
            if (partition < 0 || DecodeNumPartitions(astc_bits_) <= partition) return null;
            return DecodeEndpointMode(astc_bits_, partition);
        }

        public int? ColorStartBit()
        {
            if (IsVoidExtent()) return 64;
            var num_partitions = NumPartitions();
            if (!num_partitions.HasValue) return null;
            return (num_partitions.Value == 1) ? 17 : 29;
        }

        public int? NumColorValues()
        {
            if (IsVoidExtent()) return 4;
            if (IsIllegalEncoding() != null) return null;
            return DecodeNumColorValues(astc_bits_);
        }

        public int? NumColorBits()
        {
            if (IsIllegalEncoding() != null) return null;
            if (IsVoidExtent()) return 64;
            GetColorValuesInfo(out int color_bits, out _);
            return color_bits;
        }

        public int? ColorValuesRange()
        {
            if (IsIllegalEncoding() != null) return null;
            if (IsVoidExtent()) return (1 << 16) - 1;
            GetColorValuesInfo(out _, out int color_range);
            return color_range;
        }

        // Internal helpers - follow the logic from the reference implementation.
        private enum BlockMode
        {
            kB4_A2,
            kB8_A2,
            kA2_B8,
            kA2_B6,
            kB2_A2,
            k12_A2,
            kA2_12,
            k6_10,
            k10_6,
            kA6_B6,
            kVoidExtent,
        }

        private struct WeightGridProperties { public int width; public int height; public int range; }

        private static BlockMode? DecodeBlockMode(UInt128Ex astc_bits)
        {
            const int kVoidExtentMaskBits = 9;
            const uint kVoidExtentMask = 0x1FC;
            // The void-extent header can appear in either 64-bit word depending
            // on the block representation. Check both low and high words.
            if (BitOps.GetBits(astc_bits.Low, 0, kVoidExtentMaskBits) == kVoidExtentMask ||
                BitOps.GetBits(astc_bits.High, 0, kVoidExtentMaskBits) == kVoidExtentMask)
            {
                return PhysicalAstcBlock.BlockMode.kVoidExtent;
            }

            // For decoding block mode fields the relevant bits live in the low
            // 64-bit word of the canonical representation. Use the stored low
            // word for the remaining logic.
            ulong low_bits = astc_bits.Low;
            Console.WriteLine($"DecodeBlockMode: low_bits=0x{low_bits:X16}");
            if (BitOps.GetBits(low_bits, 0, 2) != 0)
            {
                var mode_bits = BitOps.GetBits(low_bits, 2, 2);
                Console.WriteLine($"DecodeBlockMode: first path mode_bits={mode_bits}");
                switch (mode_bits)
                {
                    case 0: return PhysicalAstcBlock.BlockMode.kB4_A2;
                    case 1: return PhysicalAstcBlock.BlockMode.kB8_A2;
                    case 2: return PhysicalAstcBlock.BlockMode.kA2_B8;
                    case 3:
                        return (BitOps.GetBits(low_bits, 8, 1) != 0) ? PhysicalAstcBlock.BlockMode.kB2_A2 : PhysicalAstcBlock.BlockMode.kA2_B6;
                }
            }
            else
            {
                var mode_bits = BitOps.GetBits(low_bits, 5, 4);
                Console.WriteLine($"DecodeBlockMode: second path mode_bits=0x{mode_bits:X}");
                if ((mode_bits & 0xC) == 0x0)
                {
                    if (BitOps.GetBits(low_bits, 0, 4) == 0) return null; // reserved
                    else return PhysicalAstcBlock.BlockMode.k12_A2;
                }
                else if ((mode_bits & 0xC) == 0x4) return PhysicalAstcBlock.BlockMode.kA2_12;
                else if (mode_bits == 0xC) return PhysicalAstcBlock.BlockMode.k6_10;
                else if (mode_bits == 0xD) return PhysicalAstcBlock.BlockMode.k10_6;
                else if ((mode_bits & 0xC) == 0x8) return PhysicalAstcBlock.BlockMode.kA6_B6;
            }

            return null;
        }

        private static WeightGridProperties? DecodeWeightProps(UInt128Ex astc_bits, out string? error)
        {
            error = null;
            var block_mode = DecodeBlockMode(astc_bits);
            if (block_mode == null)
            {
                error = "Reserved block mode";
                return null;
            }

            var props = new WeightGridProperties();
            Console.WriteLine($"DecodeWeightProps: astc_bits.Low=0x{astc_bits.Low:X16} High=0x{astc_bits.High:X16}");
            uint low32 = (uint)(astc_bits.Low & 0xFFFFFFFFUL);
            // diagnostic
            // Console.WriteLine($"DecodeWeightProps: low32=0x{low32:X8} block_mode={block_mode}");

            switch (block_mode.Value)
            {
                case BlockMode.kB4_A2:
                    {
                        int a = (int)BitOps.GetBits(low32, 5, 2);
                        int b = (int)BitOps.GetBits(low32, 7, 2);
                        props.width = b + 4; props.height = a + 2;
                    }
                    break;
                case BlockMode.kB8_A2:
                    {
                        int a = (int)BitOps.GetBits(low32, 5, 2);
                        int b = (int)BitOps.GetBits(low32, 7, 2);
                        props.width = b + 8; props.height = a + 2;
                    }
                    break;
                case BlockMode.kA2_B8:
                    {
                        int a = (int)BitOps.GetBits(low32, 5, 2);
                        int b = (int)BitOps.GetBits(low32, 7, 2);
                        props.width = a + 2; props.height = b + 8;
                    }
                    break;
                case BlockMode.kA2_B6:
                    {
                        int a = (int)BitOps.GetBits(low32, 5, 2);
                        int b = (int)BitOps.GetBits(low32, 7, 1);
                        props.width = a + 2; props.height = b + 6;
                    }
                    break;
                case BlockMode.kB2_A2:
                    {
                        int a = (int)BitOps.GetBits(low32, 5, 2);
                        int b = (int)BitOps.GetBits(low32, 7, 1);
                        props.width = b + 2; props.height = a + 2;
                    }
                    break;
                case BlockMode.k12_A2:
                    {
                        int a = (int)BitOps.GetBits(low32, 5, 2);
                        props.width = 12; props.height = a + 2;
                    }
                    break;
                case BlockMode.kA2_12:
                    {
                        int a = (int)BitOps.GetBits(low32, 5, 2);
                        props.width = a + 2; props.height = 12;
                    }
                    break;
                case BlockMode.k6_10:
                    props.width = 6; props.height = 10; break;
                case BlockMode.k10_6:
                    props.width = 10; props.height = 6; break;
                case BlockMode.kA6_B6:
                    {
                        int a = (int)BitOps.GetBits(low32, 5, 2);
                        int b = (int)BitOps.GetBits(low32, 9, 2);
                        props.width = a + 6; props.height = b + 6;
                    }
                    break;
                case BlockMode.kVoidExtent:
                    error = "Void extent block has no weight grid";
                    return null;
                default:
                    Debug.Assert(false, "Error decoding weight grid");
                    error = "Internal error";
                    return null;
            }

            uint r = (uint)BitOps.GetBits(low32, 4, 1);
            switch (block_mode.Value)
            {
                case BlockMode.kB4_A2:
                case BlockMode.kB8_A2:
                case BlockMode.kA2_B8:
                case BlockMode.kA2_B6:
                case BlockMode.kB2_A2:
                    r |= (uint)(BitOps.GetBits(low32, 0, 2) << 1);
                    break;
                case BlockMode.k12_A2:
                case BlockMode.kA2_12:
                case BlockMode.k6_10:
                case BlockMode.k10_6:
                case BlockMode.kA6_B6:
                    r |= (uint)(BitOps.GetBits(low32, 2, 2) << 1);
                    break;
                default:
                    error = "Internal error"; return null;
            }

            uint h = (uint)BitOps.GetBits(low32, 9, 1);
            if (block_mode.Value == BlockMode.kA6_B6) h = 0;

            int[] kWeightRanges = new int[] { -1, -1, 1, 2, 3, 4, 5, 7, -1, -1, 9, 11, 15, 19, 23, 31 };
            int idx = (int)((h << 3) | r);
            if (idx < 0 || idx >= kWeightRanges.Length)
            {
                // Detailed diagnostics
                Console.WriteLine($"DecodeWeightProps: reserved range detected. low32=0x{low32:X8} block_mode={block_mode} r0={BitOps.GetBits(low32,4,1)} r_lowbits={BitOps.GetBits(low32,0,2)} r_altbits={BitOps.GetBits(low32,2,2)} hbit={BitOps.GetBits(low32,9,1)} computed_r={r} computed_h={h} idx={idx}");
                // Try alternative interpretation using high 32 bits
                uint altLow32 = (uint)((astc_bits.High) & 0xFFFFFFFFUL);
                Console.WriteLine($"Attempting altLow32=0x{altLow32:X8}");
                uint alt_r = (uint)BitOps.GetBits(altLow32, 4, 1);
                switch (block_mode.Value)
                {
                    case BlockMode.kB4_A2:
                    case BlockMode.kB8_A2:
                    case BlockMode.kA2_B8:
                    case BlockMode.kA2_B6:
                    case BlockMode.kB2_A2:
                        alt_r |= (uint)(BitOps.GetBits(altLow32, 0, 2) << 1);
                        break;
                    default:
                        alt_r |= (uint)(BitOps.GetBits(altLow32, 2, 2) << 1);
                        break;
                }
                uint alt_h = (uint)BitOps.GetBits(altLow32, 9, 1);
                int altIdx = (int)((alt_h << 3) | alt_r);
                Console.WriteLine($"Alt computed r={alt_r} h={alt_h} idx={altIdx}");
                if (altIdx >= 0 && altIdx < kWeightRanges.Length && kWeightRanges[altIdx] >= 0)
                {
                    Console.WriteLine("Using altHigh-derived header fields to decode weight range");
                    r = alt_r; h = alt_h; idx = altIdx; low32 = altLow32; // adopt the alternate low32 for subsequent logic
                }
                else
                {
                    // print bits 0..15
                    string bits = "";
                    for (int i = 0; i < 16; ++i)
                    {
                        bits = (BitOps.GetBits(low32, i, 1) == 1 ? '1' : '0') + bits;
                    }
                    Console.WriteLine($"low32 bits[15..0]={bits}");
                    error = "Reserved range for weight bits"; return null;
                }
            }
            if (idx < 0 || idx >= kWeightRanges.Length) { error = "Reserved range for weight bits"; return null; }
            props.range = kWeightRanges[idx];
            if (props.range < 0) { error = "Reserved range for weight bits"; return null; }

            int num_weights = props.width * props.height;
            if (DecodeDualPlaneBit(astc_bits)) num_weights *= 2;
            const int kMaxNumWeights = 64;
            if (kMaxNumWeights < num_weights) { error = "Too many weights specified"; return null; }

            int bit_count = IntegerSequenceCodec.GetBitCountForRange(num_weights, props.range);
            const int kWeightGridMinBitLength = 24;
            const int kWeightGridMaxBitLength = 96;
            if (bit_count < kWeightGridMinBitLength) { error = "Too few bits required for weight grid"; return null; }
            if (kWeightGridMaxBitLength < bit_count) { error = "Too many bits required for weight grid"; return null; }

            return props;
        }

        private static int[] DecodeVoidExtentCoords(UInt128Ex astc_bits)
        {
            ulong low_bits = astc_bits.Low;
            var coords = new int[4];
            for (int i = 0; i < 4; ++i)
            {
                coords[i] = (int)BitOps.GetBits(low_bits, 12 + 13 * i, 13);
            }
            return coords;
        }

        private static bool DecodeDualPlaneBit(UInt128Ex astc_bits)
        {
            var block_mode = DecodeBlockMode(astc_bits);
            if (block_mode == BlockMode.kVoidExtent) return false;
            if (block_mode == BlockMode.kA6_B6) return false;
            const int kDualPlaneBitPosition = 10;
            return BitOps.GetBits(astc_bits, kDualPlaneBitPosition, 1).Low != 0UL;
        }

        private static int DecodeNumPartitions(UInt128Ex astc_bits)
        {
            const int kNumPartitionsBitPosition = 11;
            const int kNumPartitionsBitLength = 2;
            ulong low_bits = astc_bits.Low;
            int num_partitions = 1 + (int)BitOps.GetBits(low_bits, kNumPartitionsBitPosition, kNumPartitionsBitLength);
            Debug.Assert(num_partitions > 0 && num_partitions <= 4);
            return num_partitions;
        }

        private static int DecodeNumWeightBits(UInt128Ex astc_bits)
        {
            var maybe = DecodeWeightProps(astc_bits, out var _);
            if (maybe == null) return 0;
            var props = maybe.Value;
            int num_weights = props.width * props.height;
            if (DecodeDualPlaneBit(astc_bits)) num_weights *= 2;
            return IntegerSequenceCodec.GetBitCountForRange(num_weights, props.range);
        }

        private static int DecodeNumExtraCEMBits(UInt128Ex astc_bits)
        {
            int num_partitions = DecodeNumPartitions(astc_bits);
            if (num_partitions == 1) return 0;
            const int kSharedCEMBitPosition = 23;
            const int kSharedCEMBitLength = 2;
            var shared_cem = BitOps.GetBits(astc_bits, kSharedCEMBitPosition, kSharedCEMBitLength);
            if (shared_cem.Low == 0UL) return 0;
            int[] extra_cem_bits_for_partition = new int[] { 0, 2, 5, 8 };
            return extra_cem_bits_for_partition[num_partitions - 1];
        }

        private static int DecodeDualPlaneBitStartPos(UInt128Ex astc_bits)
        {
            int start_pos = 128 - DecodeNumWeightBits(astc_bits) - DecodeNumExtraCEMBits(astc_bits);
            if (DecodeDualPlaneBit(astc_bits)) return start_pos - 2;
            return start_pos;
        }

        private static ColorEndpointMode DecodeEndpointMode(UInt128Ex astc_bits, int partition)
        {
            int num_partitions = DecodeNumPartitions(astc_bits);
            Debug.Assert(partition >= 0 && partition < num_partitions);
            ulong low_bits = astc_bits.Low;
            if (num_partitions == 1)
            {
                ulong cem = BitOps.GetBits(low_bits, 13, 4);
                return (ColorEndpointMode)cem;
            }

            if (DecodeNumExtraCEMBits(astc_bits) == 0)
            {
                ulong shared_cem = BitOps.GetBits(low_bits, 25, 4);
                return (ColorEndpointMode)shared_cem;
            }

            ulong cemval = BitOps.GetBits(low_bits, 23, 6);
            int base_cem = (int)(((cemval & 0x3) - 1) * 4);
            cemval >>= 2;

            int num_extra_cem_bits = DecodeNumExtraCEMBits(astc_bits);
            int extra_cem_start_pos = 128 - num_extra_cem_bits - DecodeNumWeightBits(astc_bits);
            var extra_cem = BitOps.GetBits(astc_bits, extra_cem_start_pos, num_extra_cem_bits);
            ulong combined = cemval | (extra_cem.Low << 4);
            ulong cembits = combined;

            int c = -1, m = -1;
            for (int i = 0; i < num_partitions; ++i)
            {
                if (i == partition) c = (int)(cembits & 0x1);
                cembits >>= 1;
            }
            for (int i = 0; i < num_partitions; ++i)
            {
                if (i == partition) m = (int)(cembits & 0x3);
                cembits >>= 2;
            }
            Debug.Assert(c >= 0 && m >= 0);
            int mode = base_cem + 4 * c + m;
            Debug.Assert(mode < (int)ColorEndpointMode.kNumColorEndpointModes);
            return (ColorEndpointMode)mode;
        }

        private static int DecodeNumColorValues(UInt128Ex astc_bits)
        {
            int num_color_values = 0;
            int num_partitions = DecodeNumPartitions(astc_bits);
            for (int i = 0; i < num_partitions; ++i)
            {
                var endpoint_mode = DecodeEndpointMode(astc_bits, i);
                num_color_values += Types.NumColorValuesForEndpointMode(endpoint_mode);
            }
            return num_color_values;
        }

        private void GetColorValuesInfo(out int color_bits, out int color_range)
        {
            int dual_plane_start_pos = DecodeDualPlaneBitStartPos(astc_bits_);
            int max_color_bits = dual_plane_start_pos - ColorStartBit().Value;
            int num_color_values = NumColorValues().Value;
            for (int range = 255; range > 0; --range)
            {
                int bitcount = IntegerSequenceCodec.GetBitCountForRange(num_color_values, range);
                if (bitcount <= max_color_bits)
                {
                    color_bits = bitcount;
                    color_range = range;
                    return;
                }
            }
            Debug.Assert(false, "Not enough bits to store color values");
            color_bits = 0; color_range = 0;
        }
    }

    internal static class BitOps
    {
        // Return the specified range as a UInt128Ex (low bits in Low field)
        public static UInt128Ex GetBits(UInt128Ex value, int pos, int len)
        {
//             template<typename T>
            // inline T GetBits(T source, uint32_t offset, uint32_t count) {
            //   static_assert(std::is_same<T, UInt128>::value || std::is_unsigned<T>::value,
            //                 "T must be unsigned.");
            // const uint32_t total_bits = sizeof(T) * 8;
            // assert(count > 0);
            // assert(offset + count <= total_bits);

            // const T mask = count == total_bits ? ~T(0) : ~T(0) >> (total_bits - count);
            // return (source >> offset) & mask;
            if (len == 0) return new UInt128Ex(0);
            var shifted = value >> pos;
            if (len >= 128) return shifted;
            if (len >= 64)
            {
                ulong lowMask = ~0UL;
                int highBits = len - 64;
                ulong highMask = (highBits == 64) ? ~0UL : ((1UL << highBits) - 1UL);
                return new UInt128Ex(shifted.Low & lowMask, shifted.High & highMask);
            }
            else
            {
                ulong mask = (len == 64) ? ~0UL : ((1UL << len) - 1UL);
                return new UInt128Ex(shifted.Low & mask, 0UL);
            }
        }

        // Overload for ulong input
        public static ulong GetBits(ulong value, int pos, int len)
        {
            // if (len == 0) return 0UL;
            // if (len >= 64) return value >> pos;
            // return (value >> pos) & ((1UL << len) - 1UL);
            int total_bits = sizeof(ulong) * 8;
            ulong mask = len == total_bits ? ~0UL : ~0UL >> (total_bits - len);
            return (value >> pos) & mask;
        }
    }
}
