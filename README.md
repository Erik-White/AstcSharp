# AstcSharp

A pure C# library for decoding and encoding ASTC (Adaptive Scalable Texture Compression) textures, supporting both LDR and HDR content.

## Features

- Managed C#, no native dependencies
- Decode ASTC textures to RGBA32 (LDR) or RGBA float / FP16 (HDR)
- Encode RGBA32 LDR images to ASTC blocks or `.astc` files
- Linear and sRGB LDR decode modes
- All standard block footprints (4x4 to 12x12)

## Installation

```bash
dotnet add package AstcSharp
```

## Usage

### Decoding
```csharp
using AstcSharp;
using AstcSharp.Core;

byte[] astcData = File.ReadAllBytes("texture.astc");
var footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

// LDR: decode to RGBA32
Span<byte> ldrPixels = AstcDecoder.DecompressImage(astcData, width, height, footprint);

// LDR sRGB: apply the spec's sRGB endpoint expansion (output stays sRGB-encoded)
Span<byte> srgbPixels = AstcDecoder.DecompressImage(astcData, width, height, footprint, LdrDecodeMode.Srgb);

// HDR: decode to RGBA float
Span<float> hdrPixels = AstcDecoder.DecompressHdrImage(astcData, width, height, footprint);
```

### Encoding
```
// Encode an RGBA32 LDR image to ASTC blocks
byte[] blocks = AstcEncoder.CompressImage(rgbaPixels, width, height, footprint);

// Encode to a complete .astc file (16-byte header + blocks)
byte[] astcFile = AstcEncoder.CompressToAstcFile(rgbaPixels, width, height, footprint);
```

## Performance

AstcSharp's decoding performance is competitive with ARM's C++ implementation, with some overhead due to being a pure C# implementation.

```
| Method           | Categories | Mean          | Error        | StdDev       | Ratio  | RatioSD | Allocated | Alloc Ratio |
|----------------- |----------- |--------------:|-------------:|-------------:|-------:|--------:|----------:|------------:|
| Arm_Decode       | Decode     |      37.62 us |     0.582 us |     0.486 us |   1.00 |    0.00 |         - |          NA |
| AstcSharp_Decode | Decode     |      26.53 us |     0.357 us |     0.316 us |   0.71 |    0.01 |         - |          NA |
|                  |            |               |              |              |        |         |           |             |
| Arm_Encode       | Encode     |     828.34 us |     8.152 us |     7.625 us |   1.00 |    0.00 |         - |          NA |
| AstcSharp_Encode | Encode     | 164,935.80 us | 2,025.726 us | 1,894.865 us | 199.13 |    2.42 |   16764 B |          NA |
```

## Future improvements

- 3D block types
- HDR encoding (encoding currently supports LDR only)

## References

This implementation is based on:

- **ASTC Specification**: [Khronos Data Format Specification](https://www.khronos.org/registry/DataFormat/specs/1.3/dataformat.1.3.html) - The official ASTC texture compression format specification
- **ARM ASTC Codec**: [github.com/ARM-software/astc-encoder](https://github.com/ARM-software/astc-encoder)
- **Google astc-codec**: [github.com/google/astc-codec](https://github.com/google/astc-codec)

## License

See [LICENSE](LICENSE) for details.
