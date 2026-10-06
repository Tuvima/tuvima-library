namespace MediaEngine.Web.Tests;

public sealed class TypographySystemGuardTests
{
    [Fact]
    public void Tokens_DefineTheUiAndBrandFontRoles()
    {
        var tokens = ReadRepoFile(@"src\MediaEngine.Web\wwwroot\tuvima.tokens.css");

        Assert.Contains("--font-ui: \"Segoe UI Variable\", \"Segoe UI\", system-ui, -apple-system,", tokens, StringComparison.Ordinal);
        Assert.Contains("--font-brand: \"Montserrat\", sans-serif;", tokens, StringComparison.Ordinal);
        Assert.Contains("--media-identity-font-family: var(--font-ui);", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalStyles_UseTheUiFontWithoutRemoteInterfaceFonts()
    {
        var styles = ReadRepoFile(@"src\MediaEngine.Web\wwwroot\app.css");

        Assert.Contains("font-family: var(--font-ui);", styles, StringComparison.Ordinal);
        Assert.Contains("--app-font-sans: var(--font-ui);", styles, StringComparison.Ordinal);
        Assert.Contains("--app-font-brand: var(--font-brand);", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("fonts.googleapis.com", styles, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nunito", styles, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeTheme_UsesTheCompleteSystemUiStack()
    {
        var theme = ReadRepoFile(@"src\MediaEngine.Web\wwwroot\tuvima.tokens.css");

        foreach (var family in new[]
                 {
                     "Segoe UI Variable", "Segoe UI", "system-ui", "-apple-system",
                     "BlinkMacSystemFont", "Helvetica Neue", "Arial", "sans-serif",
                 })
        {
            Assert.Contains(family, theme, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Nunito", theme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Montserrat_IsLimitedToBrandTokensFontLoadingAndReaderChoice()
    {
        var webRoot = RepoPath(@"src\MediaEngine.Web");
        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"wwwroot\tuvima.tokens.css",
            @"wwwroot\app.css",
            @"Components\Playback\ReaderSettingsPanel.razor",
            @"Components\Pages\EpubReader.razor",
        };

        var violations = Directory
            .EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"wwwroot{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("Montserrat", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(webRoot, path))
            .Where(path => !allowedFiles.Contains(path))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ApplicationStyles_DoNotReintroduceRemoteOrLegacyInterfaceFonts()
    {
        var violations = EnumerateApplicationStyleSources()
            .Select(path => new { Path = path, Content = File.ReadAllText(path) })
            .Where(file => file.Content.Contains("Nunito", StringComparison.OrdinalIgnoreCase)
                           || file.Content.Contains("Roboto", StringComparison.OrdinalIgnoreCase)
                           || file.Content.Contains("fonts.googleapis.com", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(RepoPath(@"src\MediaEngine.Web"), file.Path))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ApplicationStyles_UsePortableNumericFontWeights()
    {
        var violations = EnumerateApplicationStyleSources()
            .Where(path => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(path), @"font-weight:\s*(?<weight>\d+)")
                .Select(match => new
                {
                    Path = Path.GetRelativePath(RepoPath(@"src\MediaEngine.Web"), path),
                    Weight = int.Parse(match.Groups["weight"].Value, System.Globalization.CultureInfo.InvariantCulture),
                }))
            .Where(match => match.Weight % 100 != 0)
            .Select(match => $"{match.Path}: {match.Weight}")
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void OperatingSystemInterfaceFonts_AreNotBundled()
    {
        var fontsRoot = RepoPath(@"src\MediaEngine.Web\wwwroot\fonts");
        var prohibitedNames = new[] { "Segoe", "SFPro", "SF-Pro", "Roboto" };

        var violations = Directory
            .EnumerateFiles(fontsRoot, "*", SearchOption.AllDirectories)
            .Where(path => prohibitedNames.Any(name => Path.GetFileName(path).Contains(name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(violations);
    }

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(RepoPath(relativePath));

    private static IEnumerable<string> EnumerateApplicationStyleSources()
    {
        var webRoot = RepoPath(@"src\MediaEngine.Web");

        return Directory
            .EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"wwwroot{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepoPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
