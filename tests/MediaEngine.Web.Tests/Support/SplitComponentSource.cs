namespace MediaEngine.Web.Tests;

/// <summary>
/// Reads a component whose code lives in partial class files beside the Razor markup (<c>Name.razor</c>,
/// <c>Name.razor.cs</c>, <c>Name.Concern.cs</c>) as one text, so a test that pins a string in the component keeps
/// pinning it wherever the string now lives. Wording keys are put back to English the same way
/// <see cref="LocalizedSource"/> does it for the markup.
/// </summary>
internal static class SplitComponentSource
{
    public static string ReadAll(string razorPath)
    {
        var directory = Path.GetDirectoryName(razorPath)!;
        var name = Path.GetFileNameWithoutExtension(razorPath);
        var parts = new List<string> { LocalizedSource.ReadAllText(razorPath) };
        parts.AddRange(Directory
            .EnumerateFiles(directory, name + ".*.cs")
            .Concat(Directory.EnumerateFiles(directory, name + ".razor.cs"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(path => LocalizedSource.Localize(File.ReadAllText(path))));
        return string.Join("\n", parts);
    }
}
