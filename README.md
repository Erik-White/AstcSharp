# AstcSharp

A pure C# library for decoding and encoding ASTC (Adaptive Scalable Texture Compression) textures, supporting both LDR and HDR content.

## Features

- Managed C#, no native dependencies
- Stream-to-stream API (sync + async) that processes one block-row band at a time, bounding memory independently of image size
- Decode ASTC textures to RGBA32 (LDR) or RGBA float / FP16 (HDR)
- Encode RGBA32 LDR images and FP16 HDR images to ASTC blocks
- Linear and sRGB LDR decode modes
- All standard block footprints (4x4 to 12x12)
- UASTC LDR decoding (Basis Universal)

## Installation

```bash
dotnet add package AstcSharp
```

## Usage

The API is **stream-to-stream**: decode and encode read ASTC blocks / pixels from a source `Stream`
and write the result to a destination `Stream`, processing one block-row band at a time so peak
memory is bounded to a single band rather than the whole image. You pass the image dimensions and
footprint explicitly and the source should hold raw block/pixel data.

### Decoding
```csharp
using AstcSharp;
using AstcSharp.Core;

var footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
using var source = File.OpenRead("texture-blocks.bin"); // raw ASTC blocks
using var destination = new MemoryStream();

// LDR: decode to RGBA32
AstcDecoder.DecompressImage(source, destination, width, height, footprint);

// LDR sRGB: apply the spec's sRGB endpoint expansion (output stays sRGB-encoded)
AstcDecoder.DecompressImage(source, destination, width, height, footprint, LdrDecodeMode.Srgb);

// HDR: decode to RGBA float / FP16 Half, written as little-endian channels (RGBA, row-major)
AstcDecoder.DecompressHdrImage(source, destination, width, height, footprint);
AstcDecoder.DecompressHdrImageHalf(source, destination, width, height, footprint);

// Async (e.g. file/network streams)
await AstcDecoder.DecompressImageAsync(source, destination, width, height, footprint, cancellationToken: token);

// UASTC (Basis Universal, always 4x4): decode raw UASTC block data to RGBA32
UastcDecoder.DecompressImage(uastcSource, destination, width, height);
```

### Encoding
```csharp
using var pixelSource = new MemoryStream(rgbaPixels); // RGBA32, row-major
using var blockDestination = new MemoryStream();

// Encode an RGBA32 LDR image to ASTC blocks
AstcEncoder.CompressImage(pixelSource, blockDestination, width, height, footprint);
await AstcEncoder.CompressImageAsync(pixelSource, blockDestination, width, height, footprint, token);

// Encode an HDR image (FP16 RGBA)
using var hdrSource = new MemoryStream(fp16Pixels);
AstcEncoder.CompressHdrImage(hdrSource, blockDestination, width, height, footprint);
await AstcEncoder.CompressHdrImageAsync(hdrSource, blockDestination, width, height, footprint, token);
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
- Better encoding performance

## References

This implementation is based on:

- **ASTC Specification**: [Khronos Data Format Specification](https://www.khronos.org/registry/DataFormat/specs/1.3/dataformat.1.3.html) — the official ASTC texture compression format specification
- **ARM astc-encoder**: [github.com/ARM-software/astc-encoder](https://github.com/ARM-software/astc-encoder)
- **Google astc-codec**: [github.com/google/astc-codec](https://github.com/google/astc-codec)
- **Basis Universal** (UASTC decoding): [github.com/BinomialLLC/basis_universal](https://github.com/BinomialLLC/basis_universal)

## License

Licensed under the Apache License, Version 2.0 — see [LICENSE](LICENSE). Third-party attributions are in [NOTICE](NOTICE).
