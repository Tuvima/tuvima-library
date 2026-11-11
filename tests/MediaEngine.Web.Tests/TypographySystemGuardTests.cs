namespace MediaEngine.Web.Tests;

public sealed class TypographySystemGuardTests
{
    [Fact]
    public void Tokens_DefineTheUiAndBrandFontRoles()
    {
        var tokens = ReadRepoFile(@"src/MediaEngine.Web/wwwroot/tuvima.tokens.css");

        Assert.Contains("--font-ui: \"Inter\", system-ui, -apple-system, sans-serif;", tokens, StringComparison.Ordinal);
        Assert.Contains("--font-brand: \"Tuvima Sans\", sans-serif;", tokens, StringComparison.Ordinal);
        Assert.Contains("--media-identity-font-family: var(--font-ui);", tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalStyles_UseTheUiFontWithoutRemoteInterfaceFonts()
    {
        var styles = ReadRepoFile(@"src/MediaEngine.Web/wwwroot/app.css");

        Assert.Contains("font-family: var(--font-ui);", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("fonts.googleapis.com", styles, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nunito", styles, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeTheme_FallsBackToTheSystemUiStack()
    {
        var theme = ReadRepoFile(@"src/MediaEngine.Web/wwwroot/tuvima.tokens.css");

        foreach (var family in new[]
                 {
                     "Inter", "system-ui", "-apple-system", "sans-serif",
                 })
        {
            Assert.Contains(family, theme, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Nunito", theme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Montserrat_IsNoLongerShippedOrNamedBeyondTheTuvimaSansNoteAndReaderChoice()
    {
        var webRoot = RepoPath(@"src/MediaEngine.Web");
        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wwwroot/app.css",
            "Components/Playback/ReaderSettingsPanel.razor",
            "Components/Pages/EpubReader.razor",
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
            .Select(path => Path.GetRelativePath(webRoot, path).Replace('\\', '/'))
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
            .Select(file => Path.GetRelativePath(RepoPath(@"src/MediaEngine.Web"), file.Path))
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
                    Path = Path.GetRelativePath(RepoPath(@"src/MediaEngine.Web"), path),
                    Weight = int.Parse(match.Groups["weight"].Value, System.Globalization.CultureInfo.InvariantCulture),
                }))
            .Where(match => match.Weight % 100 != 0)
            .Select(match => $"{match.Path}: {match.Weight}")
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void TheInterfaceFontIsInterShippedAsAWoff2FileThatExists()
    {
        var css = ReadRepoFile(@"src/MediaEngine.Web/wwwroot/app.css");
        var tokens = ReadRepoFile(@"src/MediaEngine.Web/wwwroot/tuvima.tokens.css");

        var face = System.Text.RegularExpressions.Regex.Match(css, "@font-face\\s*\\{[^}]*font-family:\\s*\"Inter\"[^}]*\\}");
        Assert.True(face.Success, "app.css must declare an Inter @font-face.");
        var file = System.Text.RegularExpressions.Regex.Match(face.Value, "url\\(\"(?<path>[^\"]+\\.woff2)\"\\)\\s*format\\(\"woff2\"\\)");
        Assert.True(file.Success, "The Inter face must load a woff2 file.");
        Assert.True(File.Exists(RepoPath(@"src/MediaEngine.Web/wwwroot/" + file.Groups["path"].Value)), "The Inter woff2 file is missing from wwwroot.");
        Assert.True(File.Exists(RepoPath(@"src/MediaEngine.Web/wwwroot/fonts/Inter-OFL.txt")), "The Inter licence must sit beside the font.");
        Assert.Matches("--font-ui:\\s*\"Inter\"", tokens);
    }

    [Fact]
    public void EveryFontFaceLoadsAWoff2FileThatExists()
    {
        var css = ReadRepoFile(@"src/MediaEngine.Web/wwwroot/app.css");
        var urls = System.Text.RegularExpressions.Regex.Matches(css, "@font-face\\s*\\{[^}]*\\}")
            .SelectMany(face => System.Text.RegularExpressions.Regex.Matches(face.Value, "url\\(\"(?<path>[^\"]+)\"\\)").Select(m => m.Groups["path"].Value))
            .ToList();

        Assert.NotEmpty(urls);
        Assert.All(urls, url =>
        {
            Assert.EndsWith(".woff2", url, StringComparison.Ordinal);
            Assert.True(File.Exists(RepoPath(@"src/MediaEngine.Web/wwwroot/" + url)), $"{url} is missing.");
        });
        Assert.Empty(Directory.EnumerateFiles(RepoPath(@"src/MediaEngine.Web/wwwroot/fonts"), "*.ttf"));
    }

    // 700 is for page and hero titles only (Look pass v3 D7); everything else is 600 or lighter. The count may only go down.
    private const int BoldWeightCeiling = 18;

    [Fact]
    public void NoStyleUsesWeight800Or900OrBolder_AndBoldIsOnlyForPageAndHeroTitles()
    {
        var all = EnumerateApplicationStyleSources()
            .Where(path => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Select(path => File.ReadAllText(path))
            .ToList();

        var heavy = all.Sum(text => System.Text.RegularExpressions.Regex.Matches(text, @"font-weight:\s*(?:800|900|bolder)\b").Count);
        var bold = all.Sum(text => System.Text.RegularExpressions.Regex.Matches(text, @"font-weight:\s*(?:700\b|bold\b|var\(--tl-font-weight-bold\))").Count);

        Assert.Equal(0, heavy);
        Assert.True(bold <= BoldWeightCeiling, $"Weight 700 rose to {bold} (ceiling {BoldWeightCeiling}). 700 is for page and hero titles only; use 600 for card, row and group titles and 500 for labels.");
    }

    [Fact]
    public void OperatingSystemInterfaceFonts_AreNotBundled()
    {
        var fontsRoot = RepoPath(@"src/MediaEngine.Web/wwwroot/fonts");
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
        var webRoot = RepoPath(@"src/MediaEngine.Web");

        return Directory
            .EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"wwwroot{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"wwwroot{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepoPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
