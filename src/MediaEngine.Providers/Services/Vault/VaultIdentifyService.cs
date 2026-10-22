using System.Globalization;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Services.StashBox;

namespace MediaEngine.Providers.Services.Vault;

/// <summary>How an Identify step ended.</summary>
public static class VaultIdentifyStatus
{
    /// <summary>It worked.</summary>
    public const string Ok = "ok";

    /// <summary>The item is not a Vault item: nothing was asked.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The item's library has its lookup set to None: nothing was asked.</summary>
    public const string LookupOff = "lookup_off";

    /// <summary>No server with a working key: the lookup is shown as not connected and the library behaves as None.</summary>
    public const string NotConnected = "not_connected";

    /// <summary>The chosen record could not be read from the server.</summary>
    public const string NotFound = "not_found";
}

/// <summary>One record that might be this video.</summary>
public sealed record VaultCandidate(
    string ServerId,
    string RecordId,
    string? Title,
    string? Studio,
    DateOnly? Date,
    string? ProductCode,
    int? DurationSeconds,
    IReadOnlyList<string> Performers,
    bool LengthAgrees);

public sealed record VaultCandidateList(string Status, IReadOnlyList<VaultCandidate> Candidates, string? SearchedFor, string? ProductCode);

/// <summary>One row of the tick-per-field screen: what is there now beside what the record would put there.</summary>
/// <param name="PersonsOwn">The current value is the person's own edit. It is never replaced, so the row cannot be ticked.</param>
/// <param name="Changes">The proposed value differs from the current one.</param>
public sealed record VaultFieldChoice(string Field, string? Current, string? Proposed, bool Changes, bool PersonsOwn, bool Ticked);

public sealed record VaultIdentifyPreview(
    string Status,
    string ServerId,
    string RecordId,
    string? RecordTitle,
    string? ProductCode,
    int? DurationSeconds,
    bool LengthAgrees,
    IReadOnlyList<VaultFieldChoice> Fields);

public sealed record VaultIdentifyResult(string Status, IReadOnlyList<string> Applied, IReadOnlyList<string> Refused);

/// <summary>
/// Identify (Videos spec D38b): a manual lookup for a Vault video. It searches the servers by the title the person types, the name
/// pattern results or the product code; shows the current values beside the proposed ones with a tick per field; and applies exactly
/// the fields ticked. What is applied is read again from the server by id, never taken from what a browser sent. Every call goes
/// through <see cref="IProviderAudienceGate"/>, so only adult-only providers are asked and only about Vault items. "None of these"
/// needs no call: the video simply stays Unmatched.
/// </summary>
public sealed class VaultIdentifyService
{
    private readonly IProviderAudienceGate _gate;
    private readonly IVaultLibraryRepository _vault;
    private readonly IMediaAssetRepository _assets;
    private readonly ICanonicalValueRepository _canonical;
    private readonly IVaultMatchRepository _matches;
    private readonly IVaultNameRepository _names;
    private readonly IVaultArtworkRepository _artwork;
    private readonly IVaultActivityRepository _activity;
    private readonly IEnumerable<IAdultVideoSearch> _searches;
    private readonly IStashBoxSettings _settings;
    private readonly VaultFileNameReader _reader;
    private readonly VaultNamingConfigurationProvider _naming;
    private readonly VaultRecordApplier _applier;
    private readonly TimeProvider _time;

    public VaultIdentifyService(
        IProviderAudienceGate gate,
        IVaultLibraryRepository vault,
        IMediaAssetRepository assets,
        ICanonicalValueRepository canonical,
        IVaultMatchRepository matches,
        IVaultNameRepository names,
        IVaultArtworkRepository artwork,
        IVaultActivityRepository activity,
        IEnumerable<IAdultVideoSearch> searches,
        IStashBoxSettings settings,
        VaultFileNameReader reader,
        VaultNamingConfigurationProvider naming,
        VaultRecordApplier applier,
        TimeProvider time)
    {
        _gate = gate;
        _vault = vault;
        _assets = assets;
        _canonical = canonical;
        _matches = matches;
        _names = names;
        _artwork = artwork;
        _activity = activity;
        _searches = searches;
        _settings = settings;
        _reader = reader;
        _naming = naming;
        _applier = applier;
        _time = time;
    }

    public async Task<VaultCandidateList> SearchAsync(Guid assetId, string? text, CancellationToken ct = default)
    {
        if (await ContextAsync(assetId, ct).ConfigureAwait(false) is not { } context)
        {
            return new VaultCandidateList(VaultIdentifyStatus.Unavailable, [], null, null);
        }

        if (await NotReadyAsync(context, ct).ConfigureAwait(false) is { } notReady)
        {
            return new VaultCandidateList(notReady, [], null, null);
        }

        // With no text typed, the name patterns' title and the product code in the name are searched for.
        var typed = VaultFieldValidator.Title(text);
        var searchText = typed ?? (context.Parts.PatternMatched ? context.Parts.Title : null) ?? context.StoredTitle ?? context.Parts.Title;
        var code = typed is null ? context.Parts.ProductCode : null;
        var query = new AdultCandidateQuery(searchText, code, context.Library.LookupServerId);

        var found = new List<AdultVideoInfo>();
        foreach (var search in _searches)
        {
            if (await _gate.MayAskAboutItemAsync(search.ProviderName, assetId, ct).ConfigureAwait(false))
            {
                found.AddRange(await search.SearchAsync(query, ct).ConfigureAwait(false));
            }
        }

        var tolerance = _naming.Current.DurationTolerance;
        var candidates = found
            .Select(info => new VaultCandidate(
                info.ServerId,
                info.RecordId,
                VaultFieldValidator.Title(info.Title),
                VaultFieldValidator.Name(info.Studio),
                info.Date is { } date && VaultFieldValidator.IsSensibleDate(date) ? date : null,
                VaultFieldValidator.ProductCode(info.ProductCode),
                info.DurationSeconds,
                VaultFieldValidator.Names(info.Performers).Take(6).ToList(),
                VaultMatchRules.DurationAgrees(context.DurationSeconds, info.DurationSeconds, tolerance)))
            .OrderByDescending(c => c.LengthAgrees)
            .ToList();
        return new VaultCandidateList(VaultIdentifyStatus.Ok, candidates, searchText, code);
    }

    public async Task<VaultIdentifyPreview> PreviewAsync(Guid assetId, string serverId, string recordId, CancellationToken ct = default)
    {
        if (await ContextAsync(assetId, ct).ConfigureAwait(false) is not { } context)
        {
            return Failed(VaultIdentifyStatus.Unavailable, serverId, recordId);
        }

        if (await NotReadyAsync(context, ct).ConfigureAwait(false) is { } notReady)
        {
            return Failed(notReady, serverId, recordId);
        }

        if (await ReadRecordAsync(assetId, serverId, recordId, ct).ConfigureAwait(false) is not { } info)
        {
            return Failed(VaultIdentifyStatus.NotFound, serverId, recordId);
        }

        var existing = await _canonical.GetByEntityAsync(assetId, ct).ConfigureAwait(false);
        CanonicalValue? Current(string key) => existing.FirstOrDefault(v => v.Key == key && !string.IsNullOrWhiteSpace(v.Value));

        var title = VaultFieldValidator.Title(info.Title);
        var description = VaultFieldValidator.Description(info.Description);
        var date = info.Date is { } d && VaultFieldValidator.IsSensibleDate(d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        var studio = VaultFieldValidator.Name(info.Studio);
        var performers = VaultFieldValidator.Names(info.Performers);
        var tags = VaultFieldValidator.Names(info.Tags, VaultFieldValidator.MaxTagLength);
        var coverUsable = info.Cover is not null && VaultArtworkValidator.Validate(info.Cover, VaultImagePurpose.Cover).Image is not null;
        var hasCover = (await _artwork.GetForOwnerAsync(VaultArtworkOwnerKinds.Asset, assetId, ct).ConfigureAwait(false)).Any(a => a.Kind == VaultArtworkKinds.Cover);

        var currentPerformers = string.Join(", ", (await _names.GetPerformersOfAsync(assetId, ct).ConfigureAwait(false)).Select(p => p.Name));
        var currentStudio = string.Join(", ", (await _names.GetStudiosOfAsync(assetId, ct).ConfigureAwait(false)).Select(s => s.Name));
        var currentTags = string.Join(", ", (await _names.GetTagsOfAsync(assetId, ct).ConfigureAwait(false)).Select(t => t.Name));

        VaultFieldChoice Row(string field, string? current, string? proposed, bool personsOwn = false)
        {
            var changes = proposed is not null && !string.Equals(current ?? string.Empty, proposed, StringComparison.Ordinal);
            return new VaultFieldChoice(field, current, proposed, changes, personsOwn, changes && !personsOwn);
        }

        var titleValue = Current(MetadataFieldConstants.Title);
        var descriptionValue = Current(MetadataFieldConstants.Description);
        var dateValue = Current(MetadataFieldConstants.OriginalReleaseDate);
        var rows = new List<VaultFieldChoice>
        {
            Row(VaultFieldNames.Title, titleValue?.Value, title, VaultRecordApplier.IsPersonsOwn(titleValue)),
            Row(VaultFieldNames.Description, descriptionValue?.Value, description, VaultRecordApplier.IsPersonsOwn(descriptionValue)),
            Row(VaultFieldNames.ReleaseDate, dateValue?.Value, date, VaultRecordApplier.IsPersonsOwn(dateValue)),
            Row(VaultFieldNames.Cover, hasCover ? "Cover image" : "Still frame from the video", coverUsable ? "Cover image from the server" : null),
            Row(VaultFieldNames.Studio, NullIfEmpty(currentStudio), studio),
            Row(VaultFieldNames.Performers, NullIfEmpty(currentPerformers), performers.Count > 0 ? string.Join(", ", performers) : null),
            Row(VaultFieldNames.Tags, NullIfEmpty(currentTags), tags.Count > 0 ? string.Join(", ", tags) : null),
        };

        return new VaultIdentifyPreview(
            VaultIdentifyStatus.Ok, info.ServerId, info.RecordId, title, VaultFieldValidator.ProductCode(info.ProductCode), info.DurationSeconds,
            VaultMatchRules.DurationAgrees(context.DurationSeconds, info.DurationSeconds, _naming.Current.DurationTolerance), rows);
    }

    public async Task<VaultIdentifyResult> ApplyAsync(
        Guid assetId, string serverId, string recordId, IReadOnlyCollection<string> fields, CancellationToken ct = default)
    {
        if (await ContextAsync(assetId, ct).ConfigureAwait(false) is not { } context)
        {
            return new VaultIdentifyResult(VaultIdentifyStatus.Unavailable, [], []);
        }

        if (await NotReadyAsync(context, ct).ConfigureAwait(false) is { } notReady)
        {
            return new VaultIdentifyResult(notReady, [], []);
        }

        var chosen = fields.Where(VaultFieldNames.IsValid).ToHashSet(StringComparer.Ordinal);
        if (chosen.Count == 0 || await ReadRecordAsync(assetId, serverId, recordId, ct).ConfigureAwait(false) is not { } info)
        {
            return new VaultIdentifyResult(chosen.Count == 0 ? VaultIdentifyStatus.Ok : VaultIdentifyStatus.NotFound, [], []);
        }

        var result = await _applier.ApplyAsync(
            new VaultApplyRequest(assetId, context.LibraryId, info, VaultAppliedVia.Identify, chosen), ct).ConfigureAwait(false);
        await _activity.AddAsync(
            assetId, VaultActivityKinds.Identified, Path.GetFileName(context.FilePath),
            $"Identified by hand; {result.Applied.Count} fields applied.", _time.GetUtcNow(), ct).ConfigureAwait(false);
        return new VaultIdentifyResult(VaultIdentifyStatus.Ok, result.Applied, result.Refused);
    }

    private sealed record Context(
        string LibraryId,
        VaultLibrary Library,
        string FilePath,
        double? DurationSeconds,
        VaultNameParts Parts,
        string? StoredTitle);

    private async Task<Context?> ContextAsync(Guid assetId, CancellationToken ct)
    {
        var asset = await _assets.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset?.LibraryId is not { } libraryId
            || await _vault.GetAsync(libraryId, ct).ConfigureAwait(false) is not { } library
            || await _matches.GetFileFactsAsync(assetId, ct).ConfigureAwait(false) is not { } facts)
        {
            return null;
        }

        var existing = await _canonical.GetByEntityAsync(assetId, ct).ConfigureAwait(false);
        return new Context(
            libraryId,
            library,
            facts.FilePath,
            VaultFieldValidator.IsSensibleDuration(facts.DurationSeconds) ? facts.DurationSeconds : null,
            _reader.Read(facts.FilePath),
            existing.FirstOrDefault(v => v.Key == MetadataFieldConstants.Title)?.Value);
    }

    private async Task<AdultVideoInfo?> ReadRecordAsync(Guid assetId, string serverId, string recordId, CancellationToken ct)
    {
        foreach (var search in _searches)
        {
            if (await _gate.MayAskAboutItemAsync(search.ProviderName, assetId, ct).ConfigureAwait(false)
                && await search.GetAsync(serverId, recordId, ct).ConfigureAwait(false) is { } info)
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>Null when Identify can go ahead; otherwise why it cannot: the library does not look videos up, or no server has a working key.</summary>
    private async Task<string?> NotReadyAsync(Context context, CancellationToken ct)
    {
        if (context.Library.EffectiveLookupSource != VaultLookupSources.StashBox)
        {
            return VaultIdentifyStatus.LookupOff;
        }

        return await AnyServerConnectedAsync(ct).ConfigureAwait(false) ? null : VaultIdentifyStatus.NotConnected;
    }

    private async Task<bool> AnyServerConnectedAsync(CancellationToken ct) =>
        (await _settings.GetAsync(ct).ConfigureAwait(false)).Servers.Any(s => s.HasKey && s.Connection != StashBoxConnectionStates.NotConnected);

    private static VaultIdentifyPreview Failed(string status, string serverId, string recordId) =>
        new(status, serverId, recordId, null, null, null, false, []);

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
