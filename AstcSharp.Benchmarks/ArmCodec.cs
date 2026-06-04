using AstcEncoder;
using AstcSharp.Core;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Shared helpers for driving the ARM reference codec (astcenc) from the benchmarks: the identity
/// channel swizzle, an error-to-exception guard, and LDR/HDR context creation. Centralises the
/// boilerplate that the ARM-comparison benchmarks would otherwise each repeat.
/// </summary>
internal static class ArmCodec
{
    public static readonly AstcencSwizzle IdentitySwizzle = new()
    {
        r = AstcencSwz.AstcencSwzR,
        g = AstcencSwz.AstcencSwzG,
        b = AstcencSwz.AstcencSwzB,
        a = AstcencSwz.AstcencSwzA,
    };

    /// <summary>
    /// Allocates an astcenc context for the given footprint, profile, quality preset, and flags.
    /// </summary>
    public static AstcencContext CreateContext(Footprint footprint, AstcencProfile profile, float preset, AstcencFlags flags)
    {
        ThrowOnError(
            Astcenc.AstcencConfigInit(
                profile, (uint)footprint.Width, (uint)footprint.Height, blockZ: 1, preset, flags, out AstcencConfig config),
            "ConfigInit");
        ThrowOnError(Astcenc.AstcencContextAlloc(ref config, threadCount: 1, out AstcencContext context), "ContextAlloc");
        return context;
    }

    /// <summary>
    /// Throws if <paramref name="error"/> is not success, naming the failed <paramref name="operation"/>.
    /// </summary>
    public static void ThrowOnError(AstcencError error, string operation)
    {
        if (error != AstcencError.AstcencSuccess)
        {
            throw new InvalidOperationException($"ARM ASTC {operation} failed: {Astcenc.GetErrorString(error) ?? error.ToString()}");
        }
    }
}
