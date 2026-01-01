namespace AstcSharp;

public static class ColorEndpointModeExtensions
{
    public static int GetEndpointModeClass(this ColorEndpointMode mode) => (int)mode / 4;
    public static int GetColorValuesCount(this ColorEndpointMode mode) => (mode.GetEndpointModeClass() + 1) * 2;
}