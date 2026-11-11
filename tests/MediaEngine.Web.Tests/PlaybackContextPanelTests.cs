using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackContextPanelTests : AsyncBunitContext
{
    public PlaybackContextPanelTests() { JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLogging(); Services.AddLocalization(); Services.AddNativeUiServices(); }

    [Fact]
    public void ThePanelCardLeavesOutTheChaptersTimelineChoiceWhenAHostAsksFor()
    {
        var asset = Guid.NewGuid();
        var chapters = Enumerable.Range(0, 3).Select(i => new PlaybackChapterDto { Index = i, AssetId = asset, Title = $"Chapter {i + 1}", StartSeconds = i * 60d, EndSeconds = (i + 1) * 60d }).ToList();
        var snapshot = new ListenPlaybackSnapshot
        {
            ProfileId = Guid.NewGuid(),
            Queue = [new() { WorkId = Guid.NewGuid(), AssetId = asset, MediaType = "Audiobook", Title = "Book", Chapters = chapters }],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            DurationSeconds = 180,
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: new Preferences(snapshot.ProfileId!.Value));
        Services.AddSingleton(playback);
        Services.AddSingleton(provider => new PlaybackTimelineScope(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), playback));

        var main = Render<PlaybackPanelCard>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new RecordingSink()).Add(x => x.PanelKey, "chapters"));
        var withoutFooter = Render<PlaybackPanelCard>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new RecordingSink()).Add(x => x.PanelKey, "chapters").Add(x => x.ShowChaptersFooter, false));

        Assert.NotEmpty(main.FindAll("[data-chapters-footer]"));
        Assert.Empty(withoutFooter.FindAll("[data-chapters-footer]"));
    }

    private sealed class Preferences(Guid profile) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId => profile;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(profile));
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class RecordingSink : IPlaybackCommandSink, IPlaybackIdentityNavigationSink
    {
        public ListenPlaybackCommandReplyDto? Reply { get; set; } = new();
        public Task<ListenPlaybackCommandReplyDto?> NavigateIdentityAsync(ListenPlaybackSnapshot snapshot, string kind, Guid id, CancellationToken ct = default) => Task.FromResult<ListenPlaybackCommandReplyDto?>(new());
        public List<(ListenPlaybackSnapshot Snapshot, ListenPlaybackCommandDto Command)> Commands { get; } = [];
        public bool Supports(string action, ListenPlaybackSnapshot snapshot) => true;
        public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default)
        { Commands.Add((snapshot, command)); return Task.FromResult(Reply); }
    }
}
