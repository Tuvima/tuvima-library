using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// For Me, the personal shelves and grids: it has no My List view of its own (My List is the page at /my-list), and Liked
/// keeps its rule about Documents.
/// </summary>
public sealed class ForMePersonalViewsTests : AsyncBunitContext
{
    private static readonly Guid OwnerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private IEngineApiClient? _api;

    public ForMePersonalViewsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // The test sets _api before the page renders; the first resolve captures it.
        Services.AddSingleton(_ => _api ??= EngineApiClientStub.CreateDefault());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<ViewWorkspaceService>();
        Services.AddScoped(provider => new DeviceContextService(provider.GetRequiredService<IEngineApiClient>()));
        Services.AddSupplyValueFromQueryProvider();
    }

    private static DisplayArtworkDto Art()
    {
        var fields = typeof(DisplayArtworkDto).GetProperties()
            .Where(p => p.Name.EndsWith("Url", StringComparison.Ordinal))
            .ToDictionary(p => p.Name, _ => "/art.jpg");
        return JsonSerializer.Deserialize<DisplayArtworkDto>(JsonSerializer.Serialize(fields))!;
    }

    private static DisplayCardDto Card(Guid id, string kind, string title) =>
        new(id, id, Guid.NewGuid(), null, kind, "work", title, null, [], Art(), "portrait", "default", "caption", "bottom",
            null, [], new(true, false, false, false), Now.AddDays(-30))
        { ContinuationState = DisplayContinuationState.Unstarted };

    private static DisplayPageDto Page(params DisplayCardDto[] cards) =>
        new("page", "Page", null, null, [new DisplayShelfDto("shelf", "Shelf", null, cards, null)], cards);

    private sealed record Data
    {
        /// <summary>What the Engine's item lookup can find: any saved or liked id that has a card here comes back.</summary>
        public DisplayPageDto? Library { get; init; }
        public List<ProfileSavedItemDto> Saved { get; init; } = [];
        public List<ProfileReactionDto> Reactions { get; init; } = [];
        public bool? CanViewDocuments { get; init; }
    }

    private void UseData(Data data) => _api = EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(
            new List<ProfileViewModel> { new(OwnerId, "Shy", "#C9922E", "Administrator", DateTimeOffset.UtcNow) }));
        stub.SetHandler(nameof(IEngineApiClient.GetSavedItemsAsync), _ => Task.FromResult<IReadOnlyList<ProfileSavedItemDto>>(data.Saved));
        stub.SetHandler(nameof(IEngineApiClient.GetProfileReactionsAsync), _ => Task.FromResult<IReadOnlyList<ProfileReactionDto>>(data.Reactions));
        stub.SetHandler(nameof(IEngineApiClient.GetDisplayItemsAsync), args =>
        {
            var ids = (IReadOnlyList<Guid>)args![0]!;
            var found = (data.Library?.Catalog ?? []).Where(card => ids.Contains(card.Id)).ToArray();
            return Task.FromResult<DisplayPageDto?>(Page(found));
        });
        stub.SetHandler(nameof(IEngineApiClient.GetCollectionCatalogAsync), _ => Task.FromResult(new List<CollectionManagementCatalogViewModel>()));
        stub.SetHandler(nameof(IEngineApiClient.GetManagedCollectionsAsync), _ => Task.FromResult(new List<ManagedCollectionViewModel>()));
        if (data.CanViewDocuments is { } canView)
        {
            stub.SetHandler(nameof(IEngineApiClient.GetViewScopesAsync), _ =>
                Task.FromResult<ViewScopeResolutionDto?>(new(
                    new ViewResolvedScopeDto(ViewScopeKind.Mine, OwnerId, false), [], CanViewDocuments: canView)));
        }
    });

    private IRenderedComponent<ForMe> RenderForMe(string url)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<ForMe>();
    }

    // The all-areas Liked grid lives at /all/personal/liked, and the Engine's grid read never includes Documents. What is left on
    // For Me is the per-area grid, and no area (Watch, Read, Listen, Collections) holds a Document, so one never shows there.
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    [InlineData(true)]
    public void TheForMeLikedAreaGridNeverShowsDocumentItems(bool? canView)
    {
        var document = Guid.Parse("d0000000-0000-0000-0000-000000000001");
        var film = Guid.Parse("d0000000-0000-0000-0000-000000000002");
        UseData(new Data
        {
            CanViewDocuments = canView,
            Library = Page(Card(document, "Document", "Tax Return"), Card(film, "Movie", "Liked Film")),
            Reactions =
            [
                new(ProfileEntityKind.Movie, document, ProfileReactionKind.Like, Now),
                new(ProfileEntityKind.Movie, film, ProfileReactionKind.Like, Now.AddMinutes(-1)),
            ],
        });

        var cut = RenderForMe("/for-me?view=liked&area=watch");

        cut.WaitForAssertion(() => Assert.Contains("Liked Film", cut.Markup), TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("Tax Return", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ForMeNoLongerHasAMyListViewAndItsTabsAreTheOthers()
    {
        var film = Guid.Parse("d0000000-0000-0000-0000-000000000009");
        UseData(new Data
        {
            Library = Page(Card(film, "Movie", "Saved Area Film")),
            Saved = [new(ProfileEntityKind.Movie, film, Now, null)],
        });

        // The old address falls back to the first tab (Overview), like any unknown view; it does not show the saved grid.
        var cut = RenderForMe("/for-me?view=my-list&area=watch");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".for-me-page__shelves")), TimeSpan.FromSeconds(10));
        var tabs = cut.FindAll(".for-me-page__navigation [role='tab'], .for-me-page__navigation button, .for-me-page__navigation a")
            .Select(tab => tab.TextContent)
            .ToArray();
        Assert.NotEmpty(tabs);
        Assert.DoesNotContain(tabs, text => text.Contains("My List", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".for-me-page__view-heading"));
    }

    [Fact]
    public void TheMyListShelfOnOverviewSendsSeeAllToTheMyListPage()
    {
        var film = Guid.Parse("d0000000-0000-0000-0000-000000000010");
        UseData(new Data
        {
            Library = Page(Card(film, "Movie", "Saved Film")),
            Saved = [new(ProfileEntityKind.Movie, film, Now, null)],
        });

        var cut = RenderForMe("/for-me");

        cut.WaitForAssertion(() => Assert.Contains("Saved Film", cut.Markup), TimeSpan.FromSeconds(10));
        var viewAll = Assert.Single(cut.FindAll("a.media-tile-shelf-view-all"));
        Assert.Equal("/my-list", viewAll.GetAttribute("href"));
    }

    [Fact]
    public void MyListIsTheDockDestinationAndLikedKeepsItsGridRoute()
    {
        Assert.Equal("/my-list", ShellDock.MyListPath);
        var entry = Assert.Single(ShellDock.ResolvePhone(isKids: false), tab => tab.Key == "MyList");
        Assert.Equal("/my-list", entry.Path);
        Assert.Equal("/all/personal/liked", ForMe.LikedGridRoute);
    }
}
