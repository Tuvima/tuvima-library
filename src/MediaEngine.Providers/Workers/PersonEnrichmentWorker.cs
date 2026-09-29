using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using System.Text.Json.Nodes;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Workers;

/// <summary>
/// Extracts person references from claims/canonicals and runs enrichment.
/// Handles both QID-linked persons (from Wikidata claims) and standalone
/// name-only persons (reconciled via <see cref="IPersonReconciliationService"/>).
///
/// Extracted from <c>HydrationPipelineService</c> Stage 1 person enrichment
/// and Stage 2 actor-character mapping sections.
/// </summary>
public sealed class PersonEnrichmentWorker
{
    private static readonly SemaphoreSlim ProviderPersonCreationLock = new(1, 1);
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly ICanonicalValueArrayRepository? _canonicalArrayRepo;
    private readonly IRecursiveIdentityService _identity;
    private readonly IMetadataHarvestingService _harvesting;
    private readonly IPersonRepository _personRepo;
    private readonly IFictionalEntityRepository _fictionalEntityRepo;
    private readonly ICollectionRepository _collectionRepo;
    private readonly ReconciliationAdapter? _reconciliationAdapter;
    private readonly IPersonReconciliationService? _personReconciliation;
    private readonly PersonImageEnrichmentWorker? _personImages;
    private readonly ILogger<PersonEnrichmentWorker> _logger;
    private readonly IBridgeIdRepository? _bridgeIds;
    private readonly IConfigurationLoader? _configuration;
    private readonly TmdbRetailClient? _tmdbClient;
    private readonly TvdbRetailClient? _tvdbClient;

    public PersonEnrichmentWorker(
        IMetadataClaimRepository claimRepo,
        ICanonicalValueRepository canonicalRepo,
        IRecursiveIdentityService identity,
        IMetadataHarvestingService harvesting,
        IPersonRepository personRepo,
        IFictionalEntityRepository fictionalEntityRepo,
        ICollectionRepository collectionRepo,
        ILogger<PersonEnrichmentWorker> logger,
        ReconciliationAdapter? reconciliationAdapter = null,
        IPersonReconciliationService? personReconciliation = null,
        PersonImageEnrichmentWorker? personImages = null,
        ICanonicalValueArrayRepository? canonicalArrayRepo = null,
        IBridgeIdRepository? bridgeIds = null,
        IConfigurationLoader? configuration = null,
        TmdbRetailClient? tmdbClient = null,
        TvdbRetailClient? tvdbClient = null)
    {
        _claimRepo = claimRepo;
        _canonicalRepo = canonicalRepo;
        _identity = identity;
        _harvesting = harvesting;
        _personRepo = personRepo;
        _fictionalEntityRepo = fictionalEntityRepo;
        _collectionRepo = collectionRepo;
        _reconciliationAdapter = reconciliationAdapter;
        _logger = logger;
        _personReconciliation = personReconciliation;
        _personImages = personImages;
        _canonicalArrayRepo = canonicalArrayRepo;
        _bridgeIds = bridgeIds;
        _configuration = configuration;
        _tmdbClient = tmdbClient;
        _tvdbClient = tvdbClient;
    }

    /// <summary>
    /// Extracts person references from raw claims and canonicals, enriches
    /// QID-linked persons, and reconciles unlinked names via Wikidata search.
    /// </summary>
    public async Task EnrichFromClaimsAsync(Guid entityId, CancellationToken ct)
    {
        var claims = (await _claimRepo.GetByEntityAsync(entityId, ct)).ToList();
        var canonicals = (await _canonicalRepo.GetByEntityAsync(entityId, ct)).ToList();
        var canonicalArrays = new Dictionary<string, List<CanonicalArrayEntry>>(StringComparer.OrdinalIgnoreCase);
        await AddCanonicalArraysAsync(entityId, canonicalArrays, ct).ConfigureAwait(false);

        // Lineage-aware scoring stores TV/movie people on Work rows. TV cast is
        // commonly routed to the parent show row while enrichment is invoked
        // for the episode asset, so read the whole Work lineage before
        // extracting credits. Links still point at the owned file asset.
        IReadOnlyList<Guid> lineageWorkIds = await _collectionRepo.GetWorkLineageIdsByMediaAssetAsync(entityId, ct)
            .ConfigureAwait(false);
        if (lineageWorkIds.Count == 0)
        {
            var workId = await _collectionRepo.GetWorkIdByMediaAssetAsync(entityId, ct)
                .ConfigureAwait(false);
            if (workId.HasValue)
            {
                lineageWorkIds = new[] { workId.Value };
            }
        }

        foreach (var lineageWorkId in lineageWorkIds.Where(id => id != entityId).Distinct())
        {
            claims.AddRange(await _claimRepo.GetByEntityAsync(lineageWorkId, ct).ConfigureAwait(false));
            canonicals.AddRange(await _canonicalRepo.GetByEntityAsync(lineageWorkId, ct).ConfigureAwait(false));
            await AddCanonicalArraysAsync(lineageWorkId, canonicalArrays, ct).ConfigureAwait(false);
        }

        // Determine media type from canonicals
        var mediaTypeStr = canonicals
            .FirstOrDefault(c => string.Equals(c.Key, "media_type", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var mediaType = Enum.TryParse<MediaType>(mediaTypeStr, true, out var mt) ? mt : MediaType.Unknown;

        // Convert stored claims to ProviderClaim format
        var providerClaims = claims
            .Select(mc => new ProviderClaim(mc.ClaimKey, mc.ClaimValue, mc.Confidence))
            .ToList();
        var tmdbImageHints = BuildTmdbImageHints(providerClaims);
        var readOnlyCanonicalArrays = ToReadOnlyCanonicalArrays(canonicalArrays);

        _logger.LogDebug(
            "Person source data for entity {EntityId}: {ClaimCount} claim(s), {CanonicalCount} scalar(s), array keys [{ArrayKeys}]",
            entityId,
            claims.Count,
            canonicals.Count,
            string.Join(", ", readOnlyCanonicalArrays.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)));

        // Extract person refs — prefer raw claims (QID-first), fall back to canonicals
        var personRefs = PersonReferenceExtractor.FromRawClaims(providerClaims, mediaType)
            .Concat(PersonReferenceExtractor.FromCanonicalArrays(readOnlyCanonicalArrays, mediaType))
            .Where(reference => !string.IsNullOrWhiteSpace(reference.WikidataQid))
            .GroupBy(reference => $"{reference.WikidataQid}::{reference.Role}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        _logger.LogInformation(
            "Person extraction for entity {EntityId}: {Count} person ref(s)",
            entityId, personRefs.Count);

        if (personRefs.Count > 0)
        {
            var failures = new List<Exception>();

            try
            {
                var personRequests = await _identity.EnrichAsync(entityId, personRefs, ct);
                var imageEnrichedPeople = new HashSet<Guid>();

                foreach (var personReq in personRequests)
                {
                    try
                    {
                        await _harvesting.ProcessSynchronousAsync(personReq, ct);
                        await EnsurePersonHarvestCompletedAsync(personReq.EntityId, ct).ConfigureAwait(false);
                        await EnrichPersonImageAsync(
                            personReq.EntityId,
                            personReq.Hints.GetValueOrDefault("role"),
                            mediaType,
                            FindTmdbImageHint(tmdbImageHints, personReq.Hints.GetValueOrDefault("role"), personReq.Hints.GetValueOrDefault("name")),
                            ct);
                        imageEnrichedPeople.Add(personReq.EntityId);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex,
                            "Synchronous person enrichment failed for person {Id}",
                            personReq.EntityId);
                        failures.Add(ex);
                    }
                }

                if (_personImages is not null)
                {
                    foreach (var reference in personRefs)
                    {
                        var person = await _personRepo.FindByQidAsync(reference.WikidataQid!, ct)
                            .ConfigureAwait(false);
                        if (person is not null && imageEnrichedPeople.Add(person.Id))
                        {
                            await EnrichPersonImageAsync(
                                person.Id,
                                reference.Role,
                                mediaType,
                                FindTmdbImageHint(tmdbImageHints, reference.Role, reference.Name),
                                ct);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Person enrichment failed for entity {Id}", entityId);
                failures.Add(ex);
            }

            if (failures.Count > 0)
            {
                _logger.LogWarning(
                    "Person enrichment completed with {FailureCount} linked profile/detail failure(s) for entity {EntityId}; persisted person links remain usable and ingestion will continue",
                    failures.Count,
                    entityId);
            }
        }

        var unlinkedRefs = PersonReferenceExtractor.FromRawClaimsUnlinked(providerClaims, mediaType)
            .Concat(PersonReferenceExtractor.FromCanonicalArrays(readOnlyCanonicalArrays, mediaType)
                .Where(reference => string.IsNullOrWhiteSpace(reference.WikidataQid)))
            .GroupBy(reference => $"{reference.Role}::{reference.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var resolvedUnlinked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Standalone person reconciliation for unlinked names
        if (_personReconciliation is not null)
        {
            var titleHint = ResolvePersonWorkTitleHint(canonicals, mediaType) ?? "";

            _logger.LogDebug(
                "Standalone person reconciliation for entity {EntityId}: {Count} unlinked contributor(s)",
                entityId,
                unlinkedRefs.Count);

            foreach (var unlinked in unlinkedRefs)
            {
                try
                {
                    var searchResult = await _personReconciliation.SearchPersonAsync(
                        unlinked.Name, unlinked.Role, titleHint, ct);

                    if (searchResult is not null)
                    {
                        _logger.LogInformation(
                            "Person enrichment writeback: {OldName} -> {NewName} ({Qid}) (role: {Role})",
                            unlinked.Name, searchResult.Name, searchResult.WikidataQid, unlinked.Role);

                        var harvestRequest = await PersistReconciledPersonAsync(
                            entityId, unlinked.Name, unlinked.Role,
                            searchResult.WikidataQid, searchResult.Name, ct);
                        resolvedUnlinked.Add($"{unlinked.Role}::{unlinked.Name}");

                        if (harvestRequest is not null)
                        {
                            try
                            {
                                await _harvesting.ProcessSynchronousAsync(harvestRequest, ct);
                                await EnsurePersonHarvestCompletedAsync(harvestRequest.EntityId, ct).ConfigureAwait(false);
                                await EnrichPersonImageAsync(
                                    harvestRequest.EntityId,
                                    unlinked.Role,
                                    mediaType,
                                    FindTmdbImageHint(tmdbImageHints, unlinked.Role, unlinked.Name),
                                    ct);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogWarning(ex,
                                    "Synchronous person enrichment failed for reconciled person {Id}",
                                    harvestRequest.EntityId);
                                throw;
                            }
                        }
                        else
                        {
                            var existing = await _personRepo.FindByQidAsync(searchResult.WikidataQid, ct)
                                .ConfigureAwait(false);
                            if (existing is not null)
                            {
                                await EnrichPersonImageAsync(
                                    existing.Id,
                                    unlinked.Role,
                                    mediaType,
                                    FindTmdbImageHint(tmdbImageHints, unlinked.Role, unlinked.Name),
                                    ct);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Person reconciliation failed for '{Name}'", unlinked.Name);
                }
            }
        }

        // TV portraits and biographies follow the same provider as the show.
        // Wikidata-linked actors still need their TVDB person bridge recorded;
        // otherwise the generic QID image path would query TMDB for TV credits.
        if (mediaType == MediaType.TV && _tvdbClient?.IsConfigured() == true && _bridgeIds is not null)
        {
            foreach (var reference in personRefs.Where(reference =>
                         string.Equals(reference.Role, "Actor", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var person = await _personRepo.FindByQidAsync(reference.WikidataQid!, ct)
                        .ConfigureAwait(false);
                    if (person is not null)
                        await EnrichTvdbLinkedPersonAsync(person.Id, reference.Name, providerClaims, ct)
                            .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "TheTVDB person detail enrichment failed for {Name}", reference.Name);
                }
            }
        }

        // A provider person ID is a stable identity even when Wikidata has no
        // matching QID. Keep the credited name and direct provider profile.
        foreach (var unlinked in unlinkedRefs.Where(reference =>
                     !resolvedUnlinked.Contains($"{reference.Role}::{reference.Name}")))
        {
            try
            {
                await EnrichProviderIdentifiedPersonAsync(entityId, unlinked.Name,
                    unlinked.Role, mediaType, providerClaims, tmdbImageHints, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Provider person detail enrichment failed for {Name} on {EntityId}",
                    unlinked.Name, entityId);
            }
        }
    }

    private async Task EnrichProviderIdentifiedPersonAsync(
        Guid assetId, string name, string role, MediaType mediaType,
        IReadOnlyList<ProviderClaim> claims,
        IReadOnlyDictionary<string, TmdbPersonImageHint> tmdbHints,
        CancellationToken ct)
    {
        if (_bridgeIds is null) return;
        var (tvdbId, tvdbImage) = FindTvdbCastHint(claims, name);
        var tmdb = FindTmdbImageHint(tmdbHints, role, name);
        var useTvdb = !string.IsNullOrWhiteSpace(tvdbId) && _tvdbClient?.IsConfigured() == true;
        if (mediaType == MediaType.TV && !useTvdb) return;
        var idType = useTvdb ? BridgeIdKeys.TvdbPersonId : BridgeIdKeys.TmdbPersonId;
        var providerId = useTvdb ? tvdbId : tmdb?.PersonId?.ToString();
        if (string.IsNullOrWhiteSpace(providerId)) return;

        Person? person = null;
        await ProviderPersonCreationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var linked = await _bridgeIds.FindByValueAsync(idType, providerId, ct).ConfigureAwait(false);
            foreach (var entry in linked)
            {
                person = await _personRepo.FindByIdAsync(entry.EntityId, ct).ConfigureAwait(false);
                if (person is not null) break;
            }
            if (person is null)
            {
                person = await _personRepo.CreateAsync(new Person { Name = name, Roles = [role] }, ct)
                    .ConfigureAwait(false);
                await _bridgeIds.UpsertAsync(new BridgeIdEntry
                {
                    EntityId = person.Id, IdType = idType, IdValue = providerId,
                    ProviderId = useTvdb ? "tvdb" : "tmdb", CreatedAt = DateTimeOffset.UtcNow,
                }, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ProviderPersonCreationLock.Release();
        }
        await _personRepo.AddRoleAsync(person.Id, role, ct).ConfigureAwait(false);
        await _personRepo.LinkToMediaAssetAsync(assetId, person.Id, role, ct).ConfigureAwait(false);

        JsonNode? profile = null;
        string? headshot = null;
        string? biography = null;
        if (useTvdb)
        {
            profile = await _tvdbClient!.GetPersonAsync(providerId, ct).ConfigureAwait(false);
            headshot = profile?["image"]?.ToString() ?? tvdbImage;
            if (profile?["biographies"] is JsonArray biographies)
                biography = biographies.FirstOrDefault(node =>
                    string.Equals(node?["language"]?.ToString(), "eng", StringComparison.OrdinalIgnoreCase))?
                    ["biography"]?.ToString()
                    ?? biographies.FirstOrDefault()?["biography"]?.ToString();
        }
        else if (_tmdbClient is not null && _configuration is not null && tmdb?.PersonId is { } tmdbId)
        {
            var key = _configuration.LoadProvider("tmdb")?.HttpClient?.ApiKey;
            if (!string.IsNullOrWhiteSpace(key))
            {
                profile = await _tmdbClient.FetchPersonDetailsAsync(tmdbId, key, ct).ConfigureAwait(false);
                biography = profile?["biography"]?.ToString();
                var path = profile?["profile_path"]?.ToString();
                headshot = !string.IsNullOrWhiteSpace(path)
                    ? "https://image.tmdb.org/t/p/original" + path : tmdb.ProfileUrl;
            }
        }
        if (profile is not null)
        {
            await _personRepo.UpdateEnrichmentAsync(person.Id, null, headshot,
                biography, name: null, ct).ConfigureAwait(false);
            if (!useTvdb)
                await _personRepo.UpdateBiographicalFieldsAsync(person.Id,
                    profile["birthday"]?.ToString(), profile["deathday"]?.ToString(),
                    profile["place_of_birth"]?.ToString(), null, null, false,
                    ct: ct).ConfigureAwait(false);
        }
        if (_personImages is not null)
        {
            if (useTvdb && !string.IsNullOrWhiteSpace(headshot))
                await _personImages.EnrichProviderUrlAsync(person.Id, headshot, "tvdb", ct).ConfigureAwait(false);
            else if (mediaType != MediaType.TV && tmdb?.PersonId is { } tmdbPersonId)
                await _personImages.EnrichAsync(person.Id, role, mediaType, ct,
                    tmdbPersonId, headshot ?? tmdb.ProfileUrl).ConfigureAwait(false);
        }
    }

    private static (string? Id, string? Image) FindTvdbCastHint(
        IReadOnlyList<ProviderClaim> claims, string name)
    {
        string? compositeId = null;
        foreach (var composite in claims.Where(claim =>
                     claim.Key == "cast_member_tvdb_identity"))
        {
            var separator = composite.Value.IndexOf("::", StringComparison.Ordinal);
            if (separator < 1 || !string.Equals(composite.Value[(separator + 2)..], name,
                    StringComparison.OrdinalIgnoreCase)) continue;
            compositeId = composite.Value[..separator];
            break;
        }
        for (var index = 0; index < claims.Count; index++)
        {
            if (!string.Equals(claims[index].Key, MetadataFieldConstants.CastMember,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(claims[index].Value, name, StringComparison.OrdinalIgnoreCase)) continue;
            string? id = null;
            string? image = null;
            for (var next = index + 1; next < claims.Count; next++)
            {
                if (string.Equals(claims[next].Key, MetadataFieldConstants.CastMember,
                        StringComparison.OrdinalIgnoreCase)) break;
                if (claims[next].Key == "cast_member_tvdb_id") id ??= claims[next].Value;
                if (claims[next].Key == "cast_member_profile_url") image ??= claims[next].Value;
            }
            if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(compositeId))
                return (compositeId ?? id, image);
        }
        return (compositeId, null);
    }

    private async Task EnrichTvdbLinkedPersonAsync(Guid personId, string name,
        IReadOnlyList<ProviderClaim> claims, CancellationToken ct)
    {
        var (tvdbId, imageHint) = FindTvdbCastHint(claims, name);
        if (string.IsNullOrWhiteSpace(tvdbId) || _bridgeIds is null || _tvdbClient is null) return;
        await _bridgeIds.UpsertAsync(new BridgeIdEntry
        {
            EntityId = personId, IdType = BridgeIdKeys.TvdbPersonId,
            IdValue = tvdbId, ProviderId = "tvdb", CreatedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
        var profile = await _tvdbClient.GetPersonAsync(tvdbId, ct).ConfigureAwait(false);
        var headshot = profile?["image"]?.ToString() ?? imageHint;
        var biography = profile?["biographies"] is JsonArray biographies
            ? biographies.FirstOrDefault(node => string.Equals(node?["language"]?.ToString(), "eng",
                StringComparison.OrdinalIgnoreCase))?["biography"]?.ToString()
                ?? biographies.FirstOrDefault()?["biography"]?.ToString()
            : null;
        if (profile is not null)
            await _personRepo.UpdateEnrichmentAsync(personId, null, headshot, biography, name: null, ct)
                .ConfigureAwait(false);
        if (_personImages is not null && !string.IsNullOrWhiteSpace(headshot))
            await _personImages.EnrichProviderUrlAsync(personId, headshot, "tvdb", ct).ConfigureAwait(false);
    }

    private async Task AddCanonicalArraysAsync(
        Guid entityId,
        Dictionary<string, List<CanonicalArrayEntry>> target,
        CancellationToken ct)
    {
        if (_canonicalArrayRepo is null)
        {
            return;
        }

        var arrays = await _canonicalArrayRepo.GetAllByEntityAsync(entityId, ct).ConfigureAwait(false);
        foreach (var (key, values) in arrays)
        {
            if (!target.TryGetValue(key, out var list))
            {
                list = [];
                target[key] = list;
            }

            list.AddRange(values);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>> ToReadOnlyCanonicalArrays(
        IReadOnlyDictionary<string, List<CanonicalArrayEntry>> arrays)
    {
        var result = new Dictionary<string, IReadOnlyList<CanonicalArrayEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in arrays)
        {
            result[key] = values;
        }

        return result;
    }

    private async Task EnsurePersonHarvestCompletedAsync(Guid personId, CancellationToken ct)
    {
        var person = await _personRepo.FindByIdAsync(personId, ct).ConfigureAwait(false);
        if (person?.EnrichedAt is null)
        {
            throw new InvalidOperationException(
                $"Person {personId} was queued for enrichment but did not complete.");
        }
    }

    private async Task EnrichPersonImageAsync(
        Guid personId,
        string? role,
        MediaType mediaType,
        TmdbPersonImageHint? tmdbImageHint,
        CancellationToken ct)
    {
        if (_personImages is null || mediaType == MediaType.TV)
        {
            return;
        }

        try
        {
            await _personImages.EnrichAsync(
                personId,
                role,
                mediaType,
                ct,
                tmdbImageHint?.PersonId,
                tmdbImageHint?.ProfileUrl).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Person image enrichment failed for person {PersonId}", personId);
            throw;
        }
    }

    internal static IReadOnlyDictionary<string, TmdbPersonImageHint> BuildTmdbImageHints(IReadOnlyList<ProviderClaim> claims)
    {
        var hints = new Dictionary<string, TmdbPersonImageHint>(StringComparer.OrdinalIgnoreCase);

        AddTmdbImageHints(hints, claims, "Actor", MetadataFieldConstants.CastMember, "cast_member_tmdb_id", "cast_member_profile_url");
        AddTmdbImageHints(hints, claims, "Director", "director", "director_tmdb_id", "director_profile_url");
        AddTmdbImageHints(hints, claims, "Screenwriter", "screenwriter", "screenwriter_tmdb_id", "screenwriter_profile_url");
        AddTmdbImageHints(hints, claims, "Composer", "composer", "composer_tmdb_id", "composer_profile_url");
        AddTmdbImageHints(hints, claims, "Producer", "producer", "producer_tmdb_id", "producer_profile_url");

        foreach (var composite in claims.Where(claim => claim.Key == "cast_member_tmdb_identity"))
        {
            var separator = composite.Value.IndexOf("::", StringComparison.Ordinal);
            if (separator < 1 || !int.TryParse(composite.Value[..separator], out var id)) continue;
            var name = composite.Value[(separator + 2)..];
            var key = $"Actor::{RetailHints.NormalizePersonNameKey(name)}";
            hints[key] = new TmdbPersonImageHint(id, hints.GetValueOrDefault(key)?.ProfileUrl);
        }

        return hints;
    }

    private static void AddTmdbImageHints(
        Dictionary<string, TmdbPersonImageHint> hints,
        IReadOnlyList<ProviderClaim> claims,
        string role,
        string nameKey,
        string idKey,
        string profileUrlKey)
    {
        for (var i = 0; i < claims.Count; i++)
        {
            var nameClaim = claims[i];
            if (!string.Equals(nameClaim.Key, nameKey, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(nameClaim.Value))
            {
                continue;
            }

            var name = nameClaim.Value;
            int? personId = null;
            string? profileUrl = null;

            // TMDB contributor claims are emitted as an atomic run: name, ID,
            // profile URL. Scan only until the next contributor of the same role
            // so a missing ID or image cannot shift every later person's data.
            for (var companionIndex = i + 1; companionIndex < claims.Count; companionIndex++)
            {
                var companion = claims[companionIndex];
                if (string.Equals(companion.Key, nameKey, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (!personId.HasValue
                    && string.Equals(companion.Key, idKey, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(companion.Value, out var parsedId))
                {
                    personId = parsedId;
                }
                else if (profileUrl is null
                         && string.Equals(companion.Key, profileUrlKey, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(companion.Value))
                {
                    profileUrl = companion.Value;
                }
            }

            if (!personId.HasValue && string.IsNullOrWhiteSpace(profileUrl))
            {
                continue;
            }

            hints[$"{role}::{RetailHints.NormalizePersonNameKey(name)}"] = new TmdbPersonImageHint(personId, profileUrl);
        }
    }

    private static TmdbPersonImageHint? FindTmdbImageHint(
        IReadOnlyDictionary<string, TmdbPersonImageHint> hints,
        string? role,
        string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || hints.Count == 0)
        {
            return null;
        }

        var normalizedName = RetailHints.NormalizePersonNameKey(name);
        if (!string.IsNullOrWhiteSpace(role)
            && hints.TryGetValue($"{role}::{normalizedName}", out var exact))
        {
            return exact;
        }

        return hints.TryGetValue($"Actor::{normalizedName}", out var actorHint)
            ? actorHint
            : null;
    }

    internal static string? ResolvePersonWorkTitleHint(
        IReadOnlyList<CanonicalValue> canonicals,
        MediaType mediaType)
    {
        string? Value(string key) => canonicals
            .FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return mediaType switch
        {
            MediaType.TV => StringHelpers.FirstNonBlank(
                Value(MetadataFieldConstants.ShowName),
                Value(MetadataFieldConstants.Series),
                Value(MetadataFieldConstants.Title))?.Trim(),
            MediaType.Music => StringHelpers.FirstNonBlank(
                Value(MetadataFieldConstants.Album),
                Value(MetadataFieldConstants.Title))?.Trim(),
            MediaType.Audiobooks => StringHelpers.FirstNonBlank(
                Value("book_title"),
                Value(MetadataFieldConstants.Album),
                Value(MetadataFieldConstants.Series),
                Value(MetadataFieldConstants.Title))?.Trim(),
            _ => StringHelpers.FirstNonBlank(Value(MetadataFieldConstants.Title), Value(MetadataFieldConstants.Series))?.Trim(),
        };
    }

    internal sealed record TmdbPersonImageHint(int? PersonId, string? ProfileUrl);

    /// <summary>
    /// Creates or locates a Person record for a name-only person reference that was
    /// successfully reconciled to a Wikidata QID by <see cref="PersonReconciliationService"/>,
    /// writes back the resolved QID + canonical name, and writes a
    /// <c>person_media_links</c> row so the detail drawer shows library presence.
    ///
    /// Idempotent: uses INSERT OR IGNORE semantics via <see cref="IPersonRepository.LinkToMediaAssetAsync"/>.
    /// </summary>
    private async Task<HarvestRequest?> PersistReconciledPersonAsync(
        Guid mediaAssetId,
        string rawName,
        string role,
        string resolvedQid,
        string resolvedName,
        CancellationToken ct)
    {
        var existing = await _personRepo.FindByQidAsync(resolvedQid, ct).ConfigureAwait(false);

        Guid personId;
        bool needsHarvest;
        if (existing is not null)
        {
            personId = existing.Id;
            needsHarvest = existing.EnrichedAt is null;
            if (!string.Equals(existing.Name, resolvedName, StringComparison.OrdinalIgnoreCase))
            {
                await _personRepo.UpdateEnrichmentAsync(existing.Id, resolvedQid,
                    headshotUrl: null, biography: null, name: resolvedName, ct).ConfigureAwait(false);
            }

            await _personRepo.AddRoleAsync(existing.Id, role, ct).ConfigureAwait(false);
        }
        else
        {
            var newPerson = await _personRepo.CreateAsync(new Person
            {
                Name = resolvedName,
                Roles = [role],
                WikidataQid = resolvedQid,
            }, ct).ConfigureAwait(false);

            personId = newPerson.Id;
            needsHarvest = true;

            _logger.LogDebug(
                "Created person record for reconciled '{RawName}' -> '{ResolvedName}' ({Qid}), id={Id}",
                rawName, resolvedName, resolvedQid, personId);
        }

        try
        {
            await _personRepo.LinkToMediaAssetAsync(mediaAssetId, personId, role, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Persisted reconciled person '{Name}' ({Qid}) linked to asset {AssetId} as {Role}",
                resolvedName, resolvedQid, mediaAssetId, role);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "Could not link reconciled person '{Name}' ({Qid}) to asset {AssetId}; continuing enrichment",
                resolvedName, resolvedQid, mediaAssetId);
        }

        if (!needsHarvest)
        {
            return null;
        }

        return new HarvestRequest
        {
            EntityId = personId,
            EntityType = EntityType.Person,
            MediaType = MediaType.Unknown,
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = resolvedName,
                ["role"] = role,
                [BridgeIdKeys.WikidataQid] = resolvedQid,
            },
        };
    }

    /// <summary>
    /// Fetches P161 (cast member) claims with P453 (character role) qualifiers from Wikidata
    /// and links actors to fictional entities already known for the same work.
    /// </summary>
    public async Task EnrichActorCharacterMappingsAsync(
        Guid entityId, string workQid, CancellationToken ct)
    {
        var candidateEntityIds = await ResolveActorCharacterArrayEntityIdsAsync(entityId, ct).ConfigureAwait(false);
        var canonicals = await LoadActorCharacterCanonicalsAsync(candidateEntityIds, ct).ConfigureAwait(false);
        var hasArrayEvidence = await HasActorCharacterArrayEvidenceAsync(candidateEntityIds, ct).ConfigureAwait(false);
        var mediaType = canonicals
            .FirstOrDefault(c => string.Equals(c.Key, "media_type", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!IsVideoMediaType(mediaType) && !hasArrayEvidence)
        {
            return;
        }

        if (_reconciliationAdapter is null)
        {
            _logger.LogDebug(
                "Actor-character mapping skipped for entity {EntityId} ({Qid}) because ReconciliationAdapter is unavailable; independent cast and character arrays are not pairing evidence",
                entityId,
                workQid);
            return;
        }

        var extension = await _reconciliationAdapter.ExtendAsync([workQid], ["P161"], ct).ConfigureAwait(false);
        if (!extension.TryGetValue(workQid, out var properties)
            || !properties.TryGetValue("P161", out var castClaims)
            || castClaims.Count == 0)
        {
            _logger.LogDebug(
                "Actor-character mapping found no qualified live cast-member claims for entity {EntityId} ({Qid}); independent cast and character arrays are not pairing evidence",
                entityId,
                workQid);
            return;
        }

        var linkCount = 0;
        foreach (var castClaim in castClaims)
        {
            var performerQid = castClaim.Value?.EntityId ?? castClaim.Value?.RawValue;
            if (string.IsNullOrWhiteSpace(performerQid))
            {
                continue;
            }

            var person = await _personRepo.FindByQidAsync(performerQid, ct).ConfigureAwait(false);
            if (person is null)
            {
                continue;
            }

            if (!castClaim.Qualifiers.TryGetValue("P453", out var characterValues) || characterValues.Count == 0)
            {
                continue;
            }

            foreach (var characterValue in characterValues)
            {
                if (characterValue.Kind != WikidataValueKind.EntityId || string.IsNullOrWhiteSpace(characterValue.EntityId))
                {
                    continue;
                }

                var fictionalEntity = await _fictionalEntityRepo.FindByQidAsync(characterValue.EntityId, ct).ConfigureAwait(false);
                if (fictionalEntity is null)
                {
                    fictionalEntity = new FictionalEntity
                    {
                        Id = Guid.NewGuid(),
                        WikidataQid = characterValue.EntityId,
                        Label = ResolveCharacterLabel(characterValue),
                        EntitySubType = FictionalEntityType.Character,
                        FictionalUniverseQid = ResolveCanonicalQid(canonicals, "fictional_universe_qid")
                            ?? ResolveCanonicalQid(canonicals, "franchise_qid")
                            ?? ResolveCanonicalQid(canonicals, "series_qid")
                            ?? workQid,
                        FictionalUniverseLabel = ResolveCanonicalLabel(canonicals, "fictional_universe")
                            ?? ResolveCanonicalLabel(canonicals, "franchise")
                            ?? ResolveCanonicalLabel(canonicals, "series"),
                        CreatedAt = DateTimeOffset.UtcNow,
                    };

                    await _fictionalEntityRepo.CreateAsync(fictionalEntity, ct).ConfigureAwait(false);
                }

                await _fictionalEntityRepo.LinkToWorkAsync(new FictionalEntityWorkLink(
                    fictionalEntity.Id,
                    workQid,
                    null,
                    "portrayed_in"), ct)
                    .ConfigureAwait(false);

                await _personRepo.LinkToCharacterAsync(person.Id, fictionalEntity.Id, workQid, ct).ConfigureAwait(false);
                await EnqueueCharacterHarvestIfNeededAsync(fictionalEntity, ct).ConfigureAwait(false);
                linkCount++;
            }
        }

        _logger.LogInformation(
            "Actor-character mapping created {Count} link(s) for entity {EntityId} ({Qid})",
            linkCount,
            entityId,
            workQid);
    }

    private async Task<IReadOnlyList<CanonicalValue>> LoadActorCharacterCanonicalsAsync(
        IReadOnlyList<Guid> entityIds,
        CancellationToken ct)
    {
        var canonicals = new List<CanonicalValue>();
        foreach (var candidateEntityId in entityIds)
        {
            canonicals.AddRange(await _canonicalRepo.GetByEntityAsync(candidateEntityId, ct).ConfigureAwait(false));
        }

        return canonicals;
    }

    private async Task<bool> HasActorCharacterArrayEvidenceAsync(
        IReadOnlyList<Guid> entityIds,
        CancellationToken ct)
    {
        if (_canonicalArrayRepo is null)
        {
            return false;
        }

        foreach (var candidateEntityId in entityIds)
        {
            var arrays = await _canonicalArrayRepo.GetAllByEntityAsync(candidateEntityId, ct).ConfigureAwait(false);
            if (arrays.TryGetValue(MetadataFieldConstants.CastMember, out var castMembers)
                && arrays.TryGetValue(MetadataFieldConstants.Characters, out var characters)
                && castMembers.Any(entry => RetailHints.NormalizeQid(entry.ValueQid) is not null)
                && characters.Any(entry => RetailHints.NormalizeQid(entry.ValueQid) is not null))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<Guid>> ResolveActorCharacterArrayEntityIdsAsync(
        Guid entityId,
        CancellationToken ct)
    {
        var entityIds = new List<Guid> { entityId };

        var workId = await _collectionRepo.GetWorkIdByMediaAssetAsync(entityId, ct).ConfigureAwait(false);
        if (workId.HasValue && !entityIds.Contains(workId.Value))
        {
            entityIds.Add(workId.Value);
        }

        var lineageWorkIds = await _collectionRepo.GetWorkLineageIdsByMediaAssetAsync(entityId, ct).ConfigureAwait(false);
        foreach (var lineageWorkId in lineageWorkIds)
        {
            if (!entityIds.Contains(lineageWorkId))
            {
                entityIds.Add(lineageWorkId);
            }
        }

        return entityIds;
    }

    private static string ResolveCharacterLabel(WikidataValue value)
        => StringHelpers.FirstNonBlank(value.EntityLabel, value.RawValue, value.EntityId)?.Trim() ?? "Character";

    private async Task EnqueueCharacterHarvestIfNeededAsync(FictionalEntity entity, CancellationToken ct)
    {
        if (entity.EnrichedAt is not null || string.IsNullOrWhiteSpace(entity.WikidataQid))
        {
            return;
        }

        await _harvesting.EnqueueAsync(new HarvestRequest
        {
            EntityId = entity.Id,
            EntityType = EntityType.Character,
            MediaType = MediaType.Unknown,
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["wikidata_qid"] = entity.WikidataQid,
                ["label"] = entity.Label,
                ["entity_sub_type"] = entity.EntitySubType,
                ["universe_qid"] = entity.FictionalUniverseQid ?? string.Empty,
            },
        }, ct).ConfigureAwait(false);
    }

    private static bool IsVideoMediaType(string? mediaType)
        => mediaType is not null
           && (mediaType.Equals(nameof(MediaType.Movies), StringComparison.OrdinalIgnoreCase)
               || mediaType.Equals("Movie", StringComparison.OrdinalIgnoreCase)
               || mediaType.Equals(nameof(MediaType.TV), StringComparison.OrdinalIgnoreCase)
               || mediaType.Equals("TvShow", StringComparison.OrdinalIgnoreCase)
               || mediaType.Equals("TvEpisode", StringComparison.OrdinalIgnoreCase)
               || mediaType.Equals("Television", StringComparison.OrdinalIgnoreCase));

    private static string? ResolveCanonicalQid(
        IReadOnlyList<CanonicalValue> canonicals,
        string key)
    {
        var raw = canonicals.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var first = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        var qid = first.Split("::", 2)[0].Trim();
        return qid.StartsWith('Q') ? qid : null;
    }

    private static string? ResolveCanonicalLabel(
        IReadOnlyList<CanonicalValue> canonicals,
        string key)
    {
        var raw = canonicals.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var first = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        var segments = first.Split("::", 2);
        return segments.Length == 2 && !string.IsNullOrWhiteSpace(segments[1])
            ? segments[1].Trim()
            : first;
    }
}
