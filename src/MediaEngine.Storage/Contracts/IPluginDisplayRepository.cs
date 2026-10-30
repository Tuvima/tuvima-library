namespace MediaEngine.Storage.Contracts;

/// <summary>One stored contribution. <see cref="Slot"/> is the slot's name; the Library validates it before it gets here.</summary>
public sealed record PluginDisplayRow(
    string PluginId,
    string Slot,
    string SubjectId,
    string Key,
    string? Title,
    string Text,
    string? Icon,
    int Strength,
    bool FromLocalAi,
    DateTimeOffset UpdatedAt);

/// <summary>What plugins have asked to be shown on the Library's own surfaces (see <c>plugin_display_contributions</c>).</summary>
public interface IPluginDisplayRepository
{
    /// <summary>Adds or replaces a contribution. When the plugin has more than <paramref name="maxPerPlugin"/>, the least recently updated go.</summary>
    /// <returns>How many old contributions were dropped to make room.</returns>
    Task<int> UpsertAsync(PluginDisplayRow row, int maxPerPlugin, CancellationToken ct = default);

    Task<bool> DeleteAsync(string pluginId, string slot, string subjectId, string key, CancellationToken ct = default);

    /// <summary>Contributions in one slot for one subject (empty for the ones not about an item), strongest first.</summary>
    Task<IReadOnlyList<PluginDisplayRow>> ListAsync(string slot, string subjectId, CancellationToken ct = default);

    /// <summary>The strongest contributions in one slot across every item (for slots that nominate items, such as the Home hero), at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<PluginDisplayRow>> ListSlotAsync(string slot, int limit, CancellationToken ct = default);

    Task<int> CountAsync(string pluginId, CancellationToken ct = default);
}
