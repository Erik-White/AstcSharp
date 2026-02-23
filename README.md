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

## Future improvements

- Improved performance
- Encoding

## References

This implementation is based on:

- **ASTC Specification**: [Khronos Data Format Specification](https://www.khronos.org/registry/DataFormat/specs/1.3/dataformat.1.3.html) - The official ASTC texture compression format specification
- **ARM ASTC Codec**: [github.com/ARM-software/astc-encoder](https://github.com/ARM-software/astc-encoder)
- **Google astc-codec**: [github.com/google/astc-codec](https://github.com/google/astc-codec)

## License

See [LICENSE](LICENSE) for details.
