using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A filter row never mixes controls that draw a label above themselves with controls that do not: the labelled ones sit lower
/// than their neighbours. Inside the shared <c>AppFilterRow</c> (and <c>AppSearchFilterRow</c>) every select and text box therefore
/// carries <c>HideLabel="true"</c>; its closed value says what it is and its label stays for screen readers.
/// </summary>
public sealed class FilterRowGuardrailTests
{
    private static readonly string ComponentsRoot = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        "src", "MediaEngine.Web", "Components");

    private static readonly Regex Row = new(
        @"<(?<tag>AppFilterRow|AppSearchFilterRow)\b.*?</\k<tag>>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Control = new(
        @"<(?<name>AppSelect|AppTextField|AppSearchField|AppDatePicker)\b(?<attrs>[^>]*?)/?>", RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void EveryControlInsideAFilterRow_HidesItsVisibleLabel()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ComponentsRoot, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match row in Row.Matches(text))
            {
                foreach (Match control in Control.Matches(row.Value))
                {
                    var attrs = control.Groups["attrs"].Value;
                    if (!attrs.Contains("HideLabel=\"true\"", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetRelativePath(ComponentsRoot, file)}: <{control.Groups["name"].Value} ...>");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Controls in a filter row must set HideLabel=\"true\" so the row lines up:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void TheSharedFilterRowIsUsedByTheListPagesThatFilter()
    {
        foreach (var page in new[] { "Settings/NeedsReviewPageContent.razor", "Pages/RecentlyAddedPageContent.razor", "Settings/ActivityLogPanel.razor", "Settings/TechnicalLogPanel.razor" })
        {
            Assert.Contains("<AppFilterRow", File.ReadAllText(Path.Combine(ComponentsRoot, page)), StringComparison.Ordinal);
        }
    }
}
