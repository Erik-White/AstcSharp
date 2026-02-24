# AstcSharp

A pure C# library for decoding ASTC (Adaptive Scalable Texture Compression) textures, supporting both LDR and HDR content.

## Features

- Decode ASTC textures to RGBA32 (LDR) or RGBA128 (HDR)
- All standard block footprints (4x4 to 12x12)

## Installation

```bash
dotnet add package AstcSharp
```

## Usage

```csharp
using AstcSharp;
using AstcSharp.Core;

byte[] astcData = File.ReadAllBytes("texture.astc");
var footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

// LDR: decode to RGBA8888
Span<byte> ldrPixels = AstcDecoder.DecompressImage(astcData, width, height, footprint);

// HDR: decode to RGBA float
Span<float> hdrPixels = AstcDecoder.DecompressHdrImage(astcData, width, height, footprint);
```

## Decoding paths

There are three block decoding paths, chosen automatically:

- **Direct decode** — the default for normal blocks. Decodes weights and endpoints directly from raw bits using batch unquantization, bypassing intermediate allocations.
- **Fused decode** — a SIMD-accelerated path for single-partition, single-plane LDR blocks (the most common case). Decodes and interpolates in one pass without constructing a `LogicalBlock`.
- **Void extent** — handles constant-color blocks via `IntermediateBlock.UnpackVoidExtent`.

## Performance

AstcSharp's performance is competitive with ARM's C++ implementation, with some overhead due to being a pure C# implementation.

LDR
```
┌──────────────┬─────────────────┬────────────┬──────────┬──────────┬─────────┬───────────┐
│    Method    │    FileName     │    Mean    │  Error   │  StdDev  │  Gen0   │ Allocated │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ AstcSharp    │ atlas_small_4x4 │ 1,407.9 μs │ 22.82 μs │ 19.05 μs │ 54.6875 │  674.2 KB │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ ArmReference │ atlas_small_4x4 │   911.7 μs │  7.96 μs │  7.45 μs │       - │         - │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ AstcSharp    │ atlas_small_8x8 │   713.0 μs │ 10.31 μs │  8.61 μs │ 13.6719 │  174.5 KB │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ ArmReference │ atlas_small_8x8 │   595.1 μs │ 11.30 μs │ 12.56 μs │       - │         - │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ AstcSharp    │ footprint_12x12 │     6.2 μs │  0.08 μs │  0.07 μs │       - │         - │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ ArmReference │ footprint_12x12 │     9.6 μs │  0.19 μs │  0.20 μs │       - │         - │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ AstcSharp    │ footprint_4x4   │    23.6 μs │  0.12 μs │  0.10 μs │  1.2512 │   15.6 KB │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ ArmReference │ footprint_4x4   │    13.4 μs │  0.11 μs │  0.10 μs │       - │         - │
└──────────────┴─────────────────┴────────────┴──────────┴──────────┴─────────┴───────────┘
```

HDR
```
┌──────────────┬─────────────────┬────────────┬──────────┬──────────┬─────────┬───────────┐
│    Method    │    FileName     │    Mean    │  Error   │  StdDev  │  Gen0   │ Allocated │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ AstcSharp    │ atlas_small_4x4 │ 1,700.4 μs │ 17.21 μs │ 14.37 μs │ 54.6875 │  674.2 KB │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ ArmReference │ atlas_small_4x4 │   914.7 μs │  7.51 μs │  7.02 μs │       - │         - │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ AstcSharp    │ atlas_small_8x8 │   978.4 μs │  5.41 μs │  4.22 μs │ 13.6719 │  174.5 KB │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ ArmReference │ atlas_small_8x8 │   619.5 μs │ 12.03 μs │ 11.25 μs │       - │         - │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ AstcSharp    │ footprint_12x12 │    10.0 μs │  0.17 μs │  0.16 μs │       - │         - │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ ArmReference │ footprint_12x12 │     9.6 μs │  0.10 μs │  0.09 μs │       - │         - │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ AstcSharp    │ footprint_4x4   │    28.9 μs │  0.43 μs │  0.38 μs │  1.2512 │   15.6 KB │
├──────────────┼─────────────────┼────────────┼──────────┼──────────┼─────────┼───────────┤
│ ArmReference │ footprint_4x4   │    13.2 μs │  0.18 μs │  0.15 μs │       - │         - │
└──────────────┴─────────────────┴────────────┴──────────┴──────────┴─────────┴───────────┘
```

## Future improvements

- 3D block types
- Encoding

## References

This implementation is based on:

- **ASTC Specification**: [Khronos Data Format Specification](https://www.khronos.org/registry/DataFormat/specs/1.3/dataformat.1.3.html) - The official ASTC texture compression format specification
- **ARM ASTC Codec**: [github.com/ARM-software/astc-encoder](https://github.com/ARM-software/astc-encoder)
- **Google astc-codec**: [github.com/google/astc-codec](https://github.com/google/astc-codec)

## License

See [LICENSE](LICENSE) for details.
