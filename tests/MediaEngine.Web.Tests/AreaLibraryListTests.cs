using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The "Libraries" list under one area on a person's page: a tick per library, all ticked unless hidden from the person.</summary>
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

    public AreaLibraryListTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void EverythingIsTickedByDefault_AndAnAreaWithOneLibraryShowsNoList()
    {
        var read = RenderList("read");
        var listen = RenderList("listen");

        Assert.Equal(["Books", "Comics"], read.FindAll("label").Select(label => label.TextContent.Trim()));
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
    public void AreaNamesMatchWhateverCaseTheEngineUses()
    {
        var watch = RenderList("watch");

        Assert.Equal(["Movies", "Lectures"], watch.FindAll("label").Select(label => label.TextContent.Trim()));
    }

    [Fact]
    public void AHiddenLibraryShowsUnticked_AndUntickingOrReTickingReportsTheChange()
    {
        var cut = RenderList("read", hidden: [Comics]);
        Assert.True(Tick(cut, Books).HasAttribute("checked"));
        Assert.False(Tick(cut, Comics).HasAttribute("checked"));

        Tick(cut, Books).Change(false);
        Tick(cut, Comics).Change(true);

        Assert.Equal([(Books, true), (Comics, false)], _ticks);
    }

    [Fact]
    public void ALibraryTheMainSignInHides_IsGreyedWithItsName_AndCannotBeTicked()
    {
        var cut = RenderList("read", hidden: [], primaryHidden: [Books], primaryName: "Alex");

        var row = cut.Find($"[data-library-id='{Books}']");
        Assert.Contains("Hidden by Alex", row.TextContent);
        Assert.Contains("area-library-list__item--held", row.ClassName);
        Assert.True(Tick(cut, Books).HasAttribute("disabled"));
        Assert.False(Tick(cut, Books).HasAttribute("checked"));

        Tick(cut, Books).Change(true);
        Assert.Empty(_ticks);
        Assert.False(Tick(cut, Comics).HasAttribute("disabled"));
    }

    [Fact]
    public void APersonWhoFollowsThePrimary_SeesTheListReadOnly_WithWhoTheyFollow()
    {
        var cut = RenderList("read", hidden: [Comics], primaryName: "Alex", follows: true);

        Assert.Contains("Follows Alex's access.", cut.Markup);
        Assert.All(cut.FindAll("input"), input => Assert.True(input.HasAttribute("disabled")));
        Assert.True(Tick(cut, Books).HasAttribute("checked"));
        Assert.False(Tick(cut, Comics).HasAttribute("checked"));
        Tick(cut, Comics).Change(true);
        Assert.Empty(_ticks);
    }

    [Fact]
    public void OnAPhone_TheTicksAreShownButDisabled()
    {
        var cut = RenderList("read", hidden: [Comics], readOnly: true);

        Assert.All(cut.FindAll("input"), input => Assert.True(input.HasAttribute("disabled")));
        Assert.False(Tick(cut, Comics).HasAttribute("checked"));
        Tick(cut, Books).Change(false);
        Assert.Empty(_ticks);
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

    [Fact]
    public void ATickThePageDidNotTake_IsDrawnAgainFromTheSavedState()
    {
        var cut = RenderList("read", accept: false);

        Tick(cut, Books).Change(false);

        Assert.Equal([(Books, true)], _ticks);
        // The page said no, so the box is back to what is saved: ticked.
        cut.WaitForAssertion(() => Assert.True(Tick(cut, Books).HasAttribute("checked")));
    }

    private static AngleSharp.Dom.IElement Tick(IRenderedComponent<AreaLibraryList> cut, Guid id) =>
        cut.Find($"[data-library-id='{id}'] input");

    private IRenderedComponent<AreaLibraryList> RenderList(string area, Guid[]? hidden = null, Guid[]? primaryHidden = null,
        string? primaryName = null, bool follows = false, bool readOnly = false, bool accept = true)
    {
        var picker = Picker.Select(library => primaryHidden?.Contains(library.Id) == true ? library with { HiddenByPrimary = true } : library).ToList();
        return Render<AreaLibraryList>(parameters => parameters
            .Add(list => list.Area, area)
            .Add(list => list.Libraries, picker)
            .Add(list => list.HiddenIds, (hidden ?? []).ToHashSet())
            .Add(list => list.PersonName, "Sam")
            .Add(list => list.PrimaryName, primaryName)
            .Add(list => list.FollowsPrimary, follows)
            .Add(list => list.ReadOnly, readOnly)
            .Add(list => list.OnHiddenChanged, (Func<Guid, bool, Task<bool>>)((id, isHidden) =>
            {
                _ticks.Add((id, isHidden));
                return Task.FromResult(accept);
            })));
    }
}
