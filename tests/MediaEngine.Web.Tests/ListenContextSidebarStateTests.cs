using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class ListenContextSidebarStateTests
{
    [Fact]
    public async Task PreferredPanelKeepsTheWireWidthButNeverRestoresAnOpenTool()
    {
        var store = new MemorySidebarPreferences();
        store.Seed("desktop:music", new ContextSidebarLayoutDto { Open = true, Width = 444, ActivePanelKey = "lyrics" });
        var playback = new PlaybackSessionController(null!, null!);
        var sidebar = new ListenContextSidebarState(store);
        await sidebar.ReloadAsync();

        Assert.False(sidebar.For(playback).Open);
        Assert.Equal("lyrics", sidebar.For(playback).ActivePanelKey);
        await sidebar.TogglePanelAsync(playback, "queue");
        Assert.True(sidebar.For(playback).Open);
        Assert.Equal("queue", sidebar.For(playback).ActivePanelKey);

        await sidebar.OpenPanelAsync("desktop:music", "history");
        Assert.True(sidebar.For(playback).Open);
        Assert.Equal("history", sidebar.For(playback).ActivePanelKey);
        Assert.Equal(444, sidebar.For(playback).Width);

        await sidebar.CloseAsync(playback);
        Assert.False(sidebar.For(playback).Open);
        Assert.Equal("history", sidebar.For(playback).ActivePanelKey);

        var restored = new ListenContextSidebarState(store);
        await restored.ReloadAsync();
        Assert.False(restored.For(playback).Open);
        Assert.Equal("history", restored.For(playback).ActivePanelKey);
        Assert.Equal(444, restored.For(playback).Width);
    }

    [Fact]
    public async Task AudiobookOnlyAllowsChaptersAndHistory()
    {
        var book = new ListenQueueItem
        {
            WorkId = Guid.NewGuid(),
            AudiobookWorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            MediaType = "Audiobook",
            Title = "Test audiobook",
        };
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [book],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
        });
        var sidebar = new ListenContextSidebarState(new MemorySidebarPreferences());
        await sidebar.ReloadAsync();

        await sidebar.TogglePanelAsync(playback, "chapters");
        Assert.True(sidebar.For(playback).Open);
        Assert.Equal("chapters", sidebar.For(playback).ActivePanelKey);
        await sidebar.OpenPanelAsync("desktop:audiobook", "bookmarks");
        Assert.Equal("chapters", sidebar.For(playback).ActivePanelKey);
        await sidebar.OpenPanelAsync("desktop:audiobook", "history");
        Assert.True(sidebar.For(playback).Open);
        Assert.Equal("history", sidebar.For(playback).ActivePanelKey);
        await sidebar.OpenPanelAsync("desktop:audiobook", "queue");
        Assert.Equal("history", sidebar.For(playback).ActivePanelKey);
    }

    private sealed class MemorySidebarPreferences : IContextSidebarPreferences
    {
        private readonly Dictionary<string, ContextSidebarLayoutDto> _layouts = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(string context, ContextSidebarLayoutDto layout) => _layouts[context] = layout;

        public Task<ContextSidebarLayoutDto> GetAsync(string context, CancellationToken ct = default) =>
            Task.FromResult(_layouts.TryGetValue(context, out var layout)
                ? ContextSidebarPreferences.Copy(layout)
                : ContextSidebarPreferences.Default(context));

        public Task<bool> SaveAsync(string context, ContextSidebarLayoutDto layout, CancellationToken ct = default)
        {
            _layouts[context] = ContextSidebarPreferences.Copy(layout);
            return Task.FromResult(true);
        }
    }
}
