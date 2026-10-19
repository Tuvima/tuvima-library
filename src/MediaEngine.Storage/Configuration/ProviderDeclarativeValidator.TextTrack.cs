using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Configuration;

internal static partial class ProviderDeclarativeValidator
{
    private static readonly string[] TextTrackLookupValues =
        ["title", "artist", "album", "year", "language", "duration_seconds", "tmdb_id", "imdb_id", "season_number", "episode_number"];

    private static readonly string[] TextTrackStatuses =
        ["AuthenticationRequired", "QuotaExceeded", "RateLimited", "ProviderUnavailable", "ProviderError"];

    private static void ValidateTextTrack(ProviderConfiguration provider, List<string> errors)
    {
        var isTextTrack = string.Equals(provider.AdapterType, "text_track", StringComparison.OrdinalIgnoreCase);
        if (provider.TextTrack is not { } text)
        {
            if (isTextTrack)
            {
                errors.Add("text_track is required when adapter_type is text_track.");
            }

            return;
        }

        const string path = "text_track";
        AddUnknown(text.UnmappedProperties, path, errors);
        if (!Allowed(text.Kind, TextTrackKinds.Subtitles, TextTrackKinds.Lyrics))
        {
            errors.Add($"{path}.kind must be Subtitles or Lyrics.");
        }

        AddRequired(errors, text.Noun, $"{path}.noun");
        AddUnknown(text.Api.UnmappedProperties, $"{path}.api", errors);
        AddUnknown(text.Http.UnmappedProperties, $"{path}.http", errors);
        AddUnknown(text.Language.UnmappedProperties, $"{path}.language", errors);
        if (text.Api.AllowedHosts.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{path}.api.allowed_hosts must not contain blank hosts.");
        }

        if (text.Http.MaxResponseBufferBytes < 0)
        {
            errors.Add($"{path}.http.max_response_buffer_bytes must not be negative.");
        }

        if (text.Language.Aliases.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
        {
            errors.Add($"{path}.language.aliases must map names to codes.");
        }

        ValidateTextTrackSearch(text, path, errors);
        ValidateTextTrackDownload(text, path, errors);
        ValidateTextTrackErrors(text, path, errors);
        foreach (var key in text.Messages.Keys.Where(key => !TextTrackMessages.All.Contains(key)))
        {
            errors.Add($"{path}.messages['{key}'] is not a message the adapter reads.");
        }
    }

    private static void ValidateTextTrackSearch(TextTrackConfiguration text, string path, List<string> errors)
    {
        var search = text.Search;
        var searchPath = $"{path}.search";
        AddUnknown(search.UnmappedProperties, searchPath, errors);
        foreach (var need in search.Needs.Where(need => !TextTrackLookupValues.Contains(need)))
        {
            errors.Add($"{searchPath}.needs names unknown lookup value '{need}'.");
        }

        if (search.Requests.Count == 0)
        {
            errors.Add($"{searchPath}.requests must list at least one request.");
        }

        for (var index = 0; index < search.Requests.Count; index++)
        {
            ValidateTextTrackRequest(search.Requests[index], $"{searchPath}.requests[{index}]", errors);
        }

        if (search.EmptySearchMinutes < 0)
        {
            errors.Add($"{searchPath}.empty_search_minutes must not be negative.");
        }

        if (text.Kind.Equals(TextTrackKinds.Lyrics, StringComparison.OrdinalIgnoreCase))
        {
            ValidateLyrics(search.Lyrics, $"{searchPath}.lyrics", errors);
            return;
        }

        AddRequired(errors, search.TitlesPath, $"{searchPath}.titles_path");
        AddRequired(errors, search.ItemsPath, $"{searchPath}.items_path");
        if (search.MaxItems < 1 || search.MaxCandidates < 1)
        {
            errors.Add($"{searchPath}.max_items and max_candidates must be at least 1.");
        }

        if (search.AmbiguityMargin < 0)
        {
            errors.Add($"{searchPath}.ambiguity_margin must not be negative.");
        }

        AddUnknown(search.Score.UnmappedProperties, $"{searchPath}.score", errors);
        if (search.SourceUrl is not null
            && Placeholder.Matches(search.SourceUrl).Any(match => match.Groups[1].Value != "id"))
        {
            errors.Add($"{searchPath}.source_url may only use {{id}}.");
        }

        var item = search.Item;
        AddUnknown(item.UnmappedProperties, $"{searchPath}.item", errors);
        if (item.IdPaths.Count == 0 || item.LanguagePaths.Count == 0)
        {
            errors.Add($"{searchPath}.item.id_paths and language_paths must list at least one path.");
        }

        AddRequired(errors, item.NamePath, $"{searchPath}.item.name_path");
        if (item.EpisodeMatch is { } episode)
        {
            AddUnknown(episode.UnmappedProperties, $"{searchPath}.item.episode_match", errors);
            AddRequired(errors, episode.SeasonPath, $"{searchPath}.item.episode_match.season_path");
            AddRequired(errors, episode.EpisodePath, $"{searchPath}.item.episode_match.episode_path");
        }

        if (item.Files is { } files)
        {
            AddUnknown(files.UnmappedProperties, $"{searchPath}.item.files", errors);
            AddRequired(errors, files.Path, $"{searchPath}.item.files.path");
            AddRequired(errors, files.NamePath, $"{searchPath}.item.files.name_path");
            AddRequired(errors, files.UrlPath, $"{searchPath}.item.files.url_path");
            if (files.Max < 1 || files.IdPaths.Count == 0)
            {
                errors.Add($"{searchPath}.item.files needs max of at least 1 and at least one id path.");
            }
        }
    }

    private static void ValidateTextTrackRequest(TextTrackRequestConfiguration request, string path, List<string> errors)
    {
        AddUnknown(request.UnmappedProperties, path, errors);
        if (!string.IsNullOrEmpty(request.WhenMediaType) && !Enum.TryParse<MediaType>(request.WhenMediaType, true, out _))
        {
            errors.Add($"{path}.when_media_type '{request.WhenMediaType}' is not a media type.");
        }

        if (!request.Path.StartsWith('/'))
        {
            errors.Add($"{path}.path must start with '/'.");
        }

        foreach (var need in request.Needs.Where(need => !TextTrackLookupValues.Contains(need)))
        {
            errors.Add($"{path}.needs names unknown lookup value '{need}'.");
        }

        foreach (var pair in request.Query)
        {
            if (pair.Count != 2 || string.IsNullOrWhiteSpace(pair[0]))
            {
                errors.Add($"{path}.query entries must be [name, value] pairs.");
                continue;
            }

            foreach (Match match in Placeholder.Matches(pair[1]).Where(match => !TextTrackLookupValues.Contains(match.Groups[1].Value)))
            {
                errors.Add($"{path}.query uses {{{match.Groups[1].Value}}}, which is not a lookup value.");
            }
        }

        if (request.TitleCheck is { } check)
        {
            AddUnknown(check.UnmappedProperties, $"{path}.title_check", errors);
            AddRequired(errors, check.Type, $"{path}.title_check.type");
            AddRequired(errors, check.IdPath, $"{path}.title_check.id_path");
            AddRequired(errors, check.Id, $"{path}.title_check.id");
            foreach (Match match in Placeholder.Matches(check.Id).Where(match => !TextTrackLookupValues.Contains(match.Groups[1].Value)))
            {
                errors.Add($"{path}.title_check.id uses {{{match.Groups[1].Value}}}, which is not a lookup value.");
            }
        }
    }

    private static void ValidateLyrics(TextTrackLyricsConfiguration lyrics, string path, List<string> errors)
    {
        AddUnknown(lyrics.UnmappedProperties, path, errors);
        if (lyrics.Sources.Count == 0)
        {
            errors.Add($"{path}.sources must list at least one source.");
        }

        foreach (var source in lyrics.Sources)
        {
            AddUnknown(source.UnmappedProperties, $"{path}.sources[]", errors);
            AddRequired(errors, source.Path, $"{path}.sources[].path");
            AddRequired(errors, source.Format, $"{path}.sources[].format");
        }

        var duration = lyrics.Duration;
        AddUnknown(duration.UnmappedProperties, $"{path}.duration", errors);
        if (!string.IsNullOrEmpty(duration.Path)
            && (duration.RejectBeyondSeconds <= 0 || duration.FullWithinSeconds < 0
                || duration.FullWithinSeconds > duration.RejectBeyondSeconds))
        {
            errors.Add($"{path}.duration needs 0 <= full_within_seconds <= reject_beyond_seconds and reject_beyond_seconds > 0.");
        }
    }

    private static void ValidateTextTrackDownload(TextTrackConfiguration text, string path, List<string> errors)
    {
        var download = text.Download;
        var downloadPath = $"{path}.download";
        AddUnknown(download.UnmappedProperties, downloadPath, errors);
        if (!Allowed(download.Mode, TextTrackDownloadModes.Payload, TextTrackDownloadModes.Fetch))
        {
            errors.Add($"{downloadPath}.mode must be payload or fetch.");
            return;
        }

        if (download.Mode != TextTrackDownloadModes.Fetch)
        {
            return;
        }

        if (download.AllowedHosts.Count == 0 || download.AllowedHosts.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{downloadPath}.allowed_hosts must list at least one host.");
        }

        if (download.MaxBytes <= 0)
        {
            errors.Add($"{downloadPath}.max_bytes must be greater than 0.");
        }

        if (download.Formats.Count == 0)
        {
            errors.Add($"{downloadPath}.formats must list at least one format.");
        }

        AddRequired(errors, download.OutputFormat, $"{downloadPath}.output_format");
        if ((download.RelativeBase is null) != (download.PathPrefix is null) && download.RelativeBase is not null)
        {
            errors.Add($"{downloadPath}.relative_base needs a path_prefix.");
        }

        if (download.PathPrefix is not null && !download.PathPrefix.StartsWith('/'))
        {
            errors.Add($"{downloadPath}.path_prefix must start with '/'.");
        }

        if (download.Resolve is { } resolve)
        {
            AddUnknown(resolve.UnmappedProperties, $"{downloadPath}.resolve", errors);
            if (!resolve.Path.StartsWith('/') || !resolve.Path.Contains("{id}", StringComparison.Ordinal))
            {
                errors.Add($"{downloadPath}.resolve.path must start with '/' and contain {{id}}.");
            }

            if (resolve.UrlPaths.Count == 0)
            {
                errors.Add($"{downloadPath}.resolve.url_paths must list at least one path.");
            }
        }

        if (download.Archive is { } archive)
        {
            AddUnknown(archive.UnmappedProperties, $"{downloadPath}.archive", errors);
            if (archive.MaxEntries < 1 || archive.MaxExpansionRatio < 1)
            {
                errors.Add($"{downloadPath}.archive.max_entries and max_expansion_ratio must be at least 1.");
            }
        }
    }

    private static void ValidateTextTrackErrors(TextTrackConfiguration text, string path, List<string> errors)
    {
        var failures = text.Errors;
        var errorsPath = $"{path}.errors";
        AddUnknown(failures.UnmappedProperties, errorsPath, errors);
        if (!TextTrackStatuses.Contains(failures.DefaultStatus))
        {
            errors.Add($"{errorsPath}.default_status must be one of {string.Join(", ", TextTrackStatuses)}.");
        }

        foreach (var entry in failures.Statuses)
        {
            AddUnknown(entry.UnmappedProperties, $"{errorsPath}.statuses[]", errors);
            if (!TextTrackStatuses.Contains(entry.Status))
            {
                errors.Add($"{errorsPath}.statuses[].status must be one of {string.Join(", ", TextTrackStatuses)}.");
            }

            if (entry.Codes.Count == 0 && entry.MinCode is null)
            {
                errors.Add($"{errorsPath}.statuses[] needs codes or min_code.");
            }

            if (entry.ErrorCode is not null && string.IsNullOrEmpty(failures.ErrorCodePath))
            {
                errors.Add($"{errorsPath}.statuses[].error_code needs error_code_path.");
            }
        }

        if (failures.NotOutageCodes.Any(code => code is < 100 or > 599))
        {
            errors.Add($"{errorsPath}.not_outage_codes must be HTTP status codes.");
        }
    }
}
