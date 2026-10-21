using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// The plugin directory (Developer tools spec D-F): the list of plugins Tuvima keeps on GitHub. The Engine keeps a saved
/// copy on disk and shows that copy; it goes to GitHub only when an administrator opens Available plugins and the last
/// check is more than a day old, or when Check now is pressed. There is no background schedule, nothing about the person
/// or the Library is sent (a plain GET with the standard User-Agent), and with the switch off no request is made at all.
/// The Engine only reads the list here; it does not install from it yet.
/// </summary>
public sealed class PluginDirectoryService
{
    /// <summary>
    /// Whether this Library can install a listed plugin. It cannot until the install packet adds the download, which flips
    /// this one constant; the Dashboard shows an Install button only when the Engine says so.
    /// </summary>
    public const bool InstallSupported = false;

    public const string HttpClientName = "plugin_catalog";

    /// <summary>How old the last check may be before opening Available plugins checks again.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    // With no saved copy to show, a failed check is retried on the next visit, but not on every one: an Engine that is
    // offline would otherwise wait for the connection to time out each time the page is opened.
    private static readonly TimeSpan RetryWithoutCopyInterval = TimeSpan.FromMinutes(5);

    private const long MaxListBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions SavedJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly JsonDocumentOptions ListJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigurationLoader _configurationLoader;
    private readonly TuvimaDataPaths _dataPaths;
    private readonly ILogger<PluginDirectoryService> _logger;
    private readonly TimeProvider _time;
    private readonly PluginBlocklist? _blocklist;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PluginDirectoryService(
        IHttpClientFactory httpClientFactory,
        IConfigurationLoader configurationLoader,
        TuvimaDataPaths dataPaths,
        ILogger<PluginDirectoryService> logger,
        TimeProvider? timeProvider = null,
        PluginBlocklist? blocklist = null)
    {
        _httpClientFactory = httpClientFactory;
        _configurationLoader = configurationLoader;
        _dataPaths = dataPaths;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _blocklist = blocklist;
    }

    /// <summary>Where the saved copy lives: <c>plugin-directory/directory.json</c> in the Tuvima data folder.</summary>
    public string SavedCopyPath => Path.Combine(_dataPaths.Root, "plugin-directory", "directory.json");

    /// <summary>The directory as it should be shown now, checking GitHub first when the switch is on and the last check is old or missing.</summary>
    public async Task<PluginDirectoryResponse> GetAsync(IReadOnlyCollection<string>? installedIds = null, CancellationToken ct = default)
    {
        var settings = _configurationLoader.LoadCore().PluginCatalog;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = await LoadSavedAsync(settings.ApprovedPluginsUrl, ct).ConfigureAwait(false);
            if (CanCheck(settings) && NeedsCheck(saved))
            {
                saved = await CheckAsync(settings.ApprovedPluginsUrl, saved, ct).ConfigureAwait(false);
            }

            return Build(settings, saved, installedIds);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Check now: asks GitHub whatever the age of the saved copy. With the switch off it makes no request.</summary>
    public async Task<PluginDirectoryResponse> CheckNowAsync(IReadOnlyCollection<string>? installedIds = null, CancellationToken ct = default)
    {
        var settings = _configurationLoader.LoadCore().PluginCatalog;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = await LoadSavedAsync(settings.ApprovedPluginsUrl, ct).ConfigureAwait(false);
            if (CanCheck(settings))
            {
                saved = await CheckAsync(settings.ApprovedPluginsUrl, saved, ct).ConfigureAwait(false);
            }

            return Build(settings, saved, installedIds);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The directory from the saved copy alone. Never makes a request.</summary>
    public async Task<PluginDirectoryResponse> GetSavedAsync(IReadOnlyCollection<string>? installedIds = null, CancellationToken ct = default)
    {
        var settings = _configurationLoader.LoadCore().PluginCatalog;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Build(settings, await LoadSavedAsync(settings.ApprovedPluginsUrl, ct).ConfigureAwait(false), installedIds);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records that the person has now seen every plugin in the saved copy, so none of them is new next time.</summary>
    public async Task<PluginDirectoryResponse> MarkSeenAsync(IReadOnlyCollection<string>? installedIds = null, CancellationToken ct = default)
    {
        var settings = _configurationLoader.LoadCore().PluginCatalog;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var saved = await LoadSavedAsync(settings.ApprovedPluginsUrl, ct).ConfigureAwait(false);
            if (saved?.List is { } list && PluginDirectoryParser.TryParse(list, out _) is { } parsed)
            {
                saved = saved with { PreviousIds = parsed.Entries.Select(entry => entry.Id).ToList() };
                await SaveAsync(saved, ct).ConfigureAwait(false);
            }

            return Build(settings, saved, installedIds);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Switches the directory on or off in <c>core.json</c> (<c>plugin_catalog.enabled</c>). Nothing else in the file changes.</summary>
    public void SetEnabled(bool enabled)
    {
        var core = _configurationLoader.LoadCore();
        core.PluginCatalog.Enabled = enabled;
        _configurationLoader.SaveCore(core);
    }

    /// <summary>
    /// What a person is shown before installing one listed plugin, built from what the list declares for it. Reads the saved
    /// copy only. Throws <see cref="KeyNotFoundException"/> when the plugin is not in it.
    /// </summary>
    public async Task<ApprovalPreviewResponse> PreviewInstallAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var settings = _configurationLoader.LoadCore().PluginCatalog;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        PluginDirectoryEntryDto? entry;
        try
        {
            var saved = await LoadSavedAsync(settings.ApprovedPluginsUrl, ct).ConfigureAwait(false);
            var parsed = saved?.List is { } list ? PluginDirectoryParser.TryParse(list, out _) : null;
            entry = parsed?.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, pluginId.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }

        if (entry is null)
        {
            throw new KeyNotFoundException($"Plugin '{pluginId.Trim()}' is not in the plugin directory.");
        }

        var manifest = new PluginManifest
        {
            Id = entry.Id,
            Name = entry.Name,
            Version = entry.Version,
            Kind = ToKind(entry.Kind),
            Description = entry.Description,
            Permissions = entry.Permissions,
            Network = entry.Network
                .Select(destination => new PluginNetworkDestination { Host = destination.Host, Sends = destination.Sends })
                .ToList(),
        };
        var statement = ApprovalStatementBuilder.ForDirectoryEntry(manifest, ToTrust(entry.TrustLevel));
        return ApprovalStatementBuilder.ToPreview(ApprovalKinds.Plugin, entry.Id, statement);
    }

    // ── Checking ─────────────────────────────────────────────────────────────

    private static bool CanCheck(PluginCatalogSettings settings) => settings.Enabled && IsAllowedGitHubUrl(settings.ApprovedPluginsUrl);

    private bool NeedsCheck(SavedDirectory? saved)
    {
        if (saved?.LastCheckedAt is not { } checkedAt)
        {
            return true;
        }

        var age = _time.GetUtcNow() - checkedAt;
        return age >= CheckInterval || (saved.List is null && age >= RetryWithoutCopyInterval);
    }

    private async Task<SavedDirectory?> CheckAsync(string sourceUrl, SavedDirectory? saved, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var current = saved ?? new SavedDirectory { SourceUrl = sourceUrl };
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            // The client follows redirects (a github.com address may hand over to raw.githubusercontent.com), but the list is
            // only accepted when the address it finally came from is still a GitHub one.
            if (response.RequestMessage?.RequestUri is { } finalAddress && !IsAllowedGitHubUrl(finalAddress.AbsoluteUri))
            {
                throw new InvalidDataException("The plugin directory was redirected away from GitHub.");
            }

            var bytes = await ReadLimitedAsync(response, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, ListJson);
            if (PluginDirectoryParser.TryParse(document.RootElement, out var problem) is null)
            {
                throw new InvalidDataException(problem);
            }

            current = current with
            {
                SourceUrl = sourceUrl,
                LastCheckedAt = now,
                LastSuccessAt = now,
                LastCheckFailed = false,
                List = document.RootElement.Clone(),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The earlier copy stays; only the time of this try is recorded, so the page can say the check did not work.
            _logger.LogWarning(ex, "The plugin directory could not be fetched from {SourceUrl}", sourceUrl);
            current = current with { SourceUrl = sourceUrl, LastCheckedAt = now, LastCheckFailed = true };
        }

        await SaveAsync(current, ct).ConfigureAwait(false);
        return current;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxListBytes)
        {
            throw new InvalidDataException("The plugin directory list is larger than expected.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxListBytes)
            {
                throw new InvalidDataException("The plugin directory list is larger than expected.");
            }
        }

        return buffer.ToArray();
    }

    // ── Saved copy ───────────────────────────────────────────────────────────

    private async Task<SavedDirectory?> LoadSavedAsync(string sourceUrl, CancellationToken ct)
    {
        var path = SavedCopyPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var saved = await JsonSerializer.DeserializeAsync<SavedDirectory>(stream, SavedJson, ct).ConfigureAwait(false);
            // A copy kept from another address (the setting was changed) is not this list.
            return saved is not null && string.Equals(saved.SourceUrl, sourceUrl, StringComparison.OrdinalIgnoreCase) ? saved : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The saved plugin directory at {Path} could not be read and is ignored", path);
            return null;
        }
    }

    private async Task SaveAsync(SavedDirectory saved, CancellationToken ct)
    {
        var path = SavedCopyPath;
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, saved, SavedJson, ct).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            _blocklist?.NotifyChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The page still gets this answer; the next visit will check again.
            _logger.LogWarning(ex, "The plugin directory could not be saved to {Path}", path);
        }
    }

    // ── Response ─────────────────────────────────────────────────────────────

    private PluginDirectoryResponse Build(PluginCatalogSettings settings, SavedDirectory? saved, IReadOnlyCollection<string>? installedIds)
    {
        var response = new PluginDirectoryResponse
        {
            SourceUrl = settings.ApprovedPluginsUrl,
            Enabled = settings.Enabled,
            InstallSupported = InstallSupported,
            LastCheckedAt = saved?.LastCheckedAt,
            LastSuccessAt = saved?.LastSuccessAt,
        };

        var parsed = saved?.List is { } list ? PluginDirectoryParser.TryParse(list, out _) : null;
        if (parsed is not null)
        {
            var previous = new HashSet<string>(saved!.PreviousIds, StringComparer.OrdinalIgnoreCase);
            var installed = new HashSet<string>(installedIds ?? [], StringComparer.OrdinalIgnoreCase);
            response.SchemaVersion = parsed.SchemaVersion;
            response.UpdatedAt = parsed.UpdatedAt;
            response.SkippedCount = parsed.Skipped;
            response.Plugins = parsed.Entries
                .Select(entry =>
                {
                    entry.IsNew = !previous.Contains(entry.Id);
                    entry.IsInstalled = installed.Contains(entry.Id);
                    return entry;
                })
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            response.NewCount = response.Plugins.Count(entry => entry.IsNew && !entry.IsInstalled);
        }

        var hasCopy = parsed is not null && saved?.LastSuccessAt is not null;
        var lastCheckFailed = saved is { LastCheckFailed: true };
        var copyDate = saved?.LastSuccessAt is { } date ? date.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : null;
        if (!settings.Enabled)
        {
            response.Status = "disabled";
            response.Message = "Plugin directory is switched off.";
        }
        else if (!IsAllowedGitHubUrl(settings.ApprovedPluginsUrl))
        {
            response.Status = "invalid_source";
            response.Message = "The plugin directory address must point to raw.githubusercontent.com or github.com.";
        }
        else if (lastCheckFailed)
        {
            response.Status = hasCopy ? "saved_copy" : "unavailable";
            response.Message = hasCopy
                ? $"Couldn't reach the plugin directory; showing the copy from {copyDate}."
                : "Couldn't reach the plugin directory, and there is no saved copy yet.";
        }
        else if (!hasCopy)
        {
            response.Status = "unavailable";
            response.Message = "The plugin directory has not been checked yet.";
        }

        return response;
    }

    private static bool IsAllowedGitHubUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && (string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase));
    }

    private static PluginKind ToKind(string kind) => kind switch
    {
        "declarative" => PluginKind.Declarative,
        "service" => PluginKind.Service,
        "sandboxed" => PluginKind.Sandboxed,
        _ => PluginKind.Official,
    };

    private static PluginTrustLevel ToTrust(string trust) => trust switch
    {
        "official" => PluginTrustLevel.Official,
        "verified" => PluginTrustLevel.Verified,
        _ => PluginTrustLevel.Community,
    };

    /// <summary>The saved copy and what the Engine remembers about it: when it last tried and last succeeded, and which plugins the person has already seen.</summary>
    private sealed record SavedDirectory
    {
        [JsonPropertyName("source_url")]
        public string SourceUrl { get; init; } = "";

        [JsonPropertyName("last_checked_at")]
        public DateTimeOffset? LastCheckedAt { get; init; }

        [JsonPropertyName("last_success_at")]
        public DateTimeOffset? LastSuccessAt { get; init; }

        /// <summary>True when the latest try failed (the copy, if any, is from an earlier try).</summary>
        [JsonPropertyName("last_check_failed")]
        public bool LastCheckFailed { get; init; }

        [JsonPropertyName("previous_ids")]
        public List<string> PreviousIds { get; init; } = [];

        /// <summary>The list exactly as GitHub sent it; null until a check has worked.</summary>
        [JsonPropertyName("list")]
        public JsonElement? List { get; init; }
    }
}
