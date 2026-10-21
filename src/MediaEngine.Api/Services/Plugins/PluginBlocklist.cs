using System.Text.Json;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>Why a plugin is on the blocklist, in words the person can read.</summary>
/// <param name="Reason">The reason the directory gives.</param>
public sealed record PluginBlockEntry(string Reason);

/// <summary>
/// The directory's blocklist (Developer tools spec WP7): plugins Tuvima has pulled, for a security or safety reason. The
/// list travels inside the saved directory copy, so a blocked plugin is switched off after the next directory check and
/// stays blocked while offline. An entry may name versions; without versions every version is blocked.
/// </summary>
public sealed class PluginBlocklist
{
    private const int MaxEntries = 512;
    private const int MaxReasonLength = 500;

    private readonly TuvimaDataPaths _dataPaths;
    private readonly ILogger<PluginBlocklist> _logger;
    private readonly object _lock = new();
    private DateTime _readStamp = DateTime.MinValue;
    private long _readLength = -1;
    private List<BlockedItem> _items = [];

    public PluginBlocklist(TuvimaDataPaths dataPaths, ILogger<PluginBlocklist> logger)
    {
        _dataPaths = dataPaths;
        _logger = logger;
    }

    /// <summary>Raised after the saved directory copy changed, so the plugin list can be worked out again.</summary>
    public event Action? Changed;

    private string SavedCopyPath => Path.Combine(_dataPaths.Root, "plugin-directory", "directory.json");

    /// <summary>Tells the catalog the saved copy was rewritten.</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>The block on this plugin version, or null when it is not blocked.</summary>
    public PluginBlockEntry? Find(string pluginId, string version)
    {
        List<BlockedItem> items;
        lock (_lock)
        {
            Refresh();
            items = _items;
        }

        var match = items.FirstOrDefault(item =>
            string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase)
            && (item.Versions.Count == 0 || item.Versions.Contains(version.Trim(), StringComparer.OrdinalIgnoreCase)));
        return match is null ? null : new PluginBlockEntry(match.Reason);
    }

    /// <summary>Reads the file only when it has changed since the last read.</summary>
    private void Refresh()
    {
        var path = SavedCopyPath;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                _items = [];
                _readStamp = DateTime.MinValue;
                _readLength = -1;
                return;
            }

            if (info.LastWriteTimeUtc == _readStamp && info.Length == _readLength)
            {
                return;
            }

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            _items = document.RootElement.TryGetProperty("list", out var list) ? Parse(list) : [];
            _readStamp = info.LastWriteTimeUtc;
            _readLength = info.Length;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable copy blocks nothing new, but the earlier blocks stay in force.
            _logger.LogWarning(ex, "The plugin blocklist could not be read from {Path}; earlier blocks stay in force", path);
        }
    }

    /// <summary>Reads the optional <c>blocklist</c> array of a directory list. Entries that cannot be read are left out.</summary>
    internal static List<BlockedItem> Parse(JsonElement list)
    {
        var result = new List<BlockedItem>();
        if (list.ValueKind != JsonValueKind.Object
            || !list.TryGetProperty("blocklist", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (result.Count >= MaxEntries)
            {
                break;
            }

            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("id", out var idValue)
                || idValue.ValueKind != JsonValueKind.String
                || idValue.GetString()?.Trim() is not { Length: > 0 } id)
            {
                continue;
            }

            var reason = element.TryGetProperty("reason", out var reasonValue) && reasonValue.ValueKind == JsonValueKind.String
                ? reasonValue.GetString()?.Trim() ?? ""
                : "";
            if (reason.Length > MaxReasonLength)
            {
                reason = reason[..MaxReasonLength];
            }

            var versions = new List<string>();
            if (element.TryGetProperty("versions", out var versionValues) && versionValues.ValueKind == JsonValueKind.Array)
            {
                foreach (var version in versionValues.EnumerateArray())
                {
                    if (version.ValueKind == JsonValueKind.String && version.GetString()?.Trim() is { Length: > 0 } text)
                    {
                        versions.Add(text);
                    }
                }
            }

            result.Add(new BlockedItem(id, reason, versions));
        }

        return result;
    }

    internal sealed record BlockedItem(string Id, string Reason, IReadOnlyList<string> Versions);
}
