using System.Globalization;
using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Search;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Search endpoints for Universe (Wikidata) and Retail provider searches.
/// Used by the LibraryItem's MediaSearchPanel to find matches for items.
/// </summary>
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/search")
                       .WithTags("Search");

        // ── POST /search/universe ────────────────────────────────────────────
        group.MapPost("/universe", async (
            SearchUniverseRequest request,
            ISearchService searchService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return ApiErrors.BadRequest("Query is required.");
            }

            var result = await searchService.SearchUniverseAsync(request, ct);
            return Results.Ok(result);
        })
        .WithName("SearchUniverse")
        .WithSummary("Search Wikidata for identity candidates, enriched with cover art from retail providers.")
        .Produces<SearchUniverseResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch);

        group.MapPost("/retail/detail", async (
            RetailCandidateDetailRequestDto request,
            MusicBrainzReleaseClient musicBrainz,
            AppleRetailClient apple,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProviderName))
            {
                return ApiErrors.BadRequest("Provider name is required.");
            }

            var provider = request.ProviderName.Trim().ToLowerInvariant().Replace('-', '_');
            if (provider == "musicbrainz")
            {
                var releaseId = FirstValue(request.ExtraFields, "musicbrainz_release_id")
                    ?? request.ProviderItemId;
                var release = string.IsNullOrWhiteSpace(releaseId)
                    ? null
                    : await musicBrainz.FetchReleaseAsync(releaseId, ct);
                return Results.Ok(BuildMusicBrainzDetail(release));
            }

            if (provider is "apple_api" or "apple_music")
            {
                var collectionId = FirstValue(request.ExtraFields, "apple_music_collection_id", "collection_id");
                if (string.IsNullOrWhiteSpace(collectionId))
                {
                    return Results.Ok(UnavailableDetail(request.ProviderName, "Apple did not supply an album collection identifier for this result."));
                }

                var tracks = await apple.FetchAlbumTracksAsync(collectionId, "us", "en", ct);
                var detail = new RetailCandidateDetailDto
                {
                    ProviderName = request.ProviderName,
                    DetailKind = "track_list",
                    Heading = "Candidate track list",
                    SourceLabel = "Apple Music",
                    Facts = new(StringComparer.OrdinalIgnoreCase) { ["Collection ID"] = collectionId },
                    Items = tracks.Select((track, index) => new RetailCandidateDetailItemDto
                    {
                        Ordinal = track["trackNumber"]?.GetValue<int?>() ?? index + 1,
                        DiscNumber = track["discNumber"]?.GetValue<int?>(),
                        Title = track["trackName"]?.GetValue<string>() ?? $"Track {index + 1}",
                        DurationSeconds = track["trackTimeMillis"]?.GetValue<long?>() is { } milliseconds
                            ? milliseconds / 1000d
                            : null,
                        ProviderItemId = track["trackId"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture),
                    }).ToList(),
                };
                detail.Facts["Tracks"] = detail.Items.Count.ToString(CultureInfo.InvariantCulture);
                if (detail.Items.Count == 0)
                {
                    detail.UnavailableMessage = "Apple Music did not supply a track list for this candidate.";
                }

                return Results.Ok(detail);
            }

            return Results.Ok(UnavailableDetail(request.ProviderName, $"{request.ProviderName} did not supply additional candidate details."));
        })
        .WithName("GetRetailCandidateDetail")
        .WithSummary("Load provider-specific evidence, including album track lists, for a retail candidate.")
        .Produces<RetailCandidateDetailDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch);

        // ── POST /search/retail ──────────────────────────────────────────────
        group.MapPost("/retail", async (
            SearchRetailRequestDto request,
            ISearchService searchService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return ApiErrors.BadRequest("Query is required.");
            }

            var result = await searchService.SearchRetailAsync(new SearchRetailRequest(
                request.Query,
                request.MediaType,
                request.MaxCandidates,
                request.LocalTitle,
                request.LocalAuthor,
                request.LocalYear,
                request.FileHints,
                request.SearchFields), ct);
            return Results.Ok(MapRetailResponse(result));
        })
        .WithName("SearchRetail")
        .WithSummary("Search retail providers (TMDB, Apple Books, etc.) for cover art and basic metadata.")
        .Produces<SearchRetailResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch);

        // ── POST /search/resolve ─────────────────────────────────────────────
        group.MapPost("/resolve", async (
            SearchResolveRequestDto request,
            ISearchService searchService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return ApiErrors.BadRequest("Query is required.");
            }

            // Extract local title/author/year from file hints for retail scoring
            var fileHints = request.FileHints ?? [];
            fileHints.TryGetValue("title", out var localTitle);
            fileHints.TryGetValue("author", out var localAuthor);
            fileHints.TryGetValue("year", out var localYear);

            var retailRequest = new SearchRetailRequest(
                request.Query,
                request.MediaType,
                request.MaxCandidates,
                LocalTitle: localTitle,
                LocalAuthor: localAuthor,
                LocalYear: localYear,
                FileHints: fileHints.Count > 0
                                 ? fileHints
                                 : null);

            var retailResults = await searchService.SearchRetailAsync(retailRequest, ct);

            // Map retail candidates to resolve candidates.
            // Wikidata bridge resolution runs client-side after the user selects a candidate
            // (too slow to run for all candidates in one request).
            var candidates = retailResults.Candidates
                .Select(r => new SearchResolveCandidateDto
                {
                    ProviderName = r.ProviderName,
                    ProviderItemId = r.ProviderItemId ?? "",
                    Title = r.Title,
                    Author = r.Author,
                    Year = r.Year,
                    Description = r.Description,
                    CoverUrl = r.CoverUrl,
                    RetailScore = r.Confidence,
                    DescriptionScore = 0.0,
                    CompositeScore = r.CompositeScore,
                })
                .ToList();

            return Results.Ok(new SearchResolveResponseDto { Candidates = candidates });
        })
        .WithName("SearchResolve")
        .WithDescription("Unified resolve search: retail identification with description-based scoring. " +
                         "Wikidata bridge resolution runs client-side after candidate selection.")
        .Produces<SearchResolveResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch);

        return app;
    }

    private static SearchRetailResponseDto MapRetailResponse(SearchRetailResult result)
    {
        return new SearchRetailResponseDto
        {
            Query = result.Query,
            MediaType = result.MediaType,
            Candidates = result.Candidates.Select(candidate => new SearchRetailCandidateDto
            {
                ProviderId = candidate.ProviderId,
                ProviderName = candidate.ProviderName,
                ProviderItemId = candidate.ProviderItemId,
                Title = candidate.Title,
                Year = candidate.Year,
                Author = candidate.Author,
                Director = candidate.Director,
                Description = candidate.Description,
                CoverUrl = candidate.CoverUrl,
                Confidence = candidate.Confidence,
                ExtraFields = new Dictionary<string, string>(
                    candidate.ExtraFields,
                    StringComparer.OrdinalIgnoreCase),
                MatchScores = MapMatchScores(candidate.MatchScores),
                CompositeScore = candidate.CompositeScore,
            }).ToList(),
        };
    }

    private static string? FirstValue(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return null;
    }

    private static RetailCandidateDetailDto BuildMusicBrainzDetail(MusicBrainzAlbumRelease? release)
    {
        if (release is null)
        {
            return UnavailableDetail("musicbrainz", "MusicBrainz did not supply a track list for this candidate release.");
        }

        var detail = new RetailCandidateDetailDto
        {
            ProviderName = "musicbrainz",
            DetailKind = "track_list",
            Heading = "Candidate track list",
            SourceLabel = "MusicBrainz",
            Facts = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Release ID"] = release.ReleaseId,
                ["Tracks"] = release.TrackCount.ToString(CultureInfo.InvariantCulture),
            },
        };

        using var manifest = JsonDocument.Parse(release.ManifestJson);
        if (!manifest.RootElement.TryGetProperty("tracks", out var tracks))
        {
            return detail;
        }

        foreach (var track in tracks.EnumerateArray())
        {
            detail.Items.Add(new RetailCandidateDetailItemDto
            {
                Ordinal = track.TryGetProperty("track_number", out var number) && number.TryGetInt32(out var parsedNumber)
                    ? parsedNumber
                    : detail.Items.Count + 1,
                DiscNumber = track.TryGetProperty("disc_number", out var disc) && disc.TryGetInt32(out var parsedDisc)
                    ? parsedDisc
                    : null,
                Title = track.TryGetProperty("title", out var title) ? title.GetString() ?? "Untitled track" : "Untitled track",
                DurationSeconds = track.TryGetProperty("duration_seconds", out var duration) && duration.TryGetDouble(out var seconds)
                    ? seconds
                    : null,
                ProviderItemId = track.TryGetProperty("musicbrainz_recording_id", out var recordingId)
                    ? recordingId.GetString()
                    : null,
            });
        }
        return detail;
    }

    private static RetailCandidateDetailDto UnavailableDetail(string provider, string message) => new()
    {
        ProviderName = provider,
        Heading = "Provider details",
        SourceLabel = provider,
        UnavailableMessage = message,
    };

    private static FieldMatchScoresDto? MapMatchScores(FieldMatchResult? scores)
    {
        return scores is null
            ? null
            : new FieldMatchScoresDto
            {
                FieldScores = MediaEngine.Api.Services.Canonical.FieldScoreContractMapper.Map(scores.FieldScores),
                TitleScore = scores.TitleScore,
                AuthorScore = scores.AuthorScore,
                YearScore = scores.YearScore,
                FormatScore = scores.FormatScore,
                CompositeScore = scores.CompositeScore,
                TitleVerdict = (int)scores.TitleVerdict,
                AuthorVerdict = (int)scores.AuthorVerdict,
                YearVerdict = (int)scores.YearVerdict,
                FormatVerdict = (int)scores.FormatVerdict,
                CoverScore = scores.CoverScore,
                CoverVerdict = (int)scores.CoverVerdict,
            };
    }
}
