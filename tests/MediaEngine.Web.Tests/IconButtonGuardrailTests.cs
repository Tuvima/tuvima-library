using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Icon-only buttons need a tooltip and an accessible name (one-look spec D6, WP8): every <c>AppIconButton</c> in the Dashboard
/// names itself with <c>Tooltip</c> (which also supplies the accessible name), <c>AriaLabel</c> or an <c>aria-label</c> attribute.
/// </summary>
public sealed partial class IconButtonGuardrailTests
{
    [GeneratedRegex(@"<AppIconButton\b((?:=>|[^>])*?)/?>", RegexOptions.Singleline)]
    private static partial Regex IconButtonTag();

    [GeneratedRegex(@"\b(Tooltip|AriaLabel|aria-label)\s*=")]
    private static partial Regex NameAttribute();

    [Fact]
    public void EveryIconOnlyButtonHasATooltipOrAnAccessibleName()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            foreach (Match tag in IconButtonTag().Matches(text))
            {
                if (!NameAttribute().IsMatch(tag.Groups[1].Value))
                {
                    var line = text.AsSpan(0, tag.Index).Count('\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(web, path).Replace('\\', '/')}:{line}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Give every AppIconButton a Tooltip (one-look spec D6): " + string.Join(", ", offenders));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
