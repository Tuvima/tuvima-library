using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class ListenContextWorkspaceStateTests
{
    [Fact]
    public async Task ClosedWorkspaceReopensWithItsPanelsAndSavedGeometry()
    {
        var store = new MemoryWorkspacePreferences();
        var playback = new PlaybackSessionController(null!, null!);
        var workspace = new ListenContextWorkspaceState(store);
        await workspace.ReloadAsync();

        await workspace.TogglePanelAsync(playback, "queue");
        await workspace.SetWidthAsync(playback, 468);
        await workspace.SetSplitAsync(playback, 0, .7, .3);
        await workspace.CloseAsync(playback);

        Assert.False(workspace.Music.Visible);
        Assert.Equal(["lyrics", "queue"], workspace.Music.Panels.Select(panel => panel.Key));

        var restored = new ListenContextWorkspaceState(store);
        await restored.ReloadAsync();
        Assert.False(restored.Music.Visible);
        Assert.Equal(468, restored.Music.Width);
        Assert.Equal(.7, restored.Music.Panels[0].Ratio, 3);
        await restored.TogglePanelAsync(playback, "queue");
        Assert.True(restored.Music.Visible);
        Assert.Equal(["lyrics", "queue"], restored.Music.Panels.Select(panel => panel.Key));
    }

    [Fact]
    public async Task ThreePanelsCanBeReorderedCollapsedAndResetWithoutAffectingAudiobook()
    {
        var store = new MemoryWorkspacePreferences();
        var playback = new PlaybackSessionController(null!, null!);
        var workspace = new ListenContextWorkspaceState(store);
        await workspace.ReloadAsync();
        await workspace.TogglePanelAsync(playback, "queue");
        await workspace.TogglePanelAsync(playback, "history");
        await workspace.MoveAsync(playback, "history", -1);
        await workspace.ToggleCollapsedAsync(playback, "lyrics");

        Assert.Equal(["lyrics", "history", "queue"], workspace.Music.Panels.Select(panel => panel.Key));
        Assert.True(workspace.Music.Panels[0].Collapsed);
        Assert.Equal(3, workspace.Music.Panels.Count);
        Assert.False(workspace.Audiobook.Visible);

        await workspace.ResetAsync(playback);
        Assert.True(workspace.Music.Visible);
        Assert.Equal(410, workspace.Music.Width);
        Assert.Equal(["lyrics", "queue"], workspace.Music.Panels.Select(panel => panel.Key));
        Assert.All(workspace.Music.Panels, panel => Assert.False(panel.Collapsed));
    }

    private sealed class MemoryWorkspacePreferences : IContextWorkspacePreferences
    {
        private readonly Dictionary<string, ContextWorkspaceLayoutDto> _layouts = new();

        public Task<ContextWorkspaceLayoutDto> GetAsync(string context, CancellationToken ct = default) =>
            Task.FromResult(_layouts.TryGetValue(context, out var layout)
                ? ContextWorkspacePreferences.Copy(layout)
                : ContextWorkspacePreferences.Default(context));

        public Task<bool> SaveAsync(string context, ContextWorkspaceLayoutDto layout, CancellationToken ct = default)
        {
            _layouts[context] = ContextWorkspacePreferences.Copy(layout);
            return Task.FromResult(true);
        }
    }
}
