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
