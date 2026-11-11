using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Series panel's "Show missing" choice: a kept choice needs no words (the rows themselves change), and one that could
/// not be kept says why in an alert beside the toggle, which stays until the next try or the close button.
/// </summary>
public sealed class SequencePlacementPreferenceNoticeTests : AsyncBunitContext
{
    private static readonly Guid Series = Guid.Parse("50000000-0000-0000-0000-000000000031");
    private bool _saveSucceeds = true;
    private int _saveCalls;

    public SequencePlacementPreferenceNoticeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(TestSessions.Profile, "Test User", "#C9922E", "Standard", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryPreferencesAsync), _ => Task.FromResult<LibraryPreferencesSettings?>(new LibraryPreferencesSettings
            {
                MissingItemDisplay = new Dictionary<string, MissingItemDisplayPolicy>(StringComparer.OrdinalIgnoreCase)
                {
                    ["books"] = new() { Enabled = true, DefaultVisibility = "hidden", Presentation = "inline", PageSize = 10, DetailHydration = "owned_only" },
                },
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetSeriesMissingItemPreferenceAsync), _ => Task.FromResult<SeriesMissingItemPreferenceDto?>(null));
            stub.SetHandler(nameof(IEngineApiClient.SaveSeriesMissingItemPreferenceAsync), args =>
            {
                _saveCalls++;
                return Task.FromResult<SeriesMissingItemPreferenceDto?>(_saveSucceeds
                    ? new SeriesMissingItemPreferenceDto { ProfileId = (Guid)args![0]!, MediaType = (string)args[1]!, ContainerKey = (string)args[2]!, ShowMissing = (bool)args[3]! }
                    : null);
            });
        }));
        Services.AddSingleton(TestSessions.For(kids: false, administrator: false));
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
    }

    [Fact]
    public async Task KeepingShowMissing_SaysNothingAndLeavesNoNotice()
    {
        var cut = RenderPanel();

        await cut.Find(".tl-series-detail__missing input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.WaitForAssertion(() => Assert.Equal(1, _saveCalls));
        Assert.Empty(cut.FindAll("[data-missing-preference-notice]"));
    }

    [Fact]
    public async Task ShowMissingThatCannotBeSaved_ExplainsWhyInPlaceUntilDismissed()
    {
        _saveSucceeds = false;
        var cut = RenderPanel();

        await cut.Find(".tl-series-detail__missing input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-missing-preference-notice][role=alert]")));
        Assert.False(cut.Find(".tl-series-detail__missing input[type=checkbox]").HasAttribute("checked"));

        await cut.Find("[data-missing-preference-notice] .tl-alert-close").ClickAsync();

        Assert.Empty(cut.FindAll("[data-missing-preference-notice]"));
    }

    private IRenderedComponent<SequencePlacementPanel> RenderPanel()
    {
        var items = Enumerable.Range(1, 4).Select(number => new SequenceItemViewModel
        {
            Id = $"8{number:D7}-0000-0000-0000-000000000000",
            EntityType = DetailEntityType.Book,
            Title = $"Book {number}",
            PositionLabel = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PositionNumber = number,
            PositionSort = number,
            IsOwned = number <= 2,
            IsCurrent = number == 1,
        }).ToArray();
        var placement = new SequencePlacementViewModel
        {
            ContainerId = Series.ToString("D"),
            SelectedContainerId = Series.ToString("D"),
            ContainerTitle = "A Series",
            ContainerLabel = "Series",
            ItemLabel = "Book",
            ItemPluralLabel = "Books",
            CurrentItem = items[0],
            OrderedItems = items,
        };
        var cut = Render<SequencePlacementPanel>(p => p.Add(c => c.Placement, placement).Add(c => c.DetailSurface, true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-series-detail__missing")));
        return cut;
    }
}
