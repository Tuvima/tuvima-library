using System.Globalization;
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
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>> FilterEditionsByMediaType(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>> editions,
        MediaType mediaType)
    {
        // Only filter for book-related media types.
        if (mediaType != MediaType.Audiobooks && mediaType != MediaType.Books)
        {
            return editions;
        }

        var audiobookClasses = new HashSet<string>(GetAudiobookEditionClasses(), StringComparer.OrdinalIgnoreCase);

        var filtered = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (qid, props) in editions)
        {
            var isAudiobook = props.TryGetValue("P31", out var p31)
                && p31.Any(c => c.Value?.EntityId is not null && audiobookClasses.Contains(c.Value.EntityId!));

            if (mediaType == MediaType.Audiobooks && isAudiobook)
            {
                filtered[qid] = props;
            }
            else if (mediaType == MediaType.Books && !isAudiobook)
            {
                filtered[qid] = props;
            }
        }

        // Fall back to unfiltered if no editions match the filter (avoid losing all data).
        return filtered.Count > 0 ? filtered : editions;
    }


    private static Dictionary<string, string>? BuildTitleSearchConstraints(ProviderLookupRequest request)
    {
        var c = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!IsExactWikidataQid(request.Title)
            && request.MediaType is MediaType.Books or MediaType.Audiobooks or MediaType.Comics
            && !string.IsNullOrWhiteSpace(request.Author))
        {
            c["P50"] = request.Author;
        }

        return c.Count > 0 ? c : null;
    }

    private static bool IsExactWikidataQid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && System.Text.RegularExpressions.Regex.IsMatch(
            value.Trim(),
            @"^Q[1-9]\d*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private Dictionary<string, string>? BuildPersonConstraints(ProviderLookupRequest request)
    {
        var c = new Dictionary<string, string>(StringComparer.Ordinal);

        // Use configured person_property_constraints from config.
        foreach (var (pCode, claimKey) in _config.Reconciliation.PersonPropertyConstraints)
        {
            // Map claimKey back to a request value if available.
            var val = claimKey switch
            {
                "notable_work_title" => request.Title,
                "occupation" => request.PersonRole,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(val))
            {
                c[pCode] = val;
            }
        }

        return c.Count > 0 ? c : null;
    }

    // ── Private: Extension → ProviderClaim conversion ─────────────────────────

    private static IEnumerable<ProviderClaim> ExtensionToClaims(
        string entityQid,
        IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>> properties,
        Dictionary<string, string> propertyLabels,
        bool isWork,
        int castMemberLimit = 20,
        string? metadataLanguage = null,
        bool editionScopedDates = false) =>
        ExtensionToClaimsCore(
            entityQid,
            properties,
            propertyLabels,
            isWork,
            castMemberLimit,
            metadataLanguage,
            editionScopedDates,
            imageIsHeadshot: true);

    private static IEnumerable<ProviderClaim> ExtensionToClaimsCore(
        string entityQid,
        IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>> properties,
        Dictionary<string, string> propertyLabels,
        bool isWork,
        int castMemberLimit,
        string? metadataLanguage,
        bool editionScopedDates,
        bool imageIsHeadshot)
    {
        foreach (var (pCode, rawClaims) in properties)
        {
            // Cap multi-valued cast member properties to prevent storing dozens of minor roles.
            var claims = rawClaims;
            if (string.Equals(pCode, "P161", StringComparison.OrdinalIgnoreCase)
                && castMemberLimit > 0 && rawClaims.Count > castMemberLimit)
            {
                claims = rawClaims.Take(castMemberLimit).ToList();
            }
            // ── Magic suffix handling (Len, Den, etc.) ──
            // L{lang} returns the entity label in the user's language.
            // D{lang} returns the entity description in the user's language.
            if (pCode.Length == 3 && pCode[0] == 'L' && char.IsLower(pCode[1]))
            {
                // Emit the Wikidata label as a title claim (for works) or name claim (for persons)
                // at lower confidence than the reconciliation label (0.98). The reconciliation label
                // is typically the shorter, more natural title (e.g. "Frankenstein") while L{lang}
                // can return the full formal title (e.g. "Frankenstein; or, The Modern Prometheus").
                if (claims.Count > 0 && !string.IsNullOrWhiteSpace(claims[0].Value?.RawValue))
                {
                    var labelClaimKey = isWork ? MetadataFieldConstants.Title : "name";
                    yield return new ProviderClaim(labelClaimKey, claims[0].Value!.RawValue!, ClaimConfidence.WikidataProperty);
                }
                continue;
            }

            if (pCode.Length == 3 && pCode[0] == 'D' && char.IsLower(pCode[1]))
            {
                if (claims.Count > 0 && !string.IsNullOrWhiteSpace(claims[0].Value?.RawValue))
                {
                    yield return new ProviderClaim(MetadataFieldConstants.ShortDescription, claims[0].Value!.RawValue!, ClaimConfidence.Description);
                }

                continue;
            }

            // P577 can contain several regional premieres or re-releases. Treat
            // the earliest value as the original work date, and never merge a
            // known edition's date into the same canonical field.
            if (isWork && string.Equals(pCode, "P577", StringComparison.OrdinalIgnoreCase))
            {
                var earliest = claims
                    .Select(claim => new
                    {
                        Claim = claim,
                        Year = ExtractYear(claim.Value?.RawValue ?? string.Empty),
                    })
                    .Where(candidate => candidate.Year is not null)
                    .OrderBy(candidate => candidate.Year, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.Claim.Value?.RawValue, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (earliest is not null)
                {
                    var yearKey = editionScopedDates
                        ? MetadataFieldConstants.EditionReleaseYear
                        : MetadataFieldConstants.OriginalReleaseYear;
                    var dateKey = editionScopedDates
                        ? MetadataFieldConstants.EditionReleaseDate
                        : MetadataFieldConstants.OriginalReleaseDate;
                    yield return new ProviderClaim(
                        yearKey,
                        earliest.Year!,
                        ClaimConfidence.AlternateTitle);

                    var fullDate = ExtractDate(earliest.Claim.Value?.RawValue);
                    if (fullDate is not null)
                    {
                        yield return new ProviderClaim(
                            dateKey,
                            fullDate,
                            ClaimConfidence.AlternateTitle);
                    }
                }

                continue;
            }

            if (!propertyLabels.TryGetValue(pCode, out var claimKey))
            {
                continue;
            }

            // P18 (image) only for Person entities — and needs URL conversion.
            if (string.Equals(pCode, "P18", StringComparison.OrdinalIgnoreCase) && isWork)
            {
                continue;
            }

            // P31 (instance_of): for works, used internally for filtering only — skip claims.
            // For persons, emit as claims so pseudonym detection (Q127843/Q15632617) works.
            if (string.Equals(pCode, "P31", StringComparison.OrdinalIgnoreCase) && isWork)
            {
                continue;
            }

            // P1476 (title) — monolingual text; only take the first value to avoid
            // emitting every language translation as a separate claim.
            // When metadataLanguage is configured, prefer the value whose Language matches
            // the user's metadata language (e.g. "en"). Without this, the Wikidata API may
            // return the original Japanese or French title first for foreign-language works
            // (e.g. "千と千尋の神隠し" for Spirited Away when the user expects English).
            bool isMonolingualTitle = string.Equals(pCode, "P1476", StringComparison.OrdinalIgnoreCase);
            if (isMonolingualTitle && !string.IsNullOrWhiteSpace(metadataLanguage))
            {
                // Prefer the value in the user's metadata language. If there is
                // no preferred-language value, skip P1476 instead of emitting the
                // first arbitrary language value as a canonical title.
                var langNorm = metadataLanguage.Split('-', '_')[0].ToLowerInvariant();
                var preferredClaim = claims.FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(c.Value?.Language)
                    && c.Value!.Language!.Split('-', '_')[0].Equals(langNorm, StringComparison.OrdinalIgnoreCase));
                if (preferredClaim is not null)
                {
                    claims = [preferredClaim];
                }
                else
                {
                    continue;
                }
            }

            foreach (var claim in claims)
            {
                // Special handling for P18: convert Commons filename to URL.
                if (string.Equals(pCode, "P18", StringComparison.OrdinalIgnoreCase))
                {
                    var filename = claim.Value?.RawValue;
                    if (!string.IsNullOrWhiteSpace(filename))
                    {
                        var commonsUrl = $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(filename)}";
                        yield return new ProviderClaim(
                            imageIsHeadshot ? "headshot_url" : "image_url",
                            commonsUrl,
                            ClaimConfidence.HeadshotUrl);
                    }
                    continue;
                }

                // P2047 (duration) is a quantity whose unit decides the meaning of the number
                // ("3.5" minutes is not "3.5" seconds). Store whole seconds, reading the unit from
                // Wikidata; a duration with no recognised unit is dropped rather than guessed.
                if (string.Equals(pCode, "P2047", StringComparison.OrdinalIgnoreCase)
                    && claim.Value?.Kind == WikidataValueKind.Quantity)
                {
                    var seconds = DurationQuantityToSeconds(claim.Value.Amount, claim.Value.Unit);
                    if (seconds is not null)
                    {
                        yield return new ProviderClaim("duration_seconds", seconds, ClaimConfidence.Duration);
                    }
                    continue;
                }

                // Determine confidence and string value.
                // GetPropertiesAsync (v0.8+) calls ResolveClaimsEntityLabelsAsync internally,
                // populating EntityLabel for all entity references. v0.9+ made EntityLabel
                // publicly settable, so JSON cache round-trips preserve labels correctly.
                (string? strVal, double confidence) = ExtractValueAndConfidence(claim, pCode);

                // P179 (part_of_the_series): skip list-like and broad diagnostic containers.
                if (string.Equals(pCode, "P179", StringComparison.OrdinalIgnoreCase))
                {
                    var seriesLabel = strVal ?? claim.Value?.EntityLabel ?? claim.Value?.RawValue;
                    if (!string.IsNullOrWhiteSpace(seriesLabel) && IsUnsupportedSeriesContainerLabel(seriesLabel))
                    {
                        continue;
                    }

                    var seriesPosition = ExtractQualifierValue(claim, "P1545");
                    if (!string.IsNullOrWhiteSpace(seriesPosition))
                    {
                        yield return new ProviderClaim(
                            MetadataFieldConstants.SeriesPosition,
                            seriesPosition,
                            ClaimConfidence.WikidataProperty);

                        // The same position paired with its own Series, so a Work in two Series keeps each number.
                        if (!string.IsNullOrWhiteSpace(claim.Value?.EntityId))
                        {
                            var pairedPosition = seriesPosition.Trim();
                            yield return new ProviderClaim(
                                MetadataFieldConstants.SeriesPlacement,
                                pairedPosition,
                                ClaimConfidence.WikidataProperty);
                            yield return new ProviderClaim(
                                MetadataFieldConstants.SeriesPlacement + MetadataFieldConstants.CompanionQidSuffix,
                                $"{claim.Value!.EntityId}::{pairedPosition}",
                                ClaimConfidence.EntityQidReference);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(strVal))
                {
                    // Normalize bridge ID values (strip ISBN dashes, uppercase ASINs, etc.)
                    if (IsBridgeProperty(pCode))
                    {
                        var normalized = IdentifierNormalizationService.NormalizeRaw(pCode, strVal);
                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            strVal = normalized;
                        }
                    }
                    yield return new ProviderClaim(claimKey, strVal, confidence);
                }

                // Emit individual companion _qid claim per entity value.
                // Prefer EntityLabel (populated by library v0.8.0), then RawValue, then EntityId.
                if (claim.Value?.EntityId is not null)
                {
                    var label = claim.Value.EntityLabel ?? claim.Value.RawValue ?? claim.Value.EntityId;
                    yield return new ProviderClaim($"{claimKey}_qid", $"{claim.Value.EntityId}::{label}", ClaimConfidence.EntityQidReference);
                }

                // Only emit the first value for monolingual title properties.
                if (isMonolingualTitle)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Converts a Wikidata duration quantity to whole seconds using its unit entity; null when the unit is unknown.</summary>
    internal static string? DurationQuantityToSeconds(decimal? amount, string? unitUri)
    {
        if (amount is null or <= 0 || string.IsNullOrWhiteSpace(unitUri))
        {
            return null;
        }

        var unit = unitUri[(unitUri.LastIndexOf('/') + 1)..];
        var factor = unit switch
        {
            "Q723733" => 0.001m,  // millisecond
            "Q11574" => 1m,       // second
            "Q7727" => 60m,       // minute
            "Q25235" => 3600m,    // hour
            _ => 0m,
        };

        return factor == 0m
            ? null
            : decimal.Round(amount.Value * factor, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (string? value, double confidence) ExtractValueAndConfidence(
        WikidataClaim claim, string pCode)
    {
        var val = claim.Value;
        if (val is null)
        {
            return (null, 0.0);
        }

        // Date values.
        if (val.Kind == WikidataValueKind.Time)
        {
            var year = ExtractYear(val.RawValue);
            return (year, ClaimConfidence.AlternateTitle);
        }

        // Entity reference.
        if (val.Kind == WikidataValueKind.EntityId)
        {
            var isBridge = IsBridgeProperty(pCode);
            if (isBridge)
            {
                return (val.EntityId, ClaimConfidence.BridgeId);
            }

            // P50 (author) claims from Wikidata get a reduced confidence (0.75) so that
            // an embedded author from file metadata (confidence 1.0) always wins in the
            // priority cascade. This preserves pen names: when the EPUB credits a pen name
            // like "James S. A. Corey" but Wikidata P50 lists the real authors, the file's
            // credited author takes precedence. The pen name preservation block in
            // FetchWorkAsync re-keys P50 real-name claims when a mismatch is detected —
            // this reduced confidence acts as a second safety net for that same scenario.
            if (string.Equals(pCode, "P50", StringComparison.OrdinalIgnoreCase))
            {
                return (val.EntityLabel ?? val.RawValue ?? val.EntityId, ClaimConfidence.WikidataAuthorRaw);
            }

            // For other entity references (series, director, etc.) prefer EntityLabel, then RawValue.
            return (val.EntityLabel ?? val.RawValue ?? val.EntityId, ClaimConfidence.WikidataProperty);
        }

        // Quantity values.
        if (val.Kind == WikidataValueKind.Quantity)
        {
            return (val.Amount?.ToString(), ClaimConfidence.Duration);
        }

        // Plain string / monolingual text.
        if (!string.IsNullOrWhiteSpace(val.RawValue))
        {
            return (val.RawValue, ClaimConfidence.WikidataProperty);
        }

        return (null, 0.0);
    }

    private static string? ExtractQualifierValue(WikidataClaim claim, string propertyId)
    {
        if (!claim.Qualifiers.TryGetValue(propertyId, out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            var text = value.Amount?.ToString() ?? value.RawValue;
            text = text.Trim().TrimStart('+');
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (decimal.TryParse(text, out var numeric)
                && decimal.Truncate(numeric) == numeric)
            {
                return decimal.ToInt32(numeric).ToString();
            }

            return text;
        }

        return null;
    }

    private static bool IsBridgeProperty(string pCode) => pCode switch
    {
        "P212" => true, // isbn_13
        "P957" => true, // isbn_10
        "P5749" => true, // asin
        "P4947" => true, // tmdb_movie_id
        "P4983" => true, // tmdb_tv_id
        "P345" => true, // imdb_id
        "P6395" => true, // apple_books_id
        "P9586" => true, // apple_tv_movie_id
        "P9751" => true, // apple_tv_show_id
        "P9750" => true, // apple_tv_episode_id
        "P6381" => true, // itunes_tv_season_id
        "P6398" => true, // apple_itunes_id
        "P2281" => true, // apple_music_collection_id
        "P2850" => true, // apple_artist_id
        "P10110" => true, // apple_music_id
        "P5905" => true, // comic_vine_id
        "P434" => true, // musicbrainz_artist_id
        "P435" => true, // musicbrainz_work_id
        "P436" => true, // musicbrainz_release_group_id
        "P5813" => true, // musicbrainz_release_id
        "P4404" => true, // musicbrainz_recording_id
        "P4835" => true, // tvdb_id
        "P7043" => true, // tvdb_episode_id
        "P648" => true, // open_library_id
        _ => false,
    };

    /// <summary>
    /// Returns true when a P179 series label looks like a list article, publisher/
    /// production list, broad franchise, award list, poll, or ranking rather than
    /// an immediate narrative sequence.
    /// </summary>
    private static bool IsUnsupportedSeriesContainerLabel(string label)
    {
        var lower = label.ToLowerInvariant();
        if (lower.StartsWith("list of ", StringComparison.Ordinal)
            || lower.Contains("wikimedia list", StringComparison.Ordinal)
            || lower.Contains("production list", StringComparison.Ordinal)
            || lower.Contains("productions", StringComparison.Ordinal)
            || lower.Contains("filmography", StringComparison.Ordinal)
            || lower.Contains("franchise", StringComparison.Ordinal)
            || lower.Contains("fictional universe", StringComparison.Ordinal)
            || lower.Contains("shared universe", StringComparison.Ordinal))
        {
            return true;
        }

        string[] skipPatterns =
        [
            "greatest", "best of", "top ", "100 ", " 100", "poll", "ranking",
            "award", "bfi", "sight & sound", "sight and sound", "afi",
            "all-time", "all time", "most influential", "canonical"
        ];
        return skipPatterns.Any(p => lower.Contains(p));
    }

    private static string? ExtractYear(string isoDate)
    {
        if (string.IsNullOrWhiteSpace(isoDate))
        {
            return null;
        }

        // Handle "+1965-08-01T00:00:00Z" and "1965-01-01T00:00:00Z" formats.
        var s = isoDate.TrimStart('+');
        if (s.Length >= 4 && int.TryParse(s[..4], out var year) && year > 0)
        {
            return year.ToString();
        }

        return null;
    }

    private static string? ExtractDate(string? isoDate)
    {
        if (string.IsNullOrWhiteSpace(isoDate))
        {
            return null;
        }

        var value = isoDate.Trim().TrimStart('+');
        if (value.Length < 10
            || !DateOnly.TryParseExact(
                value[..10],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return null;
        }

        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // ── Private: Cache key + SHA-256 ─────────────────────────────────────────

    private string BuildCacheKey(string input) =>
        $"{_providerId}:{ComputeSha256(input)}";

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="text"/> contains characters from
    /// non-Latin scripts (CJK, Cyrillic, Arabic, Devanagari, etc.).
    /// Used to detect cross-script mismatches that should NOT trigger pen name logic.
    /// </summary>
    private static bool ContainsNonLatinScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (cat is not System.Globalization.UnicodeCategory.OtherLetter)
            {
                continue;
            }
            // OtherLetter covers CJK ideographs, Hangul, Arabic, Devanagari, Thai, etc.
            // — anything outside the Latin/Greek/Cyrillic Letter categories.
            return true;
        }
        // Also check for Cyrillic and Greek which are UppercaseLetter/LowercaseLetter
        // but different scripts from Latin.
        foreach (var ch in text)
        {
            // Cyrillic: U+0400–U+04FF; Greek: U+0370–U+03FF
            if (ch >= '\u0400' && ch <= '\u04FF')
            {
                return true;
            }

            if (ch >= '\u0370' && ch <= '\u03FF')
            {
                return true;
            }
            // CJK Unified Ideographs: U+4E00–U+9FFF
            if (ch >= '\u4E00' && ch <= '\u9FFF')
            {
                return true;
            }
            // Hiragana: U+3040–U+309F; Katakana: U+30A0–U+30FF
            if (ch >= '\u3040' && ch <= '\u30FF')
            {
                return true;
            }
            // Hangul Syllables: U+AC00–U+D7AF
            if (ch >= '\uAC00' && ch <= '\uD7AF')
            {
                return true;
            }
            // Arabic: U+0600–U+06FF
            if (ch >= '\u0600' && ch <= '\u06FF')
            {
                return true;
            }
        }
        return false;
    }

    // ── Public: Entity staleness check ───────────────────────────────────────

    /// <summary>
    /// Lightweight staleness check: compares stored revision IDs against current Wikidata
    /// revision IDs. Returns only QIDs that have changed since the stored revision.
    /// Used by the 30-day refresh cycle to skip expensive re-fetches for unchanged entities.
    /// </summary>
    public async Task<IReadOnlyList<string>> CheckEntityStalenessAsync(
        IReadOnlyDictionary<string, long> storedRevisions,
        CancellationToken ct = default)
    {
        if (_reconciler is null || storedRevisions.Count == 0)
        {
            return [];
        }

        try
        {
            var qids = storedRevisions.Keys.ToList();
            var currentRevisions = await _reconciler.GetRevisionIdsAsync(qids, ct).ConfigureAwait(false);

            var staleQids = new List<string>();
            foreach (var (qid, storedRevId) in storedRevisions)
            {
                if (!currentRevisions.TryGetValue(qid, out var current))
                {
                    // Entity not found — treat as stale (may have been deleted/merged)
                    staleQids.Add(qid);
                    continue;
                }

                if (current.RevisionId != storedRevId)
                {
                    staleQids.Add(qid);
                }
            }

            _logger.LogDebug("{Provider}: staleness check — {Stale}/{Total} entities have changed",
                Name, staleQids.Count, storedRevisions.Count);

            return staleQids;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: staleness check failed — treating all as stale", Name);
            return storedRevisions.Keys.ToList();
        }
    }

}
