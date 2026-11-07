using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look pass v3 D8: a tooltip exists only for an icon-only control, a disabled control that says why, or text that is cut off.
/// The global title script is retired, so the browser's own title="..." is used only where it has real meaning (img, iframe, abbr,
/// time), and no tooltip repeats the visible text of the control it sits on.
/// </summary>
public sealed class TooltipGuardrailTests : AsyncBunitContext
{
    private static readonly HashSet<string> TitleAllowlist = new(StringComparer.OrdinalIgnoreCase) { "img", "iframe", "abbr", "time" };

    [Fact]
    public void NoTitleAttributeOutsideTheAllowlist()
    {
        var offenders = new List<string>();
        foreach (var path in RazorFiles())
        {
            var text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, "(?<![\\w:-])[Tt]itle=[\"'@]"))
            {
                // The tag the attribute sits on: the nearest "<name" before it (a component's Title="..." parameter is a capitalised
                // component tag and is not an HTML title attribute, so only lowercase attributes on lowercase elements and on the
                // native-button wrappers count).
                var tag = Regex.Matches(text[..match.Index], "<([A-Za-z][\\w.:-]*)").Cast<Match>().LastOrDefault()?.Groups[1].Value ?? string.Empty;
                var isHtmlElement = tag.Length > 0 && char.IsLower(tag[0]);
                var isLowerAttribute = text[match.Index] == 't';
                if (!isLowerAttribute && !tag.Equals("AppNativeButton", StringComparison.Ordinal))
                {
                    continue;
                }

                if (isLowerAttribute && isHtmlElement && TitleAllowlist.Contains(tag))
                {
                    continue;
                }

                var line = text[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(RepoRoot(), path)}:{line} <{tag}>");
            }
        }

        Assert.True(offenders.Count == 0,
            "title=\"...\" is only for img, iframe, abbr and time (D8). Use the Tooltip parameter for an icon-only control, AppTooltip for a disabled reason, data-overflow-tooltip for cut-off text, or delete a hint that repeats the visible label:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NoTooltipRepeatsItsControlsVisibleText()
    {
        var offenders = new List<string>();
        foreach (var path in RazorFiles())
        {
            var text = File.ReadAllText(path);
            foreach (Match open in Regex.Matches(text, "<(App\\w+|Playback\\w+|Media\\w+)\\b"))
            {
                var end = EndOfStartTag(text, open.Index + open.Length);
                if (end < 0)
                {
                    continue;
                }

                var tag = text[open.Index..(end + 1)];
                var tooltip = AttributeValue(tag, "Tooltip");
                if (tooltip is null)
                {
                    continue;
                }

                var visible = new List<string?> { AttributeValue(tag, "Text"), AttributeValue(tag, "Label") };
                if (!tag.TrimEnd('>').EndsWith('/'))
                {
                    var close = text.IndexOf($"</{open.Groups[1].Value}>", end, StringComparison.Ordinal);
                    if (close > end)
                    {
                        var inner = Regex.Replace(text[(end + 1)..close], "<span class=\"sr-only\">.*?</span>", string.Empty, RegexOptions.Singleline);
                        inner = Regex.Replace(inner, "<[^>]+>", " ");
                        visible.Add(inner);
                    }
                }

                var line = text[..open.Index].Count(c => c == '\n') + 1;
                if (visible.Any(v => v is not null && Normalise(v).Length > 0 && Normalise(v) == Normalise(tooltip)))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), path)}:{line} <{open.Groups[1].Value}> Tooltip={tooltip}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "A control with a visible label that says the same needs no tooltip (D8):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void TheNativeButtonHasNoTitleParameter()
    {
        var web = Path.Combine(RepoRoot(), "src", "MediaEngine.Web");
        var button = File.ReadAllText(Path.Combine(web, "Components", "Shared", "AppNativeButton.razor"));
        Assert.DoesNotContain("string? Title", button);
        Assert.DoesNotContain("title=", button);
        Assert.False(File.Exists(Path.Combine(web, "wwwroot", "js", "app-title-tooltips.js")), "The global title script is retired (D8).");
        Assert.DoesNotContain("app-title-tooltips", File.ReadAllText(Path.Combine(web, "Components", "App.razor")));
    }

    [Fact]
    public void NoControlOnThePreviewPageHasATooltipThatEqualsItsOwnText()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();

        var cut = Render<DesignSystemPreview>();

        var offenders = cut.FindAll("[data-playback-tooltip]")
            .Where(e => e.GetAttribute("data-playback-tooltip-suppressed") != "true")
            .Where(e => !string.IsNullOrWhiteSpace(e.GetAttribute("data-playback-tooltip")))
            .Where(e => Normalise(e.TextContent) == Normalise(e.GetAttribute("data-playback-tooltip")!))
            .Select(e => e.GetAttribute("data-playback-tooltip"))
            .ToList();
        Assert.True(offenders.Count == 0, "Tooltips equal to their own control's text: " + string.Join(" | ", offenders));
        Assert.DoesNotContain(cut.FindAll("[title]"), e => !TitleAllowlist.Contains(e.LocalName));
    }

    [Fact]
    public void TheTooltipUsesTheSharedTooltipTokens()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var tokens = File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-tooltip-surface:", tokens);
        Assert.Contains("--tl-tooltip-radius: 6px;", tokens);
        Assert.Contains("--tl-tooltip-font-size: 13px;", tokens);
        Assert.Contains("--tl-tooltip-max-width: 280px;", tokens);
        var script = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "playback-tooltip.js"));
        Assert.Contains("HOVER_DELAY_MS = 150", script);
        Assert.Contains("TOUCH_PRESS_MS = 500", script);
    }

    private static IEnumerable<string> RazorFiles()
    {
        var web = Path.Combine(RepoRoot(), "src", "MediaEngine.Web");
        return Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    // The ">" that closes a start tag, skipping quoted attribute values.
    private static int EndOfStartTag(string text, int from)
    {
        var quoted = false;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && text[i] == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static string? AttributeValue(string tag, string name)
    {
        var match = Regex.Match(tag, $"\\s{name}=\"(.*?)\"(?=\\s|/?>)", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    // "@L[\"Key\"].Value", "@L[\"Key\"]" and "Key" compare equal; so do equal literals ignoring case and spacing.
    private static string Normalise(string value) =>
        Regex.Replace(value.Replace(".Value", string.Empty).Replace("@", string.Empty), "\\s+", " ").Trim().ToLowerInvariant();

    private static string RepoRoot() => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
