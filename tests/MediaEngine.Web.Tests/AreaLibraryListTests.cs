using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The libraries under one area that is on, on a person's page: an All libraries tick and a tick per library, all ticked unless hidden.</summary>
public sealed class AreaLibraryListTests : AsyncBunitContext
{
    private static readonly Guid Books = Guid.NewGuid();
    private static readonly Guid Comics = Guid.NewGuid();
    private static readonly Guid Movies = Guid.NewGuid();
    private static readonly Guid Lectures = Guid.NewGuid();

    private static readonly AccessLibraryOptionDto[] Picker =
    [
        new(Books, "Books", null, "read"),
        new(Comics, "Comics", null, "read"),
        new(Movies, "Movies", null, "watch"),
        new(Lectures, "Lectures", null, "Watch"),
    ];

    private readonly List<(Guid Id, bool Hidden)> _ticks = [];
    private readonly List<(string Area, bool Ticked)> _alls = [];

    public AreaLibraryListTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void EverythingIsTickedByDefault_WithAnAllLibrariesTickAtTheTop_AndAnAreaWithOneLibraryShowsNoList()
    {
        var read = RenderList("read");
        var listen = RenderList("listen");

        Assert.Equal(["All libraries", "Books", "Comics"], read.FindAll("label").Select(label => label.TextContent.Trim()));
        Assert.All(read.FindAll("input"), input => Assert.True(input.HasAttribute("checked")));
        // Listen has no library at all: nothing is drawn.
        Assert.Empty(listen.Markup.Trim());
    }

    [Fact]
    public void AnAreaWithExactlyOneLibrary_ShowsNoList()
    {
        var cut = Render<AreaLibraryList>(parameters => parameters
            .Add(list => list.Area, "watch")
            .Add(list => list.Libraries, Picker.Take(3).ToList())
            .Add(list => list.HiddenIds, new HashSet<Guid>()));

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void AreaNamesMatchWhateverCaseTheEngineUses_AndLibrariesSitInNameOrder()
    {
        var watch = RenderList("watch");

        Assert.Equal(["All libraries", "Lectures", "Movies"], watch.FindAll("label").Select(label => label.TextContent.Trim()));
    }

    [Fact]
    public void AHiddenLibraryShowsUnticked_AndUntickingOrReTickingReportsTheChange_WithoutSavingAnything()
    {
        var cut = RenderList("read", hidden: [Comics]);
        Assert.True(Tick(cut, Books).HasAttribute("checked"));
        Assert.False(Tick(cut, Comics).HasAttribute("checked"));
        // One library is hidden, so All libraries is not ticked.
        Assert.False(AllTick(cut).HasAttribute("checked"));

        Tick(cut, Books).Change(false);
        Tick(cut, Comics).Change(true);

        Assert.Equal([(Books, true), (Comics, false)], _ticks);
    }

    [Fact]
    public void AllLibraries_ReportsTheWholeArea()
    {
        var cut = RenderList("read", hidden: [Comics]);

        AllTick(cut).Change(true);

        Assert.Equal([("read", true)], _alls);
    }

    [Fact]
    public void ALibraryTheHouseholdDoesNotHave_IsGreyedWithItsReason_AndCannotBeTicked()
    {
        var cut = RenderList("read", limited: [Comics], householdName: "Smith household");

        var row = cut.Find($"[data-library-id='{Comics}']");
        Assert.Contains("Not available to Smith household", row.TextContent);
        Assert.Contains("area-library-list__item--limited", row.ClassName);
        Assert.True(Tick(cut, Comics).HasAttribute("disabled"));
        Assert.False(Tick(cut, Comics).HasAttribute("checked"));

        Tick(cut, Comics).Change(true);
        Assert.Empty(_ticks);
        Assert.False(Tick(cut, Books).HasAttribute("disabled"));
        // The ones it can change are all ticked, so All libraries is ticked too.
        Assert.True(AllTick(cut).HasAttribute("checked"));
    }

    [Fact]
    public void ALibraryMarkedHiddenForTheHousehold_ByTheEngine_IsGreyedToo()
    {
        var picker = Picker.Select(library => library.Id == Comics ? library with { HiddenForHousehold = true } : library).ToList();

        var cut = Render<AreaLibraryList>(parameters => parameters
            .Add(list => list.Area, "read")
            .Add(list => list.Libraries, picker)
            .Add(list => list.HiddenIds, new HashSet<Guid>())
            .Add(list => list.HouseholdName, "Lee household"));

        Assert.Contains("Not available to Lee household", cut.Find($"[data-library-id='{Comics}']").TextContent);
        Assert.True(Tick(cut, Comics).HasAttribute("disabled"));
    }

    [Fact]
    public void ALibraryHiddenFromTheAdministrator_AndFromThePerson_IsGreyed_AndCannotBeShown()
    {
        var cut = RenderList("read", hidden: [Books], hiddenFromYou: [Books]);

        var row = cut.Find($"[data-library-id='{Books}']");
        Assert.Contains("Hidden from you", row.TextContent);
        Assert.Contains("area-library-list__item--held", row.ClassName);
        Assert.True(Tick(cut, Books).HasAttribute("disabled"));
        Assert.False(Tick(cut, Books).HasAttribute("checked"));

        Tick(cut, Books).Change(true);
        Assert.Empty(_ticks);
        Assert.False(Tick(cut, Comics).HasAttribute("disabled"));
    }

    [Fact]
    public void ALibraryHiddenFromTheAdministrator_ButStillVisibleToThePerson_CanBeHiddenFromThePerson()
    {
        var cut = RenderList("read", hidden: [], hiddenFromYou: [Books]);

        Assert.DoesNotContain("Hidden from you", cut.Find($"[data-library-id='{Books}']").TextContent);
        Assert.False(Tick(cut, Books).HasAttribute("disabled"));
        Assert.True(Tick(cut, Books).HasAttribute("checked"));

        Tick(cut, Books).Change(false);
        Assert.Equal([(Books, true)], _ticks);
    }

    [Fact]
    public void WhileLocked_TheTicksAreShownButDisabled()
    {
        var cut = RenderList("read", hidden: [Comics], locked: true);

        Assert.All(cut.FindAll("input"), input => Assert.True(input.HasAttribute("disabled")));
        Assert.False(Tick(cut, Comics).HasAttribute("checked"));
        Tick(cut, Books).Change(false);
        AllTick(cut).Change(false);
        Assert.Empty(_ticks);
        Assert.Empty(_alls);
    }

    [Fact]
    public void ALibraryInNoneOfTheFourAreas_IsNeverDrawn_SoNoVaultLibraryCanAppear()
    {
        var picker = Picker.Concat([new AccessLibraryOptionDto(Guid.NewGuid(), "Secret one", "Videos", "vault"), new AccessLibraryOptionDto(Guid.NewGuid(), "Secret two", "Videos", "vault")]).ToList();

        foreach (var area in new[] { "read", "watch", "listen", "view" })
        {
            var cut = Render<AreaLibraryList>(parameters => parameters
                .Add(list => list.Area, area)
                .Add(list => list.Libraries, picker)
                .Add(list => list.HiddenIds, new HashSet<Guid>()));
            Assert.DoesNotContain("Secret", cut.Markup);
        }
    }

    private static AngleSharp.Dom.IElement Tick(IRenderedComponent<AreaLibraryList> cut, Guid id) =>
        cut.Find($"[data-library-id='{id}'] input");

    private static AngleSharp.Dom.IElement AllTick(IRenderedComponent<AreaLibraryList> cut) =>
        cut.Find("[data-area-all] input");

    private IRenderedComponent<AreaLibraryList> RenderList(string area, Guid[]? hidden = null, Guid[]? hiddenFromYou = null,
        Guid[]? limited = null, string householdName = "", bool locked = false)
    {
        var picker = Picker.Select(library => hiddenFromYou?.Contains(library.Id) == true ? library with { HiddenFromYou = true } : library).ToList();
        return Render<AreaLibraryList>(parameters => parameters
            .Add(list => list.Area, area)
            .Add(list => list.Libraries, picker)
            .Add(list => list.HiddenIds, (hidden ?? []).ToHashSet())
            .Add(list => list.LimitedIds, (limited ?? []).ToHashSet())
            .Add(list => list.HouseholdName, householdName)
            .Add(list => list.Locked, locked)
            .Add(list => list.OnTick, EventCallback.Factory.Create<(Guid LibraryId, bool Hidden)>(this, tick => _ticks.Add((tick.LibraryId, tick.Hidden))))
            .Add(list => list.OnAll, EventCallback.Factory.Create<(string Area, bool Ticked)>(this, all => _alls.Add((all.Area, all.Ticked)))));
    }
}
