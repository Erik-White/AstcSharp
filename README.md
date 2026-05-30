# AstcSharp

A pure C# library for decoding ASTC (Adaptive Scalable Texture Compression) textures, supporting both LDR and HDR content.

## Features

- Managed C#, no native dependencies
- Decode ASTC textures to RGBA32 (LDR) or RGBA float / FP16 (HDR)
- Linear and sRGB LDR decode modes
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

// LDR sRGB: apply the spec's sRGB endpoint expansion (output stays sRGB-encoded)
Span<byte> srgbPixels = AstcDecoder.DecompressImage(astcData, width, height, footprint, LdrDecodeMode.Srgb);

// HDR: decode to RGBA float
Span<float> hdrPixels = AstcDecoder.DecompressHdrImage(astcData, width, height, footprint);
```

## Performance

AstcSharp's performance is competitive with ARM's C++ implementation, with some overhead due to being a pure C# implementation.

LDR
```
| Method                     | FileName        | Mean         | Error      | StdDev     | Median       | Allocated |
|--------------------------- |---------------- |-------------:|-----------:|-----------:|-------------:|----------:|
| AstcSharp_DecompressLdr    | footprint-4x4   |    13.577 us |  0.1627 us |  0.1443 us |    13.564 us |         - |
| AstcSharp_DecompressHdr    | footprint-4x4   |    16.575 us |  0.3153 us |  0.2633 us |    16.533 us |         - |
| ArmReference_DecompressLdr | footprint-4x4   |    11.191 us |  0.1550 us |  0.1374 us |    11.141 us |         - |
| ArmReference_DecompressHdr | footprint-4x4   |    13.932 us |  0.3439 us |  1.0141 us |    14.062 us |         - |
| AstcSharp_DecompressLdr    | footprint-12x12 |     4.384 us |  0.0670 us |  0.0688 us |     4.357 us |         - |
| AstcSharp_DecompressHdr    | footprint-12x12 |     7.733 us |  0.1528 us |  0.1819 us |     7.667 us |         - |
| ArmReference_DecompressLdr | footprint-12x12 |     7.770 us |  0.0729 us |  0.0682 us |     7.767 us |         - |
| ArmReference_DecompressHdr | footprint-12x12 |     7.920 us |  0.0510 us |  0.0426 us |     7.923 us |         - |
| AstcSharp_DecompressLdr    | rgba-4x4        |   962.404 us | 19.2086 us | 32.6176 us |   955.002 us |         - |
| AstcSharp_DecompressHdr    | rgba-4x4        | 1,134.736 us | 22.2962 us | 34.7125 us | 1,144.305 us |         - |
| ArmReference_DecompressLdr | rgba-4x4        |   734.388 us | 14.5948 us | 35.2481 us |   722.839 us |         - |
| ArmReference_DecompressHdr | rgba-4x4        |   716.613 us | 14.0726 us | 18.7865 us |   710.109 us |         - |
| AstcSharp_DecompressLdr    | rgba-8x8        |   422.630 us |  8.2009 us |  8.0543 us |   422.920 us |         - |
| AstcSharp_DecompressHdr    | rgba-8x8        |   629.957 us | 10.5187 us | 15.0857 us |   624.104 us |         - |
| ArmReference_DecompressLdr | rgba-8x8        |   480.440 us |  3.3241 us |  3.1094 us |   479.974 us |         - |
| ArmReference_DecompressHdr | rgba-8x8        |   492.185 us |  4.7352 us |  4.1977 us |   491.723 us |         - |
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
