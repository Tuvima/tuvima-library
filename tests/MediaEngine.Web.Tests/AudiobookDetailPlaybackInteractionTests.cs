using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class AudiobookDetailPlaybackInteractionTests : AsyncBunitContext
{
    public AudiobookDetailPlaybackInteractionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEditorLauncherService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<DelayedPlaybackPreferences>();
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(provider => provider.GetRequiredService<DelayedPlaybackPreferences>());
        Services.AddSingleton(provider => new PlaybackSessionController(
            null!, null!, preferences: provider.GetRequiredService<IUserPlaybackPreferencesAccessor>()));
    }

    [Fact]
    public async Task GenericWorkContinueStartsAudiobookAndKeepsParentAndContributorIdentities()
    {
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var narratorId = Guid.NewGuid();
        var model = new DetailPageViewModel
        {
            Id = bookId.ToString("D"),
            EntityType = DetailEntityType.Work,
            Title = "Project Hail Mary",
            Facts = new DetailFactsViewModel { MediaKind = "Audiobook", Authors = ["Andy Weir"], Narrators = ["Ray Porter"] },
            Progress = new ProgressViewModel { Percent = 97, Kind = DetailProgressKind.Listening },
            Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
            PrimaryActions =
            [
                new DetailAction
                {
                    Key = "continue",
                    Label = "Continue Listening",
                    Route = $"/details/work/{bookId:D}?context=listen",
                    IsPrimary = true,
                },
            ],
            ContributorGroups =
            [
                new CreditGroupViewModel
                {
                    GroupType = CreditGroupType.Authors,
                    Credits = [new EntityCreditViewModel { EntityId = authorId.ToString("D"), EntityType = RelatedEntityType.Person, DisplayName = "Andy Weir" }],
                },
                new CreditGroupViewModel
                {
                    GroupType = CreditGroupType.Narrators,
                    Credits = [new EntityCreditViewModel { EntityId = narratorId.ToString("D"), EntityType = RelatedEntityType.Person, DisplayName = "Ray Porter" }],
                },
            ],
            MediaGroups =
            [
                new MediaGroupingViewModel
                {
                    Key = "tracks",
                    Items =
                    [
                        new MediaGroupingItemViewModel
                        {
                            Id = chapterId.ToString("D"),
                            EntityType = DetailEntityType.Work,
                            Title = "Opening",
                            AssetId = assetId.ToString("D"),
                            Duration = "1:00:00",
                            DurationSeconds = 3600,
                            ChapterIndex = 0,
                        },
                    ],
                },
            ],
        };

        var playback = Services.GetRequiredService<PlaybackSessionController>();
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue =
            [
                new ListenQueueItem
                {
                    WorkId = Guid.NewGuid(),
                    AssetId = Guid.NewGuid(),
                    MediaType = "Music",
                    Title = "Previous song",
                    StreamUrl = "https://engine.invalid/previous-song",
                },
            ],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Music,
        });
        ListenQueueItem? firstNewSubject = null;
        string? firstNewExperience = null;
        playback.Changed += _ =>
        {
            if (firstNewSubject is null && playback.CurrentItem?.WorkId == chapterId)
            {
                firstNewSubject = playback.CurrentItem;
                firstNewExperience = playback.Experience;
            }
        };

        var cut = Render<DetailPage>(parameters => parameters.Add(page => page.Model, model));
        var clickTask = cut.Find("button.tl-detail-action--continue").ClickAsync();
        var delayedPreferences = Services.GetRequiredService<DelayedPlaybackPreferences>();
        await delayedPreferences.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(firstNewSubject);
        Assert.Equal(PlayerExperienceModes.Audiobook, firstNewExperience);
        Assert.Equal(bookId, firstNewSubject!.AudiobookWorkId);
        Assert.Equal($"/details/person/{authorId:D}", ListenPlaybackIdentityRoutes.Contributor(firstNewSubject.Authors.Single()));
        Assert.Equal($"/details/person/{narratorId:D}", ListenPlaybackIdentityRoutes.Contributor(firstNewSubject.Narrators.Single()));

        delayedPreferences.Settings.TrySetResult(UserPlaybackSettingsDto.CreateDefaults(Guid.Empty));
        await clickTask;

        Assert.Equal(PlayerExperienceModes.Audiobook, playback.Experience);
        Assert.Equal(chapterId, playback.CurrentItem?.WorkId);
        Assert.Equal(bookId, playback.CurrentItem?.AudiobookWorkId);
        Assert.Equal($"/details/person/{authorId:D}", ListenPlaybackIdentityRoutes.Contributor(playback.CurrentItem?.Authors.Single()));
        Assert.Equal($"/details/person/{narratorId:D}", ListenPlaybackIdentityRoutes.Contributor(playback.CurrentItem?.Narrators.Single()));
        Assert.DoesNotContain("/details/work/", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DelayedPlaybackPreferences : IUserPlaybackPreferencesAccessor
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<UserPlaybackSettingsDto?> Settings { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default)
        {
            Requested.TrySetResult();
            return Settings.Task;
        }

        public void UpdateCache(UserPlaybackSettingsDto settings) => Settings.TrySetResult(settings);
        public void Invalidate() { }
    }
}
