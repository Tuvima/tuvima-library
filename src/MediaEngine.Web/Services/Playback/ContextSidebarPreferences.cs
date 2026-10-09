using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public interface IContextSidebarPreferences
{
    Task<ContextSidebarLayoutDto> GetAsync(string context, CancellationToken ct = default);
    Task<bool> SaveAsync(string context, ContextSidebarLayoutDto layout, CancellationToken ct = default);
}

/// <summary>Stores one active panel and its width per profile/device context.</summary>
public sealed class ContextSidebarPreferences(
    UIOrchestratorService orchestrator,
    IUserPlaybackPreferencesAccessor accessor) : IContextSidebarPreferences
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public async Task<ContextSidebarLayoutDto> GetAsync(string context, CancellationToken ct = default)
    {
        var settings = await accessor.GetAsync(ct);
        return settings?.ContextSidebars is { } sidebars && sidebars.TryGetValue(context, out var layout)
            ? Copy(layout)
            : Default(context);
    }

    public async Task<bool> SaveAsync(string context, ContextSidebarLayoutDto layout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context) || context.Length > 64)
        {
            return false;
        }
        var initial = await accessor.GetAsync(ct);
        if (initial is null)
        {
            return false;
        }
        var targetProfileId = initial.ProfileId;
        var generation = accessor.Generation;
        await _saveGate.WaitAsync(ct);
        try
        {
            if (generation != accessor.Generation || accessor.ActiveProfileId is { } beforeWriteProfile && beforeWriteProfile != targetProfileId)
            {
                return false;
            }
            var settings = await orchestrator.GetPlaybackSettingsAsync(ct);
            if (settings is null || settings.ProfileId != targetProfileId
                || generation != accessor.Generation || accessor.ActiveProfileId is { } beforePostProfile && beforePostProfile != targetProfileId)
            {
                return false;
            }
            settings.ContextSidebars ??= new Dictionary<string, ContextSidebarLayoutDto>(StringComparer.OrdinalIgnoreCase);
            settings.ContextSidebars[context] = Copy(layout);
            var saved = await orchestrator.SavePlaybackSettingsAsync(settings, ct);
            var active = await accessor.GetAsync(ct);
            if (saved is null || saved.ProfileId != targetProfileId || active?.ProfileId != targetProfileId
                || generation != accessor.Generation || accessor.ActiveProfileId is { } afterPostProfile && afterPostProfile != targetProfileId)
            {
                return false;
            }
            accessor.UpdateCache(saved);
            return true;
        }
        finally { _saveGate.Release(); }
    }

    public static ContextSidebarLayoutDto Default(string context) => new()
    {
        Open = false,
        Width = 340,
        ActivePanelKey = context switch
        {
            "desktop:music" => "queue",
            "desktop:audiobook" => "chapters",
            "desktop:video" => "next-up",
            "desktop:ingestion" => "run",
            _ => null,
        },
    };

    public static ContextSidebarLayoutDto Copy(ContextSidebarLayoutDto source) => new()
    {
        Open = source.Open,
        Width = Math.Clamp(source.Width, 320, 480),
        ActivePanelKey = string.IsNullOrWhiteSpace(source.ActivePanelKey) ? null : source.ActivePanelKey.Trim(),
    };
}
