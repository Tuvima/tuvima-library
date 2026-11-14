using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Settings.LibraryActivity;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The slimmer batch rows of the Activity timeline (spec library-activity D33, D35, WP12): a running batch reads "Adding audiobooks" with
/// "2 added so far", two covers and Active; a finished one reads "250 items added" with one counts-by-kind line, three covers with "+247" and
/// Completed. No library name sits on the row, and the phone gets two covers with its own "+N".
/// </summary>
public sealed class LibraryActivitySlimBatchRowTests : AsyncBunitContext
{
    private const string LibraryId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    public LibraryActivitySlimBatchRowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(EngineApiClientStub.Create(_ => { }));
    }

    [Fact]
    public void ARunningBatchOfOneKind_ReadsAddingThatKind_WithAddedSoFar_TwoCovers_NowAndActive()
    {
        var cut = RenderRow(Run(ActivityRunStatusNames.Active, 2, [new ActivityRunCountDto("Audiobooks", 2)], Covers(3)));

        Assert.Equal("Adding audiobooks", cut.Find("[data-testid='lact-entry-title']").TextContent.Trim());
        Assert.Equal("2 added so far", cut.Find("[data-testid='lact-entry-line']").TextContent.Trim());
        Assert.Equal("Now", cut.Find("[data-testid='lact-entry-date']").TextContent.Trim());
        Assert.Equal("Active", cut.Find("[data-testid='lact-entry-status']").TextContent.Trim());
        Assert.Equal(2, cut.FindAll("[data-testid='lact-entry-covers'] .lact-cover").Count);
        Assert.Empty(cut.FindAll(".lact-cover-more"));
        Assert.Equal("active", cut.Find("[data-testid='lact-entry']").GetAttribute("data-run-status"));
        Assert.Contains("is-active", cut.Find("[data-testid='lact-entry']").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningBatchOfSeveralKinds_ReadsAddingItems()
    {
        var cut = RenderRow(Run(ActivityRunStatusNames.Active, 5, [new ActivityRunCountDto("Movies", 3), new ActivityRunCountDto("Books", 2)], Covers(2)));

        Assert.Equal("Adding items", cut.Find("[data-testid='lact-entry-title']").TextContent.Trim());
        Assert.Equal("5 added so far", cut.Find("[data-testid='lact-entry-line']").TextContent.Trim());
    }

    [Fact]
    public void ARunningBatchWithNoKindsYet_ReadsAddingItems_AndNoCoversAreDrawn()
    {
        var cut = RenderRow(Run(ActivityRunStatusNames.Active, 0, [], []));

        Assert.Equal("Adding items", cut.Find("[data-testid='lact-entry-title']").TextContent.Trim());
        Assert.Equal("0 added so far", cut.Find("[data-testid='lact-entry-line']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='lact-entry-covers'] .lact-cover"));
    }

    [Fact]
    public void AFinishedBatch_ReadsItemsAdded_WithTheCountsLine_ThreeCoversPlusTheRest_AndCompleted()
    {
        var counts = new[]
        {
            new ActivityRunCountDto("Movies", 6), new ActivityRunCountDto("Videos", 91), new ActivityRunCountDto("Music", 82),
            new ActivityRunCountDto("TV", 37), new ActivityRunCountDto("Books", 25), new ActivityRunCountDto("Audiobooks", 9),
        };
        var cut = RenderRow(Run(ActivityRunStatusNames.Completed, 250, counts, Covers(5)));

        Assert.Equal("250 items added", cut.Find("[data-testid='lact-entry-title']").TextContent.Trim());
        Assert.Equal("91 videos · 82 tracks · 37 episodes · 25 books · 9 audiobooks · 6 movies", cut.Find("[data-testid='lact-entry-line']").TextContent.Trim());
        Assert.Equal(3, cut.FindAll("[data-testid='lact-entry-covers'] .lact-cover").Count);
        Assert.Equal("+247", cut.Find("[data-testid='lact-entry-more']").TextContent.Trim());
        Assert.Equal("Completed", cut.Find("[data-testid='lact-entry-status']").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("[data-testid='lact-entry-status'] svg"));
        Assert.DoesNotContain("is-active", cut.Find("[data-testid='lact-entry']").ClassName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ActivityRunKindNames.Added, 250, "250 items added")]
    [InlineData(ActivityRunKindNames.Updated, 12, "12 items updated")]
    [InlineData(ActivityRunKindNames.Fixed, 3, "3 items fixed")]
    [InlineData(ActivityRunKindNames.Removed, 3, "3 items removed")]
    [InlineData(ActivityRunKindNames.Added, 1, "1 item added")]
    [InlineData(ActivityRunKindNames.Removed, 1, "1 item removed")]
    public void AFinishedBatchOfEachType_ReadsItsCountAndWhatHappenedToIt(string kind, int count, string expected)
    {
        var cut = RenderRow(Run(ActivityRunStatusNames.Completed, count, [new ActivityRunCountDto("Movies", count)], Covers(1), kind));

        Assert.Equal(expected, cut.Find("[data-testid='lact-entry-title']").TextContent.Trim());
    }

    [Theory]
    [InlineData(ActivityRunKindNames.Fixed, "Resolved from Needs Review")]
    [InlineData(ActivityRunKindNames.Removed, "Manually removed from library")]
    public void AFinishedFixOrRemovalWithNoKindsRecorded_SaysWhyOnItsOneLine(string kind, string expected)
    {
        var cut = RenderRow(Run(ActivityRunStatusNames.Completed, 3, [], Covers(3), kind));

        Assert.Equal(expected, cut.Find("[data-testid='lact-entry-line']").TextContent.Trim());
    }

    [Fact]
    public void AFinishedBatchWithNoKindsAndNoReason_DrawsNoEmptyLine()
    {
        var cut = RenderRow(Run(ActivityRunStatusNames.Completed, 3, [], Covers(3)));

        Assert.Empty(cut.FindAll("[data-testid='lact-entry-line']"));
    }

    [Fact]
    public void TheRow_NeverNamesTheLibrary()
    {
        var cut = RenderRow(Run(ActivityRunStatusNames.Completed, 4, [new ActivityRunCountDto("Movies", 4)], Covers(3)));

        Assert.DoesNotContain("My Special Library", cut.Find("[data-testid='lact-entry']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ADatedBatch_ShowsItsDateOnTheLine_AndAFailedOneItsOwnStatus()
    {
        var started = new DateTimeOffset(2026, 11, 11, 12, 0, 0, DateTimeOffset.Now.Offset);
        var cut = RenderRow(Run(ActivityRunStatusNames.Failed, 4, [new ActivityRunCountDto("Movies", 4)], Covers(1), started: started));

        Assert.Equal("Nov 11", cut.Find("[data-testid='lact-entry-date']").TextContent.Trim());
        Assert.Equal("Failed", cut.Find("[data-testid='lact-entry-status']").TextContent.Trim());
    }

    [Fact]
    public void OnAPhone_TheThirdCoverAndTheDesktopPlusAreHidden_AndThePhoneHasItsOwnPlus()
    {
        var cut = RenderRow(Run(ActivityRunStatusNames.Completed, 250, [new ActivityRunCountDto("Movies", 250)], Covers(5)));

        var covers = cut.FindAll("[data-testid='lact-entry-covers'] .lact-cover");
        Assert.DoesNotContain("is-desktop-only", covers[0].ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("is-desktop-only", covers[1].ClassName, StringComparison.Ordinal);
        Assert.Contains("is-desktop-only", covers[2].ClassName, StringComparison.Ordinal);

        var desktop = cut.Find("[data-testid='lact-entry-more']");
        var phone = cut.Find("[data-testid='lact-entry-more-phone']");
        Assert.Equal("+247", desktop.TextContent.Trim());
        Assert.Contains("is-desktop-only", desktop.ClassName, StringComparison.Ordinal);
        Assert.Equal("+248", phone.TextContent.Trim());
        Assert.Contains("is-phone-only", phone.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void WithOnlyTwoCovers_ThePlusIsTheSameOnEveryWidth_SoOneChipIsDrawn()
    {
        var cut = RenderRow(Run(ActivityRunStatusNames.Completed, 40, [new ActivityRunCountDto("Movies", 40)], Covers(2)));

        Assert.Equal("+38", cut.Find("[data-testid='lact-entry-more']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='lact-entry-more-phone']"));
        Assert.Empty(cut.FindAll(".lact-cover.is-desktop-only"));
    }

    [Fact]
    public void OpeningARow_StillLoadsItsTilesInPlace_AndTheRowDrawsOnlyTheOneStatus()
    {
        var cut = RenderRow(Run(ActivityRunStatusNames.Completed, 4, [new ActivityRunCountDto("Movies", 4)], Covers(3)));

        Assert.Single(cut.FindAll("[data-testid='lact-entry-status']"));
        cut.Find("[data-testid='lact-entry-toggle']").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='lact-open']")));
        Assert.Equal("true", cut.Find("[data-testid='lact-entry-toggle']").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void EveryWordOnTheRow_IsASharedStringsKey_AndTheRowHoldsNoSentenceOfItsOwn()
    {
        var root = FindRepoRoot();
        var razor = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Settings/LibraryActivity/LibraryActivityRunEntry.razor"));
        var english = XDocument.Load(Path.Combine(root, "src/MediaEngine.Web/Resources/SharedStrings.resx"))
            .Descendants("data").ToDictionary(data => (string)data.Attribute("name")!, data => (string)data.Element("value")!, StringComparer.Ordinal);

        var used = Regex.Matches(razor, "\"(LibraryActivity_[A-Za-z]+)\"").Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(used);
        Assert.All(used, key => Assert.True(english.ContainsKey(key), $"{key} is not in SharedStrings.resx."));

        var code = Regex.Replace(Regex.Replace(Regex.Replace(razor, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty), "\"LibraryActivity_[A-Za-z]+\"", string.Empty);
        foreach (var phrase in new[] { "so far", "Adding ", "Completed", "items added", "items updated", "items fixed", "items removed" })
        {
            Assert.DoesNotContain(phrase, code, StringComparison.Ordinal);
        }

        // The translations may lag behind the English master, but never hold a key it does not have.
        foreach (var culture in new[] { "fr", "de", "es" })
        {
            var localized = XDocument.Load(Path.Combine(root, $"src/MediaEngine.Web/Resources/SharedStrings.{culture}.resx"))
                .Descendants("data").Select(data => (string)data.Attribute("name")!);
            Assert.All(localized, key => Assert.True(english.ContainsKey(key), $"{culture} holds {key}, which the English master lacks."));
        }
    }

    [Fact]
    public void TheRowsSentencesTakeTheirNumbers_AsPlaceholders_SoATranslationCanPlaceThemAnywhere()
    {
        var english = XDocument.Load(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Resources/SharedStrings.resx"))
            .Descendants("data").ToDictionary(data => (string)data.Attribute("name")!, data => (string)data.Element("value")!, StringComparer.Ordinal);

        foreach (var key in new[]
        {
            "LibraryActivity_AddingKind", "LibraryActivity_AddedSoFar", "LibraryActivity_BatchItemAdded", "LibraryActivity_BatchItemsAdded",
            "LibraryActivity_BatchItemUpdated", "LibraryActivity_BatchItemsUpdated", "LibraryActivity_BatchItemFixed", "LibraryActivity_BatchItemsFixed",
            "LibraryActivity_BatchItemRemoved", "LibraryActivity_BatchItemsRemoved",
        })
        {
            Assert.Contains("{0}", english[key], StringComparison.Ordinal);
        }
    }

    private IRenderedComponent<LibraryActivityRunEntry> RenderRow(ActivityRunDto run)
        => Render<LibraryActivityRunEntry>(parameters => parameters.Add(entry => entry.Run, run));

    private static IReadOnlyList<ActivityCoverDto> Covers(int count)
        => Enumerable.Range(0, count).Select(index => new ActivityCoverDto($"/engine-image/stream/entity/work/{Guid.NewGuid():D}/cover?size=t", "Movies", $"Title {index}")).ToList();

    private static ActivityRunDto Run(
        string status,
        int itemCount,
        IReadOnlyList<ActivityRunCountDto> counts,
        IReadOnlyList<ActivityCoverDto> covers,
        string kind = ActivityRunKindNames.Added,
        DateTimeOffset? started = null)
        => new(
            Guid.NewGuid(), LibraryId, "My Special Library", kind, status,
            started ?? DateTimeOffset.Now.AddHours(-3), status == ActivityRunStatusNames.Active ? null : DateTimeOffset.Now.AddHours(-2),
            itemCount, counts, covers);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
