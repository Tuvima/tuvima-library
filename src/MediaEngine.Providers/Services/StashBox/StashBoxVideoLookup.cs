using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services.Vault;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services.StashBox;

/// <summary>
/// The Official Stash-box lookup (the adult-only "stashbox" provider). It contacts a stash-box server and only ever answers
/// about Vault items: the caller asks <see cref="IProviderAudienceGate"/> first. It sends a title or product code as search text,
/// and file fingerprints only while the administrator's separate permission is on. Without a working key it finds nothing, which is
/// the same as the library's lookup being set to None. It only reads from a server; it never submits.
/// <para>
/// A record is returned only for a strong match (<see cref="VaultMatchRules"/>): an exact fingerprint, or a product code or title whose
/// record agrees with the file's length. A weak match, or signals that disagree, return nothing and the video stays Unmatched.
/// </para>
/// </summary>
public sealed class StashBoxVideoLookup : IAdultVideoLookup, IAdultVideoSearch
{
    private const int MaxCandidates = 20;

    private readonly IConfigurationLoader _configuration;
    private readonly IStashBoxSettings _settings;
    private readonly StashBoxClient _client;
    private readonly VaultNamingConfigurationProvider _naming;
    private readonly ILogger<StashBoxVideoLookup> _logger;

    public StashBoxVideoLookup(
        IConfigurationLoader configuration,
        IStashBoxSettings settings,
        StashBoxClient client,
        VaultNamingConfigurationProvider naming,
        ILogger<StashBoxVideoLookup> logger)
    {
        _configuration = configuration;
        _settings = settings;
        _client = client;
        _naming = naming;
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
        var code = StashBoxClient.Clean(query.ProductCode, 64);
        var useFingerprints = provider.FileFingerprint && query.Fingerprints.Count > 0;
        if (title is null && code is null && !useFingerprints)
        {
            return null;
        }

        var snapshot = await _settings.GetAsync(ct).ConfigureAwait(false);
        useFingerprints &= snapshot.FingerprintPermission;
        var exact = useFingerprints
            ? query.Fingerprints.Where(f => f.Algorithm is AdultFingerprintAlgorithms.Oshash or AdultFingerprintAlgorithms.Md5).ToList()
            : [];
        useFingerprints = exact.Count > 0;
        var tolerance = _naming.Current.DurationTolerance;

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

            var byFingerprint = new List<StashBoxScene>();
            var byCode = new List<StashBoxScene>();
            var byTitle = new List<StashBoxScene>();

            if (useFingerprints)
            {
                var reply = await _client.FindByFingerprintsAsync(credential.Address, credential.Key, exact, ct).ConfigureAwait(false);
                if (await NoteAsync(server, reply, ct).ConfigureAwait(false))
                {
                    continue;
                }

                byFingerprint.AddRange(reply.Scenes);
            }

            if (code is not null)
            {
                var reply = await _client.SearchByTermAsync(credential.Address, credential.Key, code, ct).ConfigureAwait(false);
                if (await NoteAsync(server, reply, ct).ConfigureAwait(false))
                {
                    continue;
                }

                byCode.AddRange(reply.Scenes);
            }

            var decision = VaultMatchRules.Decide(query, new VaultMatchEvidence(exact, byFingerprint, byCode, byTitle), tolerance);

            // A name is only asked about when nothing stronger decided.
            if (decision.Verdict is not (VaultMatchVerdict.Accepted or VaultMatchVerdict.Conflict) && title is not null)
            {
                var reply = await _client.SearchByTitleAsync(credential.Address, credential.Key, title, ct).ConfigureAwait(false);
                if (await NoteAsync(server, reply, ct).ConfigureAwait(false))
                {
                    continue;
                }

                byTitle.AddRange(reply.Scenes);
                decision = VaultMatchRules.Decide(query, new VaultMatchEvidence(exact, byFingerprint, byCode, byTitle), tolerance);
            }

            if (decision.Verdict == VaultMatchVerdict.Conflict)
            {
                // Signals that disagree stop the whole lookup: nothing is applied and no other server is asked (Videos spec D37).
                _logger.LogInformation("A Vault lookup found records that disagree; nothing was applied");
                return null;
            }

            if (decision.Verdict != VaultMatchVerdict.Accepted || decision.Scene is not { } scene)
            {
                continue;
            }

            var (cover, mime) = await _client.DownloadCoverAsync(credential.Address, scene.ImageUrl, ct).ConfigureAwait(false);
            return ToInfo(server.Id, scene, decision.Signal!, cover, mime);
        }

        return null;
    }

    public async Task<IReadOnlyList<AdultVideoInfo>> SearchAsync(AdultCandidateQuery query, CancellationToken ct = default)
    {
        var provider = _configuration.LoadProvider(StashBoxSettings.ProviderName);
        if (provider is null || !provider.Enabled || !provider.AdultOnly)
        {
            return [];
        }

        var text = StashBoxClient.Clean(query.Text, 300);
        var code = StashBoxClient.Clean(query.ProductCode, 64);
        if (text is null && code is null)
        {
            return [];
        }

        var snapshot = await _settings.GetAsync(ct).ConfigureAwait(false);
        var servers = snapshot.Servers
            .Where(s => s.HasKey && s.Connection != StashBoxConnectionStates.NotConnected)
            .OrderBy(s => string.Equals(s.Id, query.PreferredServerId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        var found = new List<AdultVideoInfo>();
        foreach (var server in servers)
        {
            if (await _settings.GetCredentialAsync(server.Id, ct).ConfigureAwait(false) is not { } credential)
            {
                continue;
            }

            foreach (var term in new[] { code, text }.Where(t => t is not null).Select(t => t!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var reply = await _client.SearchByTermAsync(credential.Address, credential.Key, term, ct).ConfigureAwait(false);
                if (await NoteAsync(server, reply, ct).ConfigureAwait(false))
                {
                    break;
                }

                foreach (var scene in reply.Scenes)
                {
                    if (!found.Any(f => f.ServerId == server.Id && f.RecordId == scene.Id))
                    {
                        found.Add(ToInfo(server.Id, scene, AdultMatchSignals.Identify, null, null));
                    }
                }
            }
        }

        return found.Take(MaxCandidates).ToList();
    }

    public async Task<AdultVideoInfo?> GetAsync(string serverId, string recordId, CancellationToken ct = default)
    {
        var provider = _configuration.LoadProvider(StashBoxSettings.ProviderName);
        if (provider is null || !provider.Enabled || !provider.AdultOnly)
        {
            return null;
        }

        var snapshot = await _settings.GetAsync(ct).ConfigureAwait(false);
        var server = snapshot.Servers.FirstOrDefault(s => s.Id == serverId && s.HasKey && s.Connection != StashBoxConnectionStates.NotConnected);
        if (server is null || await _settings.GetCredentialAsync(server.Id, ct).ConfigureAwait(false) is not { } credential)
        {
            return null;
        }

        var reply = await _client.FindSceneAsync(credential.Address, credential.Key, recordId, ct).ConfigureAwait(false);
        if (await NoteAsync(server, reply, ct).ConfigureAwait(false) || reply.Scenes.FirstOrDefault(s => s.Id == recordId) is not { } scene)
        {
            return null;
        }

        var (cover, mime) = await _client.DownloadCoverAsync(credential.Address, scene.ImageUrl, ct).ConfigureAwait(false);
        return ToInfo(server.Id, scene, AdultMatchSignals.Identify, cover, mime);
    }

    private static AdultVideoInfo ToInfo(string serverId, StashBoxScene scene, string signal, byte[]? cover, string? mime) =>
        new(serverId, scene.Id, signal, scene.Title, scene.Details, scene.Date, scene.Code, scene.Studio, scene.Performers, scene.Tags, cover, mime)
        {
            DurationSeconds = scene.DurationSeconds,
        };

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
                _logger.LogWarning("A Stash-box server did not accept its key; it is shown as not connected");
                await _settings.SetConnectionAsync(server.Id, StashBoxConnectionStates.NotConnected, ct).ConfigureAwait(false);
                return true;
            default:
                return true;
        }
    }
}
