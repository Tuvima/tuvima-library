using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Profile-owned desktop audio workspace. Playback itself remains in PlaybackSessionController.</summary>
public sealed class ListenContextWorkspaceState(IContextWorkspacePreferences preferences)
{
    private readonly Dictionary<string, ContextWorkspaceLayoutDto> _layouts = new(StringComparer.Ordinal);
    private bool _loaded;

    public event Action? Changed;

    public ContextWorkspaceLayoutDto Music => Layout("desktop:music");
    public ContextWorkspaceLayoutDto Audiobook => Layout("desktop:audiobook");

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        _layouts["desktop:music"] = await preferences.GetAsync("desktop:music", ct);
        _layouts["desktop:audiobook"] = await preferences.GetAsync("desktop:audiobook", ct);
        _loaded = true;
        Changed?.Invoke();
    }

    public ContextWorkspaceLayoutDto For(PlaybackSessionController playback) =>
        playback.IsAudiobookMode ? Audiobook : Music;

    public async Task TogglePanelAsync(PlaybackSessionController playback, string key)
    {
        var context = Context(playback);
        if (!Allowed(context, key)) return;
        var layout = Layout(context);
        var existing = layout.Panels.FindIndex(panel => panel.Key == key);
        if (!layout.Visible)
        {
            if (existing < 0)
            {
                if (layout.Panels.Count == 3) layout.Panels.RemoveAt(2);
                layout.Panels.Add(new ContextWorkspacePanelDto { Key = key });
            }
            layout.Visible = true;
        }
        else if (existing >= 0)
        {
            layout.Panels.RemoveAt(existing);
            if (layout.Panels.Count == 0) layout.Visible = false;
        }
        else
        {
            if (layout.Panels.Count == 3) return;
            layout.Panels.Add(new ContextWorkspacePanelDto { Key = key });
        }
        await CommitAsync(context, layout);
    }

    public async Task CloseAsync(PlaybackSessionController playback)
    {
        var context = Context(playback);
        var layout = Layout(context);
        layout.Visible = false;
        await CommitAsync(context, layout);
    }

    public async Task RemoveAsync(PlaybackSessionController playback, string key)
    {
        var context = Context(playback);
        var layout = Layout(context);
        layout.Panels.RemoveAll(panel => panel.Key == key);
        if (layout.Panels.Count == 0) layout.Visible = false;
        await CommitAsync(context, layout);
    }

    public async Task ToggleCollapsedAsync(PlaybackSessionController playback, string key)
    {
        var context = Context(playback);
        var layout = Layout(context);
        var panel = layout.Panels.FirstOrDefault(item => item.Key == key);
        if (panel is null) return;
        panel.Collapsed = !panel.Collapsed;
        await CommitAsync(context, layout);
    }

    public async Task MoveAsync(PlaybackSessionController playback, string key, int direction)
    {
        var context = Context(playback);
        var layout = Layout(context);
        var index = layout.Panels.FindIndex(panel => panel.Key == key);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= layout.Panels.Count) return;
        (layout.Panels[index], layout.Panels[target]) = (layout.Panels[target], layout.Panels[index]);
        await CommitAsync(context, layout);
    }

    public async Task SetWidthAsync(PlaybackSessionController playback, int width)
    {
        var context = Context(playback);
        var layout = Layout(context);
        layout.Width = Math.Clamp(width, 320, 640);
        await CommitAsync(context, layout);
    }

    public async Task SetSplitAsync(PlaybackSessionController playback, int index, double firstRatio, double secondRatio)
    {
        var context = Context(playback);
        var layout = Layout(context);
        if (index < 0 || index + 1 >= layout.Panels.Count) return;
        if (!double.IsFinite(firstRatio) || !double.IsFinite(secondRatio)) return;
        layout.Panels[index].Ratio = Math.Clamp(firstRatio, .1, .9);
        layout.Panels[index + 1].Ratio = Math.Clamp(secondRatio, .1, .9);
        await CommitAsync(context, layout);
    }

    public async Task ResetAsync(PlaybackSessionController playback)
    {
        var context = Context(playback);
        var layout = ContextWorkspacePreferences.Default(context);
        layout.Visible = true;
        await CommitAsync(context, layout);
    }

    private ContextWorkspaceLayoutDto Layout(string context)
    {
        if (_layouts.TryGetValue(context, out var layout)) return layout;
        // The default is rendered until the active profile's settings arrive.
        layout = ContextWorkspacePreferences.Default(context);
        _layouts[context] = layout;
        return layout;
    }

    private async Task CommitAsync(string context, ContextWorkspaceLayoutDto layout)
    {
        NormalizeRatios(layout);
        _layouts[context] = layout;
        Changed?.Invoke();
        if (_loaded) await preferences.SaveAsync(context, layout);
    }

    private static void NormalizeRatios(ContextWorkspaceLayoutDto layout)
    {
        if (layout.Panels.Count == 0) return;
        var sum = layout.Panels.Sum(panel => double.IsFinite(panel.Ratio) && panel.Ratio > 0 ? panel.Ratio : 1);
        foreach (var panel in layout.Panels)
            panel.Ratio = (double.IsFinite(panel.Ratio) && panel.Ratio > 0 ? panel.Ratio : 1) / sum;
    }

    private static string Context(PlaybackSessionController playback) =>
        playback.IsAudiobookMode ? "desktop:audiobook" : "desktop:music";

    private static bool Allowed(string context, string key) => context switch
    {
        "desktop:music" => key is "queue" or "lyrics" or "history" or "info",
        "desktop:audiobook" => key is "chapters" or "bookmarks" or "history" or "info",
        _ => false,
    };
}
