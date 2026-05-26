namespace AstcSharp.Tests.Utils;

/// <summary>
/// Resolves test input file paths relative to the test data directory.
/// </summary>
internal sealed class TestFile
{
    private const string InputRoot = "TestData/Input";

    private TestFile(string fullPath)
    {
        this.FullPath = fullPath;
        this.Bytes = File.ReadAllBytes(fullPath);
    }

    public string FullPath { get; }

    public byte[] Bytes { get; }

    public static string GetInputFileFullPath(string relativePath)
        => Path.Combine(InputRoot, relativePath);

    public static TestFile Create(string relativePath)
        => new(GetInputFileFullPath(relativePath));
}
