using System.Globalization;
using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Providers;

public sealed partial class TextTrackProviderAdapter
{
    /// <summary>What is needed later to download one subtitle: the release, the chosen file and its link.</summary>
    private sealed record DownloadSelection(string SubtitleId, string FileName, string? FileUrl);

    private async Task<IReadOnlyList<TextTrackCandidate>> SearchSubtitlesAsync(TextTrackLookup lookup, CancellationToken ct)
    {
        // A search by typed text needs no verified identity: the person named what to look for.
        var byText = !string.IsNullOrWhiteSpace(lookup.SearchText);
        if (!CanHandle(lookup.MediaType) || (lookup.SubtitleContext is null && !byText))
        {
            return [];
        }

        var availability = GetAvailability(lookup.MediaType);
        if (!availability.IsAvailable)
        {
            throw new TextTrackLookupException(
                availability.Status, availability.Message ?? Message(TextTrackMessages.Unreachable, "{display_name} is unavailable."));
        }

        // The first search that fits what is known about the title answers: its online ids when it has them, otherwise its name and year.
        // The log says what was asked and how many results came back.
        var values = LookupValues(lookup);
        var request = SelectRequest(lookup.MediaType, values);
        if (request is null)
        {
            _logger.LogInformation("{Provider} {Noun} search skipped for {Title}: nothing to search with", Name, _text.Noun, lookup.Title);
            return [];
        }

        var requestedLanguage = values.GetValueOrDefault("language", _text.Language.Unknown);
        var path = BuildPath(request, values);
        if (!lookup.Interactive && _emptySearches.TryGetValue(path, out var nextAttempt) && nextAttempt > DateTimeOffset.UtcNow)
        {
            _logger.LogInformation("{Provider} {Noun} search {Path} skipped: it found nothing a moment ago", Name, _text.Noun, path);
            return [];
        }

        using var response = await SendApiAsync(path, ct).ConfigureAwait(false);
        if (response is null)
        {
            throw new TextTrackLookupException("ProviderUnavailable", Message(TextTrackMessages.Unreachable, "{display_name} could not be reached."));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, ct).ConfigureAwait(false);
        }

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            var search = _text.Search;
            if (!IsSuccessful(root, search.SuccessPath)
                || !TryArray(root, search.TitlesPath, out var titles)
                || !TryArray(root, search.ItemsPath, out var items))
            {
                throw new TextTrackLookupException("ProviderError", Message(TextTrackMessages.SearchIncomplete, "{display_name} returned an incomplete search response."));
            }

            _logger.LogInformation("{Provider} {Noun} search {Path} answered with {Titles} titles and {Items} {Noun}", Name, _text.Noun, path, titles.GetArrayLength(), items.GetArrayLength(), _text.Noun);
            if (titles.GetArrayLength() == 0 || items.GetArrayLength() == 0)
            {
                _emptySearches[path] = DateTimeOffset.UtcNow.AddMinutes(search.EmptySearchMinutes);
                return [];
            }

            if (request.TitleCheck is { } check && !MatchesTitle(titles, check, values))
            {
                throw new TextTrackLookupException("IdentityMismatch", Message(TextTrackMessages.IdentityMismatch, "{display_name} returned results for a different title."));
            }

            var candidates = new List<TextTrackCandidate>();
            foreach (var item in items.EnumerateArray().Take(search.MaxItems))
            {
                AddCandidates(item, lookup, values, requestedLanguage, candidates);
            }

            var ranked = candidates.OrderByDescending(candidate => candidate.Confidence).ToList();
            // A person looking through a list of results picks one, so nothing is withheld for being tied and the list is not cut short here.
            // "Find another" (Interactive) is the same: the person is choosing, so ties are listed and the list is not cut to the automatic pick's few.
            if (byText || lookup.Interactive)
            {
                if (ranked.Count == 0)
                {
                    _emptySearches[path] = DateTimeOffset.UtcNow.AddMinutes(search.EmptySearchMinutes);
                }

                return ranked;
            }

            // Two equally plausible releases cannot be resolved by popularity or API ordering.
            if (ranked.Count > 1 && ranked[0].Confidence - ranked[1].Confidence < search.AmbiguityMargin)
            {
                throw new TextTrackLookupException("AmbiguousMatch", Message(TextTrackMessages.Ambiguous, "Several {display_name} results match equally well; no automatic choice was made."));
            }

            if (ranked.Count == 0)
            {
                _emptySearches[path] = DateTimeOffset.UtcNow.AddMinutes(search.EmptySearchMinutes);
            }

            return ranked.Take(search.MaxCandidates).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "{Provider} returned an invalid {Noun} response", Name, _text.Noun);
            await _health.ReportFailureAsync(Name, $"Invalid {_text.Noun} response", ct).ConfigureAwait(false);
            throw new TextTrackLookupException("ProviderError", Message(TextTrackMessages.SearchInvalid, "{display_name} returned an invalid search response."));
        }
    }

    private static string LettersAndDigits(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsSuccessful(JsonElement root, string? successPath) =>
        string.IsNullOrEmpty(successPath) || ReadBool(root, successPath) != false;

    private static bool MatchesTitle(
        JsonElement titles, TextTrackTitleCheckConfiguration check, IReadOnlyDictionary<string, string> values)
    {
        string Fill(string template) => Placeholder.Replace(template, match => values.GetValueOrDefault(match.Groups[1].Value, string.Empty));
        // A search by name has no id to compare: the answer must name the same title (letters and digits only, any case) and, when the year is known, the same year.
        if (string.IsNullOrEmpty(check.IdPath) && !string.IsNullOrEmpty(check.NamePath))
        {
            var wanted = LettersAndDigits(Fill(check.Name));
            var year = Fill(check.Year);
            return titles.EnumerateArray().Any(title =>
                string.Equals(ReadString(title, "type"), check.Type, StringComparison.OrdinalIgnoreCase)
                && LettersAndDigits(ReadString(title, check.NamePath) ?? string.Empty) == wanted
                && (string.IsNullOrEmpty(year) || string.Equals(ReadString(title, check.YearPath) ?? string.Empty, year, StringComparison.Ordinal)));
        }

        var expected = Fill(check.Id);
        var comparison = check.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return titles.EnumerateArray().Any(title =>
            string.Equals(ReadString(title, "type"), check.Type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadString(title, check.IdPath), expected, comparison));
    }

    private void AddCandidates(
        JsonElement item,
        TextTrackLookup lookup,
        IReadOnlyDictionary<string, string> values,
        string requestedLanguage,
        List<TextTrackCandidate> candidates)
    {
        var config = _text.Search.Item;
        var subtitleId = FirstString(item, config.IdPaths);
        if (string.IsNullOrWhiteSpace(subtitleId) || !IsSafeId(subtitleId))
        {
            return;
        }

        var episode = config.EpisodeMatch is not null
            && int.TryParse(values.GetValueOrDefault("season_number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var season)
            && int.TryParse(values.GetValueOrDefault("episode_number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? (Season: season, Episode: number)
            : ((int Season, int Episode)?)null;
        var release = FirstString(item, config.ReleasePaths);
        var files = config.Files is { } filesConfig && TryArray(item, filesConfig.Path, out var unpack) ? unpack : default;
        if (config.Files is { } fileRules && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
        {
            foreach (var file in files.EnumerateArray().Take(fileRules.Max))
            {
                if (episode is { } wanted && !EpisodeMatches(file, config.EpisodeMatch!, wanted.Season, wanted.Episode))
                {
                    continue;
                }

                var fileName = ReadString(file, fileRules.NamePath);
                var fileId = FirstString(file, fileRules.IdPaths);
                var url = ReadString(file, fileRules.UrlPath);
                if (fileId is null || !IsSafeId(fileId) || !TryDownloadUri(url, out _) || !SupportedFile(fileName))
                {
                    continue;
                }

                var language = NormalizeLanguage(FirstString(file, fileRules.LanguagePaths) ?? FirstString(item, config.LanguagePaths));
                if (language != requestedLanguage)
                {
                    continue;
                }

                var hearingImpaired = FirstBool(file, config.HearingImpairedPaths) ?? FirstBool(item, config.HearingImpairedPaths) ?? false;
                AddCandidate(subtitleId, fileId, fileName!, url, release, language, hearingImpaired, lookup, candidates);
            }

            return;
        }

        if (episode is { } required && !EpisodeMatches(item, config.EpisodeMatch!, required.Season, required.Episode))
        {
            return;
        }

        var itemName = ReadString(item, config.NamePath);
        if (!SupportedFile(itemName))
        {
            return;
        }

        var itemLanguage = NormalizeLanguage(FirstString(item, config.LanguagePaths));
        if (itemLanguage != requestedLanguage)
        {
            return;
        }

        AddCandidate(subtitleId, null, itemName!, null, release, itemLanguage,
            FirstBool(item, config.HearingImpairedPaths) ?? false, lookup, candidates);
    }

    private void AddCandidate(
        string subtitleId,
        string? fileId,
        string fileName,
        string? fileUrl,
        string? release,
        string language,
        bool hearingImpaired,
        TextTrackLookup lookup,
        List<TextTrackCandidate> candidates)
    {
        var score = _text.Search.Score;
        var format = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        var confidence = score.Base
            + ReleaseScore(Path.GetFileNameWithoutExtension(lookup.Asset.FilePathRoot), release) * score.ReleaseOverlapWeight
            + score.FormatBonus.GetValueOrDefault(format);
        if (hearingImpaired)
        {
            confidence += score.HearingImpaired;
        }

        candidates.Add(new TextTrackCandidate(
            Name,
            Kind,
            fileId is null ? subtitleId : $"{subtitleId}/{fileId}",
            _text.Search.SourceUrl?.Replace("{id}", Uri.EscapeDataString(subtitleId), StringComparison.Ordinal),
            language,
            format,
            confidence,
            hearingImpaired,
            null,
            new DownloadSelection(subtitleId, fileName, fileUrl),
            ReleaseName: release));
    }

    /// <summary>
    /// A release fits an episode only with exact coordinates: whole-season packs and episode
    /// ranges need an explicit unpacked member that names the episode itself.
    /// </summary>
    private static bool EpisodeMatches(JsonElement item, TextTrackEpisodeMatchConfiguration rules, int season, int episode) =>
        ReadInt(item, rules.SeasonPath) == season
        && ReadInt(item, rules.EpisodePath) == episode
        && rules.RejectTruePaths.All(path => ReadBool(item, path) != true)
        && rules.RangePaths.All(path => !ReadInt(item, path).HasValue || ReadInt(item, path) == episode);

    private bool SupportedFile(string? name) =>
        name is not null
        && _text.Download.Formats.Contains(Path.GetExtension(name).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    private static bool IsSafeId(string value) => value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static double ReleaseScore(string? local, string? remote)
    {
        if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(remote))
        {
            return 0;
        }

        var localWords = Tokenize(local);
        var remoteWords = Tokenize(remote);
        if (localWords.Count == 0 || remoteWords.Count == 0)
        {
            return 0;
        }

        return (double)localWords.Intersect(remoteWords).Count() / localWords.Union(remoteWords).Count();
    }

    private static HashSet<string> Tokenize(string value) => new(value.ToLowerInvariant()
        .Split(['.', '_', '-', ' ', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
        .Where(word => word.Length > 1), StringComparer.Ordinal);

    // -- JSON reading helpers (a missing or differently typed field reads as absent) -------------

    private static bool TryArray(JsonElement node, string? path, out JsonElement value)
    {
        value = default;
        return !string.IsNullOrEmpty(path) && TryPath(node, path, out value) && value.ValueKind == JsonValueKind.Array;
    }

    private static bool TryPath(JsonElement node, string path, out JsonElement value)
    {
        value = node;
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ReadString(JsonElement node, string path) =>
        !string.IsNullOrEmpty(path) && TryPath(node, path, out var value) ? ScalarText(value) : null;

    private static string? FirstString(JsonElement node, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (ReadString(node, path) is { } text)
            {
                return text;
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement node, string path) =>
        int.TryParse(ReadString(node, path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? ReadBool(JsonElement node, string path) =>
        !string.IsNullOrEmpty(path) && TryPath(node, path, out var value)
            ? value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : null
            : null;

    private static bool? FirstBool(JsonElement node, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (ReadBool(node, path) is { } flag)
            {
                return flag;
            }
        }

        return null;
    }
}
