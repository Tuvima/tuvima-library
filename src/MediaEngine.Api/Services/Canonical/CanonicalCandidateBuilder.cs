using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using CanonicalTargetPolicy = MediaEngine.Api.Endpoints.ItemCanonicalEndpoints.CanonicalTargetPolicy;

namespace MediaEngine.Api.Services.Canonical;

/// <summary>
/// The candidate-scoring / field-bag construction / identity-conflict-detection cluster
/// extracted from <c>ItemCanonicalEndpoints.cs</c> (Stage 5A wave 2, packet f2, Job 3).
///
/// <see cref="FindChildParentIdentityConflictAsync"/> and <see cref="ClearStaleIdsAsync"/>
/// need repository/data-service access and are constructor-injected instance methods (the
/// dependencies were previously threaded through as per-call parameters on free-standing
/// statics in <c>ItemCanonicalEndpoints.cs</c> — this only changes how they are supplied,
/// not the logic). The candidate/field-bag builders below have no repository dependency and
/// remain <c>static</c> so the canonical-search handler can call them directly.
///
/// <see cref="ItemCanonicalEndpoints.CanonicalTargetPolicy"/> and
/// <see cref="ItemCanonicalEndpoints.ResolveTargetPolicy"/> stay in
/// <c>ItemCanonicalEndpoints.cs</c> (used by several scope-routing helpers that also stay
/// there, and pinned by a test that source-scans that file for specific policy switch
/// entries). <see cref="ItemCanonicalEndpoints.IsContainerIdentityPolicy"/> and
/// <see cref="ItemCanonicalEndpoints.ResolveScopedTarget"/> were bumped from
/// <c>private</c> to <c>internal</c> so this class can call them.
/// </summary>
internal sealed class CanonicalCandidateBuilder(
    ICanonicalValueRepository canonicalRepo,
    IBridgeIdRepository bridgeIdRepo,
    IItemCanonicalRepository itemCanonicalData)
{
    public async Task<IReadOnlyList<string>> ClearStaleIdsAsync(
        Guid assetId,
        WorkLineage? lineage,
        CanonicalTargetPolicy policy,
        ItemCanonicalApplyRequestDto request,
        CancellationToken ct)
    {
        var retainedIdKeys = request.BridgeIds.Keys
            .Concat(request.QidFields.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groupIdKeys = policy.BridgeIdKeys.Concat(policy.QidFieldKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var toClear = groupIdKeys.Where(key => !retainedIdKeys.Contains(key)).ToList();

        if (toClear.Count > 0)
        {
            var artifacts = toClear
                .Select(key => new ItemCanonicalIdentityArtifact(
                    ItemCanonicalEndpoints.ResolveScopedTarget(assetId, lineage, key),
                    key))
                .ToList();
            await itemCanonicalData.DeleteIdentityArtifactsAsync(artifacts, ct);
        }

        return toClear;
    }

    public async Task<string?> FindChildParentIdentityConflictAsync(
        CanonicalTargetPolicy policy,
        WorkLineage lineage,
        IReadOnlyDictionary<string, string> selectedFields,
        IReadOnlyDictionary<string, string> selectedBridgeIds,
        CancellationToken ct)
    {
        if (lineage.TargetForSelfScope == lineage.TargetForParentScope)
        {
            return null;
        }

        string? parentField = policy.TargetFieldGroup switch
        {
            "show_episode" => MetadataFieldConstants.ShowName,
            "track" => MetadataFieldConstants.Album,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(parentField))
        {
            return null;
        }

        var parentCanonicals = await canonicalRepo.GetByEntityAsync(lineage.TargetForParentScope, ct);
        var currentParentName = parentCanonicals
            .FirstOrDefault(value => string.Equals(value.Key, parentField, StringComparison.OrdinalIgnoreCase))?.Value
            ?? parentCanonicals.FirstOrDefault(value =>
                string.Equals(value.Key, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))?.Value;

        if (selectedFields.TryGetValue(parentField, out var selectedParentName)
            && !string.IsNullOrWhiteSpace(currentParentName)
            && !IdentityTextEquals(currentParentName, selectedParentName))
        {
            var childLabel = policy.TargetFieldGroup == "show_episode" ? "episode" : "track";
            var parentLabel = policy.TargetFieldGroup == "show_episode" ? "series" : "album";
            return $"This {childLabel} match belongs to '{selectedParentName}', not the current {parentLabel} '{currentParentName}'. Move the {childLabel} from the Details panel before applying this identity match.";
        }

        if (policy.TargetFieldGroup == "show_episode"
            && selectedBridgeIds.TryGetValue(BridgeIdKeys.TmdbId, out var selectedShowId))
        {
            var existingShowId = await bridgeIdRepo.FindAsync(lineage.TargetForParentScope, BridgeIdKeys.TmdbId, ct);
            if (existingShowId is not null
                && !string.Equals(existingShowId.IdValue, selectedShowId, StringComparison.OrdinalIgnoreCase))
            {
                return "This episode match resolves to a different TMDB series. Move the episode from the Details panel before applying the episode identity.";
            }
        }

        return null;
    }

    private static bool IdentityTextEquals(string left, string right)
    {
        static string Normalize(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
    }

    public static string BuildCanonicalQuery(CanonicalTargetPolicy policy, IReadOnlyDictionary<string, string> draftFields, string? queryOverride)
    {
        if (!string.IsNullOrWhiteSpace(queryOverride))
        {
            // An explicit query is a deliberate candidate-discovery request, not
            // a refinement constrained to the current hierarchy.  In particular,
            // an episode editor must be able to find a valid episode under a
            // different show.  Draft title/ordinal fields remain available to the
            // search provider as non-binding file hints.
            return queryOverride.Trim();
        }

        return string.Join(" ", policy.QueryFieldKeys
            .Where(draftFields.ContainsKey)
            .Select(key => draftFields[key].Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static ItemCanonicalRetailCandidateDto BuildRetailCandidate(
        Domain.Models.RetailCandidate candidate,
        string mediaType,
        CanonicalTargetPolicy policy)
    {
        var allFields = BuildRetailFieldBag(candidate, mediaType, policy.TargetFieldGroup);
        var allowContainerTitleAliases = ItemCanonicalEndpoints.IsContainerIdentityPolicy(policy);
        var requiredFields = ExtractFields(allFields, policy.RequiredFieldKeys, allowContainerTitleAliases);
        var suggestedFields = ExtractFields(allFields, policy.SuggestedFieldKeys, allowContainerTitleAliases);
        var bridgeIds = ExtractFields(allFields, policy.BridgeIdKeys);
        // A provider search result can carry both a child ID and its parent
        // container ID. The identity written for this scope must use the ID
        // that represents the selected scope, rather than whichever raw result
        // ID happened to be returned first.
        var providerItemId = ResolveProviderItemId(candidate.ProviderName, policy.TargetFieldGroup, allFields)
            ?? candidate.ProviderItemId;
        var hasProviderName = !string.IsNullOrWhiteSpace(candidate.ProviderName);
        var hasProviderRegistration = Guid.TryParse(candidate.ProviderId, out _);
        var hasProviderItemId = !string.IsNullOrWhiteSpace(providerItemId);
        var isCompatible = ItemCanonicalEndpoints.IsRetailCandidateCompatible(
            policy,
            candidate.ProviderName,
            providerItemId ?? string.Empty,
            allFields,
            out var incompatibility);
        var isApplicable = hasProviderName && hasProviderRegistration && hasProviderItemId && isCompatible;
        var blockedReason = !hasProviderName
            ? "This result does not identify its retail provider."
            : !hasProviderRegistration
                ? "This result came from a provider that is no longer available."
                : !hasProviderItemId
                    ? "This result does not include a stable provider item ID."
                    : incompatibility;

        return new ItemCanonicalRetailCandidateDto
        {
            CandidateId = $"{candidate.ProviderName}:{providerItemId ?? candidate.Title}",
            ProviderId = candidate.ProviderId,
            ProviderName = candidate.ProviderName,
            ProviderItemId = providerItemId,
            Title = candidate.Title,
            Year = candidate.Year,
            Author = candidate.Author,
            Director = candidate.Director,
            Description = candidate.Description,
            CoverUrl = candidate.CoverUrl,
            Confidence = candidate.Confidence,
            CompositeScore = candidate.CompositeScore,
            MatchScores = candidate.MatchScores is null
                ? null
                : new MediaEngine.Contracts.Search.FieldMatchScoresDto
                {
                FieldScores = MediaEngine.Api.Services.Canonical.FieldScoreContractMapper.Map(candidate.MatchScores.FieldScores),
                    TitleScore = candidate.MatchScores.TitleScore,
                    AuthorScore = candidate.MatchScores.AuthorScore,
                    YearScore = candidate.MatchScores.YearScore,
                    FormatScore = candidate.MatchScores.FormatScore,
                    CompositeScore = candidate.MatchScores.CompositeScore,
                    TitleVerdict = (int)candidate.MatchScores.TitleVerdict,
                    AuthorVerdict = (int)candidate.MatchScores.AuthorVerdict,
                    YearVerdict = (int)candidate.MatchScores.YearVerdict,
                    FormatVerdict = (int)candidate.MatchScores.FormatVerdict,
                    CoverScore = candidate.MatchScores.CoverScore,
                    CoverVerdict = (int)candidate.MatchScores.CoverVerdict,
                },
            ExtraFields = candidate.ExtraFields?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            LinkState = "provider_only",
            LinkStatusLabel = "Linked to provider only",
            IsApplicable = isApplicable,
            BlockedReason = blockedReason,
            RequiredFields = requiredFields,
            SuggestedFields = suggestedFields,
            BridgeIds = bridgeIds,
            QidFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static string? ResolveProviderItemId(
        string providerName,
        string targetFieldGroup,
        IReadOnlyDictionary<string, string> fields)
    {
        var provider = providerName.Trim().ToLowerInvariant().Replace('-', '_');
        var isContainer = targetFieldGroup is "album" or "show" or "season" or "book_identity" or "movie_identity";
        var keys = provider switch
        {
            "musicbrainz" when isContainer => new[] { "musicbrainz_release_id", "musicbrainz_release_group_id", "musicbrainz_recording_id" },
            "musicbrainz" => new[] { "musicbrainz_recording_id", "musicbrainz_release_id", "musicbrainz_release_group_id" },
            "apple_api" or "apple_music" when isContainer => new[] { "apple_music_collection_id", "apple_music_id", "apple_books_id" },
            "apple_api" or "apple_music" => new[] { "apple_music_id", "apple_music_collection_id", "apple_books_id" },
            "tmdb" => new[] { "tmdb_id", "tmdb_movie_id", "tmdb_tv_id" },
            "tvdb" when targetFieldGroup == "show_episode" => new[] { "tvdb_episode_id", "tvdb_id" },
            "tvdb" when targetFieldGroup == "season" => new[] { "tvdb_season_id", "tvdb_id" },
            "tvdb" => new[] { "tvdb_id" },
            "comicvine" or "comic_vine" when targetFieldGroup == "series" => new[] { "comic_vine_volume_id", "comicvine_volume_id" },
            "comicvine" or "comic_vine" => new[] { "comicvine_id", "comic_vine_id" },
            "open_library" => new[] { "isbn_13", "isbn", "openlibrary_id" },
            _ => new[] { "provider_item_id" },
        };

        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return null;
    }

    public static ItemCanonicalLinkedCandidateDto BuildLinkedCandidate(
        Domain.Models.UniverseCandidate candidate,
        string mediaType,
        CanonicalTargetPolicy policy)
    {
        var allFields = BuildUniverseFieldBag(candidate, mediaType, policy.TargetFieldGroup);
        var requiredFields = ExtractFields(allFields, policy.RequiredFieldKeys);
        var suggestedFields = ExtractFields(allFields, policy.SuggestedFieldKeys);
        var qid = candidate.Qid?.Trim() ?? string.Empty;
        var qidFields = policy.QidFieldKeys.ToDictionary(key => key, _ => qid, StringComparer.OrdinalIgnoreCase);
        var hasValidQid = qid.Length > 1
            && qid[0] is 'Q' or 'q'
            && qid.AsSpan(1).ToArray().All(char.IsDigit);

        return new ItemCanonicalLinkedCandidateDto
        {
            CandidateId = $"wikidata:{qid}",
            Qid = qid,
            Label = candidate.Label,
            Description = candidate.Description,
            InstanceOf = candidate.InstanceOf,
            Year = candidate.Year,
            Author = candidate.Author,
            Director = candidate.Director,
            CoverUrl = null,
            WikipediaExtract = candidate.WikipediaExtract,
            ResolutionTier = candidate.ResolutionTier,
            // Canonical confidence belongs to the Stage 2 bridge resolver. Field scores
            // explain the comparison but must not replace the resolver's acceptance score.
            Confidence = candidate.Confidence,
            MatchScores = candidate.MatchScores is null
                ? null
                : new MediaEngine.Contracts.Search.FieldMatchScoresDto
                {
                FieldScores = MediaEngine.Api.Services.Canonical.FieldScoreContractMapper.Map(candidate.MatchScores.FieldScores),
                    TitleScore = candidate.MatchScores.TitleScore,
                    AuthorScore = candidate.MatchScores.AuthorScore,
                    YearScore = candidate.MatchScores.YearScore,
                    FormatScore = candidate.MatchScores.FormatScore,
                    CompositeScore = candidate.MatchScores.CompositeScore,
                    TitleVerdict = (int)candidate.MatchScores.TitleVerdict,
                    AuthorVerdict = (int)candidate.MatchScores.AuthorVerdict,
                    YearVerdict = (int)candidate.MatchScores.YearVerdict,
                    FormatVerdict = (int)candidate.MatchScores.FormatVerdict,
                    CoverScore = candidate.MatchScores.CoverScore,
                    CoverVerdict = (int)candidate.MatchScores.CoverVerdict,
                },
            BridgeIds = candidate.BridgeIds?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            MediaTypeMetadata = candidate.MediaTypeMetadata?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            LinkState = "linked",
            LinkStatusLabel = "Linked to Wikidata",
            IsApplicable = hasValidQid,
            BlockedReason = hasValidQid ? null : "This result does not include a valid Wikidata QID.",
            RequiredFields = requiredFields,
            SuggestedFields = suggestedFields,
            QidFields = qidFields,
        };
    }

    public static Dictionary<string, string> BuildRetailFieldBag(Domain.Models.RetailCandidate candidate, string mediaType, string targetFieldGroup)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(candidate.Title))
        {
            fields[MetadataFieldConstants.Title] = candidate.Title;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Author))
        {
            fields[MetadataFieldConstants.Author] = candidate.Author;
            fields.TryAdd(MetadataFieldConstants.Artist, candidate.Author);
        }
        if (!string.IsNullOrWhiteSpace(candidate.Director))
        {
            fields[MetadataFieldConstants.Director] = candidate.Director;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Description))
        {
            fields[MetadataFieldConstants.Description] = candidate.Description;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Year))
        {
            fields[MetadataFieldConstants.Year] = candidate.Year;
        }

        if (!string.IsNullOrWhiteSpace(candidate.CoverUrl))
        {
            fields[MetadataFieldConstants.CoverUrl] = candidate.CoverUrl;
        }

        if (!string.IsNullOrWhiteSpace(candidate.ProviderItemId))
        {
            fields["provider_item_id"] = candidate.ProviderItemId;
        }

        foreach (var (key, value) in candidate.ExtraFields ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                fields[key] = value;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.ProviderItemId))
        {
            var guessedBridgeId = GuessBridgeIdKey(candidate.ProviderName, mediaType, targetFieldGroup);
            if (!string.IsNullOrWhiteSpace(guessedBridgeId))
            {
                fields.TryAdd(guessedBridgeId, candidate.ProviderItemId!);
            }
        }

        return fields;
    }

    public static Dictionary<string, string> BuildUniverseFieldBag(Domain.Models.UniverseCandidate candidate, string mediaType, string targetFieldGroup)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(candidate.Label))
        {
            fields[MetadataFieldConstants.Title] = candidate.Label;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Author))
        {
            fields[MetadataFieldConstants.Author] = candidate.Author;
            fields.TryAdd(MetadataFieldConstants.Artist, candidate.Author);
        }
        if (!string.IsNullOrWhiteSpace(candidate.Director))
        {
            fields[MetadataFieldConstants.Director] = candidate.Director;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Description))
        {
            fields[MetadataFieldConstants.Description] = candidate.Description;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Year))
        {
            fields[MetadataFieldConstants.Year] = candidate.Year;
        }

        if (!string.IsNullOrWhiteSpace(candidate.CoverUrl))
        {
            fields[MetadataFieldConstants.CoverUrl] = candidate.CoverUrl;
        }

        foreach (var (key, value) in candidate.MediaTypeMetadata ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                fields[key] = value;
            }
        }

        switch (targetFieldGroup)
        {
            case "album":
                fields[MetadataFieldConstants.Album] = candidate.Label;
                break;
            case "artist":
                fields[MetadataFieldConstants.Artist] = candidate.Label;
                break;
            case "narrator":
                fields[MetadataFieldConstants.Narrator] = candidate.Label;
                break;
            case "series":
                fields[MetadataFieldConstants.Series] = candidate.Label;
                break;
            case "show":
                fields[MetadataFieldConstants.ShowName] = candidate.Label;
                break;
            case "show_episode":
                fields.TryAdd(MetadataFieldConstants.EpisodeTitle, candidate.Label);
                break;
            case "movie_identity":
            case "book_identity":
            case "audiobook_identity":
            case "issue":
                fields[MetadataFieldConstants.Title] = candidate.Label;
                break;
        }

        if (string.Equals(mediaType, MediaType.Music.ToString(), StringComparison.OrdinalIgnoreCase)
            && fields.TryGetValue(MetadataFieldConstants.Author, out var creator))
        {
            fields.TryAdd(MetadataFieldConstants.Artist, creator);
        }

        return fields;
    }

    public static Dictionary<string, string> ExtractFields(
        IReadOnlyDictionary<string, string> source,
        IEnumerable<string> keys,
        bool allowContainerTitleAliases = true)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (TryResolveFieldValue(source, key, allowContainerTitleAliases, out var value))
            {
                output[key] = value;
            }
        }

        return output;
    }

    public static bool TryResolveFieldValue(
        IReadOnlyDictionary<string, string> source,
        string key,
        bool allowContainerTitleAliases,
        out string value)
    {
        if (source.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var aliases = key switch
        {
            MetadataFieldConstants.Artist => [MetadataFieldConstants.Author],
            MetadataFieldConstants.Author => [MetadataFieldConstants.Artist],
            MetadataFieldConstants.Album when allowContainerTitleAliases => [MetadataFieldConstants.Title],
            MetadataFieldConstants.Series when allowContainerTitleAliases => [MetadataFieldConstants.Title],
            MetadataFieldConstants.ShowName when allowContainerTitleAliases => [MetadataFieldConstants.Title],
            _ => Array.Empty<string>(),
        };

        foreach (var alias in aliases)
        {
            if (source.TryGetValue(alias, out value!) && !string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public static string? GuessBridgeIdKey(string providerName, string mediaType, string targetFieldGroup)
    {
        var normalized = providerName?.Trim().ToLowerInvariant() ?? "";
        if (normalized.Contains("comic"))
        {
            return BridgeIdKeys.ComicVineId;
        }

        if (normalized.Contains("tmdb"))
        {
            return string.Equals(mediaType, MediaType.TV.ToString(), StringComparison.OrdinalIgnoreCase)
                   && string.Equals(targetFieldGroup, "show_episode", StringComparison.OrdinalIgnoreCase)
                ? BridgeIdKeys.TmdbEpisodeId
                : BridgeIdKeys.TmdbId;
        }

        if (normalized.Contains("imdb"))
        {
            return BridgeIdKeys.ImdbId;
        }

        if (normalized.Contains("audible"))
        {
            return BridgeIdKeys.AudibleId;
        }

        if (normalized.Contains("apple_books"))
        {
            return BridgeIdKeys.AppleBooksId;
        }

        if (normalized.Contains("open_library"))
        {
            return BridgeIdKeys.OpenLibraryId;
        }

        if (normalized.Contains("apple_music"))
        {
            return targetFieldGroup switch
            {
                "artist" => BridgeIdKeys.AppleArtistId,
                "album" => BridgeIdKeys.AppleMusicCollectionId,
                _ => BridgeIdKeys.AppleMusicId,
            };
        }

        return mediaType switch
        {
            "Music" when targetFieldGroup == "album" => BridgeIdKeys.AppleMusicCollectionId,
            "Music" when targetFieldGroup == "artist" => BridgeIdKeys.AppleArtistId,
            _ => null,
        };
    }
}
