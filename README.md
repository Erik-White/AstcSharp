# AstcSharp

A C# library for decoding ASTC (Adaptive Scalable Texture Compression) compressed textures.

## Overview

ASTC is a lossy block-based texture compression format designed for real-time graphics applications. It provides flexible compression rates and quality levels, making it ideal for mobile and embedded GPU platforms.

AstcSharp is a pure C# implementation of the ASTC decompression algorithm, allowing you to decode ASTC-compressed textures to RGBA8888 format.

## Features

- Decode ASTC compressed data to RGBA8888 format
- Support for all standard ASTC block footprints (4x4, 5x4, 5x5, 6x5, 6x6, 8x5, 8x6, 8x8, 10x5, 10x6, 10x8, 10x10, 12x10, 12x12)
- Load and decode .astc files
- Clean, minimal public API surface

## Installation

```bash
dotnet add package AstcSharp
```

## Usage

### Decoding ASTC data from a byte array

```csharp
using AstcSharp;
using AstcSharp.Core;

// Your ASTC compressed data
byte[] astcData = File.ReadAllBytes("texture.astc");

// Image dimensions and block footprint
int width = 512;
int height = 512;
FootprintType footprint = FootprintType.Footprint4x4;

// Decode to RGBA8888
Span<byte> rgbaPixels = AstcDecoder.ASTCDecompressToRGBA(
    astcData,
    width,
    height,
    footprint
);

// rgbaPixels now contains width * height * 4 bytes in RGBA order
```

### Decoding an ASTC file

```csharp
using AstcSharp;
using AstcSharp.IO;

// Load an ASTC file (includes header with dimensions and footprint)
byte[] fileData = File.ReadAllBytes("texture.astc");
AstcFile astcFile = AstcFile.FromMemory(fileData);

// Decode to RGBA8888
Span<byte> rgbaPixels = AstcDecoder.DecompressToImage(astcFile);
```

## References

This implementation is based on:

- **ASTC Specification**: [Khronos Data Format Specification](https://www.khronos.org/registry/DataFormat/specs/1.3/dataformat.1.3.html) - The official ASTC texture compression format specification
- **Google astc-codec**: [github.com/google/astc-codec](https://github.com/google/astc-codec)

## License

See [LICENSE](LICENSE) for details.
