namespace AstcSharp.Core;

/// <summary>
/// Represents the dimensions of an ASTC block footprint.
/// </summary>
public readonly record struct Footprint
{
    private static readonly Footprint[] All =
    [
        new(FootprintType.Footprint4x4, 4, 4),
        new(FootprintType.Footprint5x4, 5, 4),
        new(FootprintType.Footprint5x5, 5, 5),
        new(FootprintType.Footprint6x5, 6, 5),
        new(FootprintType.Footprint6x6, 6, 6),
        new(FootprintType.Footprint8x5, 8, 5),
        new(FootprintType.Footprint8x6, 8, 6),
        new(FootprintType.Footprint8x8, 8, 8),
        new(FootprintType.Footprint10x5, 10, 5),
        new(FootprintType.Footprint10x6, 10, 6),
        new(FootprintType.Footprint10x8, 10, 8),
        new(FootprintType.Footprint10x10, 10, 10),
        new(FootprintType.Footprint12x10, 12, 10),
        new(FootprintType.Footprint12x12, 12, 12),
    ];

    private Footprint(FootprintType type, int width, int height)
    {
        this.Type = type;
        this.Width = width;
        this.Height = height;
        this.PixelCount = width * height;
    }

    /// <summary>Gets the block width in texels.</summary>
    public int Width { get; }

    /// <summary>Gets the block height in texels.</summary>
    public int Height { get; }

    /// <summary>Gets the footprint type enum value.</summary>
    public FootprintType Type { get; }

    /// <summary>Gets the total number of texels in the block (Width * Height).</summary>
    public int PixelCount { get; }

    /// <summary>
    /// Returns the number of blocks spanning <paramref name="imageWidth"/> texels, rounding up so
    /// a partial block at the right edge is counted.
    /// </summary>
    /// <param name="imageWidth">Image width in texels.</param>
    /// <returns>The block count along the image width.</returns>
    public int BlocksWide(int imageWidth) => (imageWidth + this.Width - 1) / this.Width;

    /// <summary>
    /// Returns the number of blocks spanning <paramref name="imageHeight"/> texels, rounding up so
    /// a partial block at the bottom edge is counted.
    /// </summary>
    /// <param name="imageHeight">Image height in texels.</param>
    /// <returns>The block count along the image height.</returns>
    public int BlocksHigh(int imageHeight) => (imageHeight + this.Height - 1) / this.Height;

    /// <summary>
    /// Creates a <see cref="Footprint"/> from the specified <see cref="FootprintType"/>.
    /// </summary>
    /// <param name="type">The footprint type to create a footprint from.</param>
    /// <returns>A <see cref="Footprint"/> matching the specified type.</returns>
    public static Footprint FromFootprintType(FootprintType type)
        => (uint)type < (uint)All.Length
            ? All[(int)type]
            : throw new ArgumentOutOfRangeException(nameof(type), $"Invalid FootprintType: {type}");
}
