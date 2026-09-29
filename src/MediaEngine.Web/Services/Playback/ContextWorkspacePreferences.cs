using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public interface IContextWorkspacePreferences
{
    Task<ContextWorkspaceLayoutDto> GetAsync(string context, CancellationToken ct = default);
    Task<bool> SaveAsync(string context, ContextWorkspaceLayoutDto layout, CancellationToken ct = default);
}

/// <summary>Stores desktop context geometry in the active profile's playback settings.</summary>
public sealed class ContextWorkspacePreferences(
    UIOrchestratorService orchestrator,
    IUserPlaybackPreferencesAccessor accessor) : IContextWorkspacePreferences
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public async Task<ContextWorkspaceLayoutDto> GetAsync(string context, CancellationToken ct = default)
    {
        var settings = await accessor.GetAsync(ct);
        return settings?.ContextWorkspaces is { } workspaces && workspaces.TryGetValue(context, out var layout)
            ? Copy(layout)
            : Default(context);
    }

    public async Task<bool> SaveAsync(string context, ContextWorkspaceLayoutDto layout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context) || context.Length > 64)
            return false;

        await _saveGate.WaitAsync(ct);
        try
        {
            // Fetch immediately before writing so an unrelated settings edit is retained.
            var settings = await orchestrator.GetPlaybackSettingsAsync(ct);
            if (settings is null)
                return false;
            settings.ContextWorkspaces ??= new Dictionary<string, ContextWorkspaceLayoutDto>(StringComparer.Ordinal);
            settings.ContextWorkspaces[context] = Copy(layout);
            var saved = await orchestrator.SavePlaybackSettingsAsync(settings, ct);
            if (saved is null)
                return false;
            accessor.UpdateCache(saved);
            return true;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public static ContextWorkspaceLayoutDto Default(string context) => new()
    {
        Visible = false,
        Width = 410,
        Panels = context switch
        {
            "desktop:music" => [new() { Key = "lyrics", Ratio = .65 }, new() { Key = "queue", Ratio = .35 }],
            "desktop:audiobook" => [new() { Key = "chapters", Ratio = .6 }, new() { Key = "bookmarks", Ratio = .4 }],
            "desktop:video" => [new() { Key = "next-up" }],
            _ => [],
        },
    };

    public static ContextWorkspaceLayoutDto Copy(ContextWorkspaceLayoutDto source) => new()
    {
        Visible = source.Visible,
        Width = Math.Clamp(source.Width, 320, 640),
        Panels = source.Panels.Select(panel => new ContextWorkspacePanelDto
        {
            Key = panel.Key,
            Ratio = panel.Ratio,
            Collapsed = panel.Collapsed,
        }).ToList(),
    };
}
