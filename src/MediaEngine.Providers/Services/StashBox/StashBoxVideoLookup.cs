using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services.StashBox;

/// <summary>
/// The Official Stash-box lookup (the adult-only "stashbox" provider). It contacts a stash-box server and only ever answers
/// about Vault items: the caller asks <see cref="IProviderAudienceGate"/> first. By title it sends only the title; by file
/// fingerprint it sends fingerprints, and only while the administrator's separate permission is on. Without a working key it
/// finds nothing, which is the same as the library's lookup being set to None. It only reads from a server; it never submits.
/// </summary>
public sealed class StashBoxVideoLookup : IAdultVideoLookup
{
    private readonly IConfigurationLoader _configuration;
    private readonly IStashBoxSettings _settings;
    private readonly StashBoxClient _client;
    private readonly ILogger<StashBoxVideoLookup> _logger;

    public StashBoxVideoLookup(
        IConfigurationLoader configuration,
        IStashBoxSettings settings,
        StashBoxClient client,
        ILogger<StashBoxVideoLookup> logger)
    {
        _configuration = configuration;
        _settings = settings;
        _client = client;
        _logger = logger;
    }

    public string ProviderName => StashBoxSettings.ProviderName;

    public async Task<AdultVideoInfo?> LookupAsync(AdultLookupQuery query, CancellationToken ct = default)
    {
        var provider = _configuration.LoadProvider(StashBoxSettings.ProviderName);
        if (provider is null || !provider.Enabled || !provider.AdultOnly)
        {
            return null;
        }

        var title = StashBoxClient.Clean(query.Title, 300);
        var useFingerprints = provider.FileFingerprint && query.Fingerprints.Count > 0;
        if (title is null && !useFingerprints)
        {
            return null;
        }

        var snapshot = await _settings.GetAsync(ct).ConfigureAwait(false);
        useFingerprints &= snapshot.FingerprintPermission;

        var servers = snapshot.Servers
            .Where(s => s.HasKey && s.Connection != StashBoxConnectionStates.NotConnected)
            .OrderBy(s => string.Equals(s.Id, query.PreferredServerId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        foreach (var server in servers)
        {
            if (await _settings.GetCredentialAsync(server.Id, ct).ConfigureAwait(false) is not { } credential)
            {
                continue;
            }

            StashBoxScene? scene = null;
            var signal = AdultMatchSignals.Title;
            var rejected = false;

            if (useFingerprints)
            {
                var reply = await _client.FindByFingerprintsAsync(credential.Address, credential.Key, query.Fingerprints, ct).ConfigureAwait(false);
                if (await NoteAsync(server, reply, ct).ConfigureAwait(false))
                {
                    continue;
                }

                var distinct = reply.Scenes.DistinctBy(s => s.Id).ToList();
                if (distinct.Count == 1)
                {
                    scene = distinct[0];
                    signal = AdultMatchSignals.Fingerprint;
                }
                else if (distinct.Count > 1)
                {
                    // Two records claim the same file: not applied (Videos spec D37).
                    rejected = true;
                }
            }

            if (scene is null && !rejected && title is not null)
            {
                var reply = await _client.SearchByTitleAsync(credential.Address, credential.Key, title, ct).ConfigureAwait(false);
                if (await NoteAsync(server, reply, ct).ConfigureAwait(false))
                {
                    continue;
                }

                // A name alone is weak (D37): only one record whose title is the very same words is taken.
                var same = reply.Scenes.Where(s => Normalise(s.Title) == Normalise(title) && Normalise(title).Length > 0).DistinctBy(s => s.Id).ToList();
                if (same.Count == 1)
                {
                    scene = same[0];
                    signal = AdultMatchSignals.Title;
                }
            }

            if (scene is null)
            {
                continue;
            }

            var (cover, mime) = await _client.DownloadCoverAsync(credential.Address, scene.ImageUrl, ct).ConfigureAwait(false);
            return new AdultVideoInfo(
                server.Id, scene.Id, signal, scene.Title, scene.Details, scene.Date, scene.Code, scene.Studio,
                scene.Performers, scene.Tags, cover, mime);
        }

        return null;
    }

    /// <summary>Records what the server said about the key. Returns true when this server gave no usable answer.</summary>
    private async Task<bool> NoteAsync(StashBoxServer server, StashBoxReply reply, CancellationToken ct)
    {
        switch (reply.Status)
        {
            case "ok":
                if (server.Connection != StashBoxConnectionStates.Connected)
                {
                    await _settings.SetConnectionAsync(server.Id, StashBoxConnectionStates.Connected, ct).ConfigureAwait(false);
                }

                return false;
            case "not_connected":
                _logger.LogWarning("The Stash-box server '{Server}' did not accept its key; it is shown as not connected", server.Name);
                await _settings.SetConnectionAsync(server.Id, StashBoxConnectionStates.NotConnected, ct).ConfigureAwait(false);
                return true;
            default:
                return true;
        }
    }

    private static string Normalise(string? text) =>
        new((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
