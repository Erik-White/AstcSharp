namespace AstcSharp.ColorEncoding;

public static class EndpointEncodingModeExtensions
{
    public static int GetValuesCount(this EndpointEncodingMode mode) => mode switch
    {
        EndpointEncodingMode.DirectLuma => 2,
        EndpointEncodingMode.DirectLumaAlpha or EndpointEncodingMode.BaseScaleRgb => 4,
        EndpointEncodingMode.DirectRbg or EndpointEncodingMode.BaseScaleRgba => 6,
        _ => 8
    };
}