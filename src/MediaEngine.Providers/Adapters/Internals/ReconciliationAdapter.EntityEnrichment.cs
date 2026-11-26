using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ReconciliationAdapter
{
    // A TV show or album is looked up once per run: every episode or track of it asks for the same children,
    // and each walk costs dozens of Wikidata requests (see SharedWalkMemo).
    private readonly SharedWalkMemo<IReadOnlyList<ProviderClaim>> _childEntityClaims = new(TimeSpan.FromMinutes(30));

    internal Task<IReadOnlyList<ProviderClaim>> DiscoverChildEntitiesAsync(
        string parentQid,
        MediaType mediaType,
        string language,
        CancellationToken ct) =>
        _childEntityClaims.GetAsync(
            LookupMemo<int>.Key(parentQid, mediaType.ToString(), language),
            () => DiscoverChildEntitiesCoreAsync(parentQid, mediaType, language, CancellationToken.None),
            claims => claims.Count > 0,
            ct);

    private async Task<IReadOnlyList<ProviderClaim>> DiscoverChildEntitiesCoreAsync(
        string parentQid,
        MediaType mediaType,
        string language,
        CancellationToken ct)
    {
        var claims = new List<ProviderClaim>();
        if (_reconciler is null)
        {
            return claims;
        }

        var kind = mediaType switch
        {
            MediaType.TV => ChildEntityKind.TvSeasonsAndEpisodes,
            MediaType.Music => ChildEntityKind.MusicTracks,
            MediaType.Comics => ChildEntityKind.ComicIssues,
            _ => (ChildEntityKind?)null,
        };
        if (kind is null)
        {
            return claims;
        }

        ChildEntityManifest manifest;
        try
        {
            manifest = await _reconciler.Children.GetChildEntitiesAsync(new ChildEntityRequest
            {
                ParentQid = parentQid,
                Kind = kind.Value,
                Language = language,
                MaxPrimary = mediaType == MediaType.TV ? MaxTvSeasons : MaxChildEntities,
                MaxTotal = MaxChildEntities,
                IncludeCreatorProperties = true,
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Provider}: child entity discovery — manifest builder failed for {QID} ({MediaType})",
                Name, parentQid, mediaType);
            return claims;
        }

        if (manifest is null || manifest.Children.Count == 0)
        {
            return claims;
        }

        switch (mediaType)
        {
            case MediaType.TV:
                {
                    // v2.5.0 returns TV manifests with seasons in the primary slice
                    // and episodes tagged with Parent = season number, so the
                    // adapter can group directly from the manifest. Any episode that
                    // still arrives without a usable Parent is surfaced under an
                    // "Unassigned" pseudo-season instead of triggering extra fetches.
                    var projection = BuildTvManifestProjection(manifest);
                    claims.Add(new ProviderClaim(MetadataFieldConstants.SeasonCount, projection.SeasonCount.ToString(), ClaimConfidence.WikidataProperty));
                    claims.Add(new ProviderClaim(MetadataFieldConstants.EpisodeCount, projection.EpisodeCount.ToString(), ClaimConfidence.WikidataProperty));
                    claims.Add(new ProviderClaim(MetadataFieldConstants.ChildEntitiesJson, projection.JsonBlob, ClaimConfidence.WikidataProperty));
                    _logger.LogInformation(
                        "{Provider}: child entity discovery — TV {QID}: {SeasonCount} seasons, {EpisodeCount} episodes ({Unassigned} unassigned)",
                        Name, parentQid, projection.SeasonCount, projection.EpisodeCount, projection.UnassignedEpisodeCount);
                    break;
                }

            case MediaType.Music:
                {
                    var trackNodes = manifest.Children.Select(t => new
                    {
                        qid = t.Qid,
                        title = t.Title,
                        ordinal = t.Ordinal,
                        duration_minutes = t.Duration is { } d ? (int?)Math.Round(d.TotalMinutes) : null,
                        performer = t.Creators?.GetValueOrDefault("Performer"),
                        release_date = t.ReleaseDate?.ToString("yyyy-MM-dd"),
                    }).ToList();

                    var jsonBlob = JsonSerializer.Serialize(new { tracks = trackNodes });
                    claims.Add(new ProviderClaim(MetadataFieldConstants.TrackCount, trackNodes.Count.ToString(), ClaimConfidence.WikidataProperty));
                    claims.Add(new ProviderClaim(MetadataFieldConstants.ChildEntitiesJson, jsonBlob, ClaimConfidence.WikidataProperty));

                    _logger.LogInformation(
                        "{Provider}: child entity discovery — Music {QID}: {TrackCount} tracks",
                        Name, parentQid, trackNodes.Count);
                    break;
                }

            case MediaType.Comics:
                {
                    var issueNodes = manifest.Children.Select(i => new
                    {
                        qid = i.Qid,
                        title = i.Title,
                        ordinal = i.Ordinal,
                        publication_date = i.ReleaseDate?.ToString("yyyy-MM-dd"),
                    }).ToList();

                    var jsonBlob = JsonSerializer.Serialize(new { issues = issueNodes });
                    claims.Add(new ProviderClaim(MetadataFieldConstants.IssueCount, issueNodes.Count.ToString(), ClaimConfidence.WikidataProperty));
                    claims.Add(new ProviderClaim(MetadataFieldConstants.ChildEntitiesJson, jsonBlob, ClaimConfidence.WikidataProperty));

                    _logger.LogInformation(
                        "{Provider}: child entity discovery — Comics {QID}: {IssueCount} issues",
                        Name, parentQid, issueNodes.Count);
                    break;
                }
        }

        return claims;
    }

    // ── Private: FetchPersonAsync ─────────────────────────────────────────────

    private async Task<IReadOnlyList<ProviderClaim>> FetchPersonAsync(
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var qid = request.PreResolvedQid;
        var name = request.PersonName ?? request.Author ?? request.Narrator;
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(qid))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(qid))
        {
            // Build constraints from person role hints.
            var constraints = BuildPersonConstraints(request);
            var candidates = await ReconcileAsync(name!, constraints, ct).ConfigureAwait(false);

            if (candidates.Count == 0)
            {
                return [];
            }

            var top = candidates[0];
            if (top.Score < _config.Reconciliation.ReviewThreshold)
            {
                return [];
            }

            qid = top.Id;
        }

        // Extend with person properties.
        var personProps = _config.DataExtension.PersonProperties;
        var allProps = BuildPersonExtensionProperties(personProps).ToList();

        // Inject language-specific label (Len) and description (Den) magic suffixes.
        var language = _configLoader?.LoadCore().Language.Metadata ?? "en";
        allProps.Add($"L{language}");
        allProps.Add($"D{language}");

        if (allProps.Count == 0)
        {
            return [new ProviderClaim(BridgeIdKeys.WikidataQid, qid, 1.0)];
        }

        var extensions = await ExtendAsync([qid], allProps, ct).ConfigureAwait(false);
        extensions.TryGetValue(qid, out var extPersonProps);

        var claims = new List<ProviderClaim>
        {
            new(BridgeIdKeys.WikidataQid, qid, 1.0)
        };

        if (extPersonProps is not null)
        {
            claims.AddRange(ExtensionToClaims(qid, extPersonProps, _config.DataExtension.PropertyLabels, isWork: false, castMemberLimit: 0, metadataLanguage: language));
        }

        // Data Extension label pseudo-properties are not guaranteed to be
        // returned by every compatible endpoint. Resolve the canonical label
        // through the dedicated label service when the extension response did
        // not produce one, so internal QIDs never become display names.
        if (!claims.Any(claim =>
                string.Equals(claim.Key, "name", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(claim.Value)))
        {
            var displayName = await FetchDisplayLabelAsync(qid, language, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                claims.Add(new ProviderClaim("name", displayName, ClaimConfidence.WikidataProperty));
            }
        }

        // ── Wikipedia description ─────────────────────────────────────────────
        // Fetch a rich Wikipedia description for this person using the resolved QID.
        // Failures never block — an empty list is returned and execution continues.
        // Use "biography" as the claim key so MetadataHarvestingService.HandlePersonEnrichmentAsync
        // can locate the value — it looks for key "biography", not "description".
        var wikiPersonClaims = await FetchWikipediaDescriptionAsync(qid, language, ct,
            claimKey: MetadataFieldConstants.Biography)
            .ConfigureAwait(false);
        claims.AddRange(wikiPersonClaims);

        _logger.LogInformation("{Provider}: fetched {Count} person claims for QID {QID}",
            Name, claims.Count, qid);

        return claims;
    }

    internal static IReadOnlyList<string> BuildPersonExtensionProperties(
        DataExtensionPropertyGroup personProps)
        => personProps.Core
            .Concat(personProps.Social)
            .Concat(personProps.PenNames)
            .Concat(personProps.Group)
            .Distinct()
            .ToList();

    // ── Private: Wikipedia description ───────────────────────────────────────────

    /// <summary>
    /// Fetches a rich Wikipedia description for the given Wikidata QID.
    /// Returns up to three claims: the description claim (confidence 0.90), "wikipedia_url" (1.0),
    /// and optionally "plot_summary". Uses language fallback built into the library.
    /// Always returns an empty list on failure — never throws.
    /// </summary>
    /// <param name="claimKey">
    /// The claim key to use for the primary description claim.
    /// Pass <c>"biography"</c> when fetching for a Person; defaults to <c>"description"</c> for Works.
    /// </param>
    private async Task<IReadOnlyList<ProviderClaim>> FetchWikipediaDescriptionAsync(
        string qid,
        string language,
        CancellationToken ct,
        string claimKey = MetadataFieldConstants.Description)
    {
        if (_reconciler is null || string.IsNullOrWhiteSpace(qid))
        {
            return [];
        }

        try
        {
            var lang = NormalizeLang(language);

            // Use language fallback: try requested language, then English
            var fallbackLangs = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)
                ? (IReadOnlyList<string>)["en"]
                : (IReadOnlyList<string>)["en"];

            var summaries = await _reconciler.GetWikipediaSummariesAsync(
                [qid], lang, fallbackLangs, ct).ConfigureAwait(false);

            var summary = summaries?.FirstOrDefault();
            if (summary is null || string.IsNullOrWhiteSpace(summary.Extract))
            {
                _logger.LogDebug("{Provider}: Wikipedia summary empty for {Qid}", Name, qid);
                return [];
            }

            var resolvedLang = summary.Language ?? lang;

            _logger.LogInformation("{Provider}: Wikipedia description for {Qid} ({Lang}): {Len} chars",
                Name, qid, resolvedLang, summary.Extract.Length);

            var resultClaims = new List<ProviderClaim>
            {
                new(claimKey, StripLeadingMediaWikiHeadings(summary.Extract), ClaimConfidence.Description),
                new("wikipedia_url", summary.ArticleUrl ?? "", 1.0),
            };

            // Fetch Wikipedia Plot/Synopsis section for richer LLM analysis.
            try
            {
                var sections = await _reconciler.GetWikipediaSectionsAsync([qid], resolvedLang, ct)
                    .ConfigureAwait(false);

                if (sections is not null && sections.TryGetValue(qid, out var toc) && toc is not null)
                {
                    var plotSectionNames = new[] { "Plot", "Synopsis", "Plot summary", "Summary", "Premise", "Overview" };
                    var plotSection = toc.FirstOrDefault(s =>
                        plotSectionNames.Any(name => string.Equals(s.Title, name, StringComparison.OrdinalIgnoreCase)));

                    if (plotSection is not null)
                    {
                        var plotContent = await _reconciler.GetWikipediaSectionContentAsync(
                            qid, plotSection.Index, resolvedLang, ct).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(plotContent))
                        {
                            resultClaims.Add(new ProviderClaim("plot_summary", StripLeadingMediaWikiHeadings(plotContent), ClaimConfidence.PlotSummary));
                            _logger.LogInformation(
                                "{Provider}: Wikipedia plot section '{Section}' for {Qid}: {Len} chars",
                                Name, plotSection.Title, qid, plotContent.Length);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "{Provider}: Wikipedia plot section fetch failed for {Qid}", Name, qid);
            }

            return resultClaims;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Provider}: Wikipedia description fetch failed for {Qid}", Name, qid);
            return [];
        }
    }

    /// <summary>
    /// Normalises a BCP-47 language tag to its primary subtag (e.g. "en-US" → "en").
    /// Returns "en" when the input is null or empty.
    /// </summary>
    /// <summary>
    /// Cleans an audiobook (or book) title before Wikidata reconciliation by stripping
    /// edition markers and genre-subtitle suffixes that confuse CirrusSearch.
    /// <list type="bullet">
    ///   <item><c>"Project Hail Mary (Unabridged)"</c> → <c>"Project Hail Mary"</c></item>
    ///   <item><c>"Dune: A Novel"</c> → <c>"Dune"</c></item>
    ///   <item><c>"Where the Crawdads Sing A Novel"</c> → <c>"Where the Crawdads Sing"</c></item>
    /// </list>
    /// Preserves "Unabridged" when it is part of the title itself (e.g. "The Unabridged Story")
    /// and "A Novel" when it appears in the middle of a title (e.g. "A Novel Approach to Chess").
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> FetchWikipediaExtractsAsync(
        IEnumerable<string> qids,
        string language,
        CancellationToken ct)
    {
        if (_reconciler is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var uniqueQids = qids
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();

        if (uniqueQids.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var lang = NormalizeLang(language);
            var fallbackLangs = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)
                ? (IReadOnlyList<string>)["en"]
                : (IReadOnlyList<string>)["en"];

            var summaries = await _reconciler.GetWikipediaSummariesAsync(
                uniqueQids, lang, fallbackLangs, ct).ConfigureAwait(false);

            return summaries
                .Where(summary => !string.IsNullOrWhiteSpace(summary.EntityId)
                    && !string.IsNullOrWhiteSpace(summary.Extract))
                .GroupBy(summary => summary.EntityId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => StripLeadingMediaWikiHeadings(group.First().Extract),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Provider}: batch Wikipedia description fetch failed for {Count} child entities",
                Name, uniqueQids.Count);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static string CleanAudiobookTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var cleaned = title;

        // Strip parenthesized/bracketed edition markers: (Unabridged), [Unabridged],
        // (Abridged), (Audiobook).
        cleaned = Regex.Replace(cleaned,
            @"\s*[\[\(](Unabridged|Abridged|Audiobook)[\]\)]\s*$",
            string.Empty, RegexOptions.IgnoreCase);

        // Strip subtitle suffix introduced by ":" or "-": ": A Novel", "- A Memoir", etc.
        // The trailing descriptor must be a known genre/format word.
        cleaned = Regex.Replace(cleaned,
            @"\s*[-–—:]\s+(A|An)\s+(Novel|Memoir|Thriller|Story|Tale|Journey|Mystery|Romance)\s*$",
            string.Empty, RegexOptions.IgnoreCase);

        // Strip trailing "A Novel" / "A Memoir" without any separator.
        // Only removes when at the END of the title — "A Novel Approach to Chess" is preserved.
        cleaned = Regex.Replace(cleaned,
            @"\s+(A|An)\s+(Novel|Memoir|Thriller|Story|Tale|Journey|Mystery|Romance)\s*$",
            string.Empty, RegexOptions.IgnoreCase);

        return cleaned.Trim();
    }

    internal static string ResolveDisplayLanguage(string? metadataLanguage, string? fileLanguage)
        => NormalizeLang(metadataLanguage);

    internal static string ResolveSearchLanguage(string? metadataLanguage, string? fileLanguage)
    {
        var metadata = NormalizeLang(metadataLanguage);
        var file = NormalizeOptionalLang(fileLanguage);

        return !string.IsNullOrWhiteSpace(file)
               && !string.Equals(file, metadata, StringComparison.OrdinalIgnoreCase)
            ? file
            : metadata;
    }

    private async Task<string?> FetchDisplayLabelAsync(string qid, string displayLanguage, CancellationToken ct)
    {
        if (_reconciler is null || string.IsNullOrWhiteSpace(qid))
        {
            return null;
        }

        try
        {
            return await _reconciler.Labels
                .GetAsync(qid, displayLanguage, withFallbackLanguage: false, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "{Provider}: failed to fetch display label for {Qid} in language '{Lang}'",
                Name, qid, displayLanguage);
            return null;
        }
    }

    private static bool IsSameLanguage(string? candidateLanguage, string displayLanguage)
    {
        var candidate = NormalizeOptionalLang(candidateLanguage);
        return string.IsNullOrWhiteSpace(candidate)
               || string.Equals(candidate, NormalizeLang(displayLanguage), StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptionalLang(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return null;
        }

        var primary = lang.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return string.IsNullOrWhiteSpace(primary) ? null : primary.ToLowerInvariant();
    }

    private static string NormalizeLang(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return "en";
        }

        var primary = lang.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)[0];
        return primary.ToLowerInvariant();
    }

    /// <summary>
    /// Strips MediaWiki section heading lines (e.g. "== Plot ==", "=== Synopsis ===") from
    /// the start of a description string and trims any resulting leading whitespace.
    /// Headings anywhere after the first non-heading line are left untouched.
    /// </summary>
    private static string StripLeadingMediaWikiHeadings(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var lines = text.Split('\n');
        var firstContentLine = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            // Match lines that are pure MediaWiki headings: ==...== with optional surrounding whitespace
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^={2,}\s*.+?\s*={2,}$"))
            {
                firstContentLine = i + 1;
            }
            else if (!string.IsNullOrWhiteSpace(trimmed))
            {
                // First non-heading, non-blank line — stop scanning
                break;
            }
        }

        return firstContentLine == 0
            ? text
            : string.Join('\n', lines.Skip(firstContentLine)).TrimStart();
    }

    /// <summary>
    /// Filters edition data by media type. Audiobooks get only audiobook-class editions;
    /// books get only non-audiobook editions. Other media types get all editions unfiltered.
    /// </summary>
}
