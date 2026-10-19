using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Keeps every Settings page inside the one page frame and keeps pages from setting their own outer width.
/// The frame owns the left edge and the content width (see engineering/specs/settings-revamp.md, D1).
/// </summary>
public sealed partial class SettingsFrameGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void EverySettingsSectionCase_IsRenderedInsideTheSettingsPageFrame()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Pages", "Settings.razor"));
        var frameStart = source.IndexOf("<SettingsPageFrame", StringComparison.Ordinal);
        var frameEnd = source.IndexOf("</SettingsPageFrame>", StringComparison.Ordinal);

        Assert.True(frameStart >= 0 && frameEnd > frameStart, "Settings.razor must render its pages inside <SettingsPageFrame>.");
        Assert.Single(Regex.Matches(source, "<SettingsPageFrame\\b"));

        var cases = SectionCase().Matches(source);
        Assert.NotEmpty(cases);
        foreach (Match match in cases)
        {
            Assert.True(
                match.Index > frameStart && match.Index < frameEnd,
                $"{match.Value.Trim()} is rendered outside <SettingsPageFrame>.");
        }
    }

    [Fact]
    public void SettingsPageStyles_DoNotCentreTheirOwnRootWithAMaxWidth()
    {
        var offenders = new List<string>();
        var settingsFolder = Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Settings");
        var files = Directory.EnumerateFiles(settingsFolder, "*.razor.css", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("SettingsPageFrame.razor.css", StringComparison.Ordinal));

        foreach (var path in files)
        {
            var css = Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            foreach (Match rule in CssRule().Matches(css))
            {
                var body = rule.Groups["body"].Value;
                var hasMaxWidth = MaxWidth().IsMatch(body);
                var centres = CentringMargin().IsMatch(body);
                if (hasMaxWidth && centres)
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, path)}: {rule.Groups["selector"].Value.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Settings pages must not centre themselves with a max-width and auto margins; the page frame owns the left edge and width: "
            + string.Join("; ", offenders));
    }

    [GeneratedRegex(@"case\s+SettingsSection\.\w+\s*:")]
    private static partial Regex SectionCase();

    [GeneratedRegex(@"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex CssRule();

    [GeneratedRegex(@"(?<![-\w])max-width\s*:\s*(?!none\b)[^;]+;?")]
    private static partial Regex MaxWidth();

    [GeneratedRegex(@"(?<![-\w])margin(-inline)?\s*:\s*(0\s+auto|auto)\b")]
    private static partial Regex CentringMargin();
}
