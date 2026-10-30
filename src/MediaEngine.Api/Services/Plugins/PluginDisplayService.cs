using System.Text.RegularExpressions;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Holds what plugins want shown on the Library's own surfaces (Plugin architecture spec C21). Publishing is a quick local write with
/// hard size limits, so a plugin cannot slow a page; reading is a plain lookup that never waits on a plugin. A contribution is shown only
/// while its plugin is switched on and not waiting on something it needs. Who may see it (audience, rating, Vault, the person's own switch)
/// is decided by the central access check that arrives with the audience rules, before any page reads this.
/// </summary>
public sealed partial class PluginDisplayService(
    IPluginDisplayRepository repository,
    PluginCatalog catalog,
    TimeProvider clock,
    ILogger<PluginDisplayService>? logger = null)
{
    public const int MaximumKeyLength = 64;
    public const int MaximumSubjectLength = 64;
    public const int MaximumTextLength = 280;
    public const int MaximumTitleLength = 80;
    public const int MaximumIconLength = 40;
    public const int MaximumPerPlugin = 2000;

    public async Task PublishAsync(string pluginId, PluginDisplayContribution contribution, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(contribution);
        if (!Enum.IsDefined(contribution.Slot))
        {
            throw new ArgumentException("That is not one of the Library's display slots.", nameof(contribution));
        }

        var key = Required(contribution.Key, MaximumKeyLength, "name");
        var text = Required(contribution.Text, MaximumTextLength, "text");
        var title = Optional(contribution.Title, MaximumTitleLength, "title");
        var icon = Optional(contribution.Icon, MaximumIconLength, "icon");
        RefuseAnythingButPlainData(pluginId, contribution.Slot, key, text, title, icon);
        var subject = SubjectOf(contribution.SubjectId);
        if (contribution.Strength is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(contribution), "Strength is a number from 0 to 100.");
        }

        if (contribution.Slot != PluginDisplaySlot.SettingsOverview && subject.Length == 0)
        {
            throw new ArgumentException("This slot shows something about one item, so name the item.", nameof(contribution));
        }

        await repository.UpsertAsync(
            new PluginDisplayRow(pluginId, contribution.Slot.ToString(), subject, key, title, text, icon, contribution.Strength, contribution.FromLocalAi, clock.GetUtcNow()),
            MaximumPerPlugin,
            ct).ConfigureAwait(false);
    }

    public Task WithdrawAsync(string pluginId, PluginDisplaySlot slot, string? subjectId, string key, CancellationToken ct) =>
        repository.DeleteAsync(pluginId, slot.ToString(), SubjectOf(subjectId), Required(key, MaximumKeyLength, "name"), ct);

    /// <summary>
    /// What plugins nominate in one slot across every item, strongest first, from plugins that are on and not waiting. Used by slots that
    /// pick items (the Home hero), where the page asks "what is suggested" rather than "what is said about this item".
    /// </summary>
    public async Task<IReadOnlyList<PluginDisplayItem>> ListSlotAsync(PluginDisplaySlot slot, int limit, CancellationToken ct)
    {
        var rows = await repository.ListSlotAsync(slot.ToString(), limit, ct).ConfigureAwait(false);
        return Ready(rows, slot);
    }

    /// <summary>What is ready for one slot and subject, strongest first, from plugins that are on and not waiting.</summary>
    public async Task<IReadOnlyList<PluginDisplayItem>> ListAsync(PluginDisplaySlot slot, string? subjectId, CancellationToken ct)
    {
        var rows = await repository.ListAsync(slot.ToString(), SubjectOf(subjectId), ct).ConfigureAwait(false);
        return Ready(rows, slot);
    }

    private List<PluginDisplayItem> Ready(IReadOnlyList<PluginDisplayRow> rows, PluginDisplaySlot slot)
    {
        var items = new List<PluginDisplayItem>(rows.Count);
        foreach (var row in rows)
        {
            var registration = catalog.Get(row.PluginId);
            if (registration is null
                || !registration.Enabled
                || registration.LoadError is not null
                || !registration.Manifest.Permissions.Contains(PluginPermissionIds.DisplaySlots, StringComparer.Ordinal)
                || catalog.WaitingFor(row.PluginId) is not null)
            {
                continue;
            }

            items.Add(new PluginDisplayItem(
                row.PluginId, registration.Manifest.Name, slot, row.SubjectId.Length == 0 ? null : row.SubjectId, row.Key, row.Title, row.Text, row.Icon, row.Strength, row.FromLocalAi));
        }

        return items;
    }

    // A contribution is words and a plain icon name, nothing else. Markup, script and picture addresses are refused, with the reason
    // logged, because the Library draws these with its own components and a plugin may only point at pictures it declared (none, for now).
    private void RefuseAnythingButPlainData(string pluginId, PluginDisplaySlot slot, string key, string text, string? title, string? icon)
    {
        string? problem = null;
        if (text.Contains('<') || text.Contains('>') || (title is not null && (title.Contains('<') || title.Contains('>'))))
        {
            problem = "its text contains markup or script";
        }
        else if (UrlPattern().IsMatch(text) || (title is not null && UrlPattern().IsMatch(title)))
        {
            problem = "its text contains an address the plugin did not declare";
        }
        else if (icon is not null && !IconName().IsMatch(icon))
        {
            problem = "its icon is not a plain icon name (pictures from addresses are not allowed)";
        }

        if (problem is null)
        {
            return;
        }

        logger?.LogWarning("Plugin {PluginId} offered a {Slot} contribution '{Key}' that was refused: {Problem}.", pluginId, slot, key, problem);
        throw new ArgumentException($"The contribution was refused: {problem}.");
    }

    [GeneratedRegex(@"(?i)(?:[a-z][a-z0-9+.\-]*:/?/|javascript:|data:|\bwww\.)")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^[a-z0-9_\-]{1,40}$")]
    private static partial Regex IconName();

    private static string SubjectOf(string? subjectId)
    {
        var subject = subjectId?.Trim() ?? string.Empty;
        return subject.Length > MaximumSubjectLength
            ? throw new ArgumentException("The item name is too long.", nameof(subjectId))
            : subject;
    }

    private static string Required(string? value, int maximum, string what)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException($"The {what} is empty.", what);
        }

        return trimmed.Length > maximum
            ? throw new PluginLimitExceededException($"The {what} can be at most {maximum} characters.")
            : trimmed;
    }

    private static string? Optional(string? value, int maximum, string what) =>
        string.IsNullOrWhiteSpace(value) ? null : Required(value, maximum, what);
}

/// <summary>A contribution ready to draw, with the plugin it came from (shown on hover).</summary>
public sealed record PluginDisplayItem(
    string PluginId,
    string PluginName,
    PluginDisplaySlot Slot,
    string? SubjectId,
    string Key,
    string? Title,
    string Text,
    string? Icon,
    int Strength,
    bool FromLocalAi);

/// <summary>Gives one plugin the display slots, only with the <c>display-slots</c> permission and only for itself.</summary>
internal sealed class BoundPluginDisplay(string pluginId, IPluginPermissionGate permissions, PluginDisplayService service) : IPluginDisplay
{
    public async Task PublishAsync(PluginDisplayContribution contribution, CancellationToken cancellationToken = default)
    {
        var registration = permissions.Demand(pluginId, PluginPermissionIds.DisplaySlots);
        await service.PublishAsync(registration.Manifest.Id, contribution, cancellationToken).ConfigureAwait(false);
    }

    public async Task WithdrawAsync(PluginDisplaySlot slot, string? subjectId, string key, CancellationToken cancellationToken = default)
    {
        var registration = permissions.Demand(pluginId, PluginPermissionIds.DisplaySlots);
        await service.WithdrawAsync(registration.Manifest.Id, slot, subjectId, key, cancellationToken).ConfigureAwait(false);
    }
}
