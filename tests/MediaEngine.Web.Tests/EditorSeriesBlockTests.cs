using Bunit;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.MediaEditor.Sections;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The Series block in the editor's Matching section.</summary>
public sealed class EditorSeriesBlockTests : AsyncBunitContext
{
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly Guid Current = Guid.NewGuid();
    private static readonly Guid Listed = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private SeriesPlacementDto _placement = Placement(isPersonChoice: false);
    private readonly List<(Guid? Collection, bool Locked)> _saved = [];
    private string _formFactor = "desktop";

    public EditorSeriesBlockTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IEngineApiClient>(_ => EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSeriesPlacementAsync), _ => Task.FromResult<SeriesPlacementDto?>(_placement));
            stub.SetHandler(nameof(IEngineApiClient.SaveSeriesPlacementAsync), args =>
            {
                _saved.Add(((Guid?)args![1], (bool)args[2]!));
                _placement = Placement(isPersonChoice: (bool)args[2]!, current: (bool)args[2]! && args[1] is Guid chosen && chosen == Other ? Shelf(Other, "Spice Wars", "MainSequence", null) : null);
                return Task.FromResult<SeriesPlacementDto?>(_placement);
            });
        }));
        Services.AddScoped(_ =>
        {
            var phone = _formFactor == "phone";
            var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
            device.InitialiseAsync(new DeviceContextSnapshot(phone ? "mobile" : "web", _formFactor, phone, !phone, !phone)).GetAwaiter().GetResult();
            return device;
        });
    }

    [Fact]
    public void TheBlock_ShowsTheCurrentSeries_WhatListsIt_AndOtherSeries_WithPlainScopeWords()
    {
        var cut = Render<EditorSeriesBlock>(p => p.Add(c => c.EntityId, Item));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".sme-series")));
        var text = cut.Find(".sme-series").TextContent;
        Assert.Contains("Dune Novels", text);
        Assert.Contains("In the main run (#3)", text);
        Assert.Contains("Dune Reading Order", text);
        Assert.Contains("Also in this world", text);
        Assert.Contains("Spice Wars", text);
        Assert.Contains("Not in a Series", text);
        Assert.DoesNotContain("Chosen by you", text);
        Assert.DoesNotContain("Let the Library decide", text);
    }

    [Fact]
    public void ChoosingASeries_ReviewsIt_ThenSavesItAsALockedChoice_AndTellsTheEditor()
    {
        var applied = 0;
        var cut = Render<EditorSeriesBlock>(p => p
            .Add(c => c.EntityId, Item)
            .Add(c => c.Applied, () => { applied++; }));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".sme-series")));

        cut.FindAll(".app-choice-row").Single(row => row.TextContent.Contains("Spice Wars")).Click();
        Assert.Contains("This item will go in Spice Wars", cut.Find(".sme-series__review").TextContent);
        Assert.Empty(_saved);

        cut.FindAll(".sme-series__review button").Single(b => b.TextContent.Contains("Save Series")).Click();

        cut.WaitForAssertion(() => Assert.Single(_saved));
        Assert.Equal((Other, true), _saved[0]);
        Assert.Equal(1, applied);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Chosen by you", cut.Find(".sme-series").TextContent);
            Assert.Contains("Let the Library decide", cut.Find(".sme-series").TextContent);
        });
    }

    [Fact]
    public void NotInASeries_IsSavedAsALockedChoiceWithNoShelf()
    {
        var cut = Render<EditorSeriesBlock>(p => p.Add(c => c.EntityId, Item));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".sme-series")));

        cut.FindAll(".app-choice-row").Single(row => row.TextContent.Contains("Not in a Series")).Click();
        cut.FindAll(".sme-series__review button").Single(b => b.TextContent.Contains("Save Series")).Click();

        cut.WaitForAssertion(() => Assert.Single(_saved));
        Assert.Equal((null, true), _saved[0]);
    }

    [Fact]
    public void LetTheLibraryDecide_ReleasesTheChoice()
    {
        _placement = Placement(isPersonChoice: true);
        var cut = Render<EditorSeriesBlock>(p => p.Add(c => c.EntityId, Item));
        cut.WaitForAssertion(() => Assert.Contains("Chosen by you", cut.Find(".sme-series").TextContent));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Let the Library decide")).Click();

        cut.WaitForAssertion(() => Assert.Single(_saved));
        Assert.Equal((null, false), _saved[0]);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Let the Library decide", cut.Markup));
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    public void OnAPhoneOrTablet_TheBlockIsNotShown(string formFactor)
    {
        _formFactor = formFactor;

        var cut = Render<EditorSeriesBlock>(p => p.Add(c => c.EntityId, Item));

        Assert.Empty(cut.FindAll(".sme-series"));
    }

    [Fact]
    public void ForAnItemThatIsNotPlacedOnSeriesShelves_TheBlockIsNotShown()
    {
        _placement = new SeriesPlacementDto { WorkId = Item, MediaType = "TV", SupportsSeries = false };

        var cut = Render<EditorSeriesBlock>(p => p.Add(c => c.EntityId, Item));

        Assert.Empty(cut.FindAll(".sme-series"));
    }

    [Theory]
    [InlineData("MainSequence", "3", "In the main run (#3)")]
    [InlineData("MainSequence", null, "In the main run")]
    [InlineData("Supplementary", "2.5", "Also in this world")]
    [InlineData("CollectedContent", null, "Also in this world")]
    public void ScopeIsSaidInPlainWords(string scope, string? position, string expected)
    {
        Assert.Equal(expected, EditorSeriesBlock.ScopeText(Shelf(Guid.NewGuid(), "X", scope, position)));
    }

    [Fact]
    public void EachRow_IsABuiltInChoiceButton_ThatCarriesItsOwnScopedStyleAttribute()
    {
        var cut = Render<EditorSeriesBlock>(p => p.Add(c => c.EntityId, Item));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".sme-series")));

        // Blazor scopes a component's CSS with a b-xxxx attribute on the elements that component renders itself. The rows are
        // AppChoiceRow buttons (the button is the primitive's own markup), so the primitive's styles reach them with no deep
        // rule from the page; a row drawn by a button inside another component would not carry the attribute.
        var rows = cut.FindAll(".app-choice-row");

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("button", row.TagName.ToLowerInvariant());
            Assert.Contains(row.Attributes, a => a.Name.StartsWith("b-", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ASelectedRow_ShowsTheSelectedClassAndPressedText()
    {
        var cut = Render<EditorSeriesBlock>(p => p.Add(c => c.EntityId, Item));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".sme-series")));
        Assert.All(cut.FindAll(".app-choice-row"), row => Assert.Equal("false", row.GetAttribute("aria-pressed")));

        cut.FindAll(".app-choice-row").Single(row => row.TextContent.Contains("Spice Wars")).Click();

        var selected = cut.FindAll(".app-choice-row").Single(row => row.TextContent.Contains("Spice Wars"));
        Assert.Contains("is-selected", selected.ClassName);
        Assert.Equal("true", selected.GetAttribute("aria-pressed"));
        Assert.All(
            cut.FindAll(".app-choice-row").Where(row => !ReferenceEquals(row, selected) && !row.TextContent.Contains("Spice Wars")),
            row => Assert.Equal("false", row.GetAttribute("aria-pressed")));
    }

    [Fact]
    public void WhenTheSeriesCannotBeLoaded_TheBlockShowsAnErrorWithRetry_ThenLoadsOnRetry()
    {
        SeriesPlacementDto? answer = null;
        Services.AddSingleton<IEngineApiClient>(_ => EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetSeriesPlacementAsync), _ => Task.FromResult<SeriesPlacementDto?>(answer))));

        var cut = Render<EditorSeriesBlock>(p => p.Add(c => c.EntityId, Item));
        cut.WaitForAssertion(() => Assert.Contains("Series could not be loaded", cut.Markup));
        Assert.Empty(cut.FindAll(".sme-series"));

        answer = Placement(isPersonChoice: false);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Retry", StringComparison.OrdinalIgnoreCase)).Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".sme-series")));
    }

    [Fact]
    public void TheSearchWaitsForAPause_AndNeedsAtLeastTwoLetters()
    {
        Assert.Equal(300, EditorSeriesBlock.SearchDebounceMilliseconds);
        Assert.Equal(2, EditorSeriesBlock.MinSearchLength);
    }

    private static SeriesPlacementShelfDto Shelf(Guid id, string label, string scope, string? position) => new()
    {
        CollectionId = id,
        Label = label,
        SeriesQid = "Q1",
        MediaType = "Books",
        Scope = scope,
        PositionLabel = position,
    };

    private static SeriesPlacementDto Placement(bool isPersonChoice, SeriesPlacementShelfDto? current = null) => new()
    {
        WorkId = Item,
        MediaType = "Books",
        SupportsSeries = true,
        IsPersonChoice = isPersonChoice,
        Current = current ?? Shelf(Current, "Dune Novels", "MainSequence", "3"),
        Listed = [Shelf(Listed, "Dune Reading Order", "Supplementary", null)],
        Candidates = [Shelf(Other, "Spice Wars", "MainSequence", null)],
    };
}
