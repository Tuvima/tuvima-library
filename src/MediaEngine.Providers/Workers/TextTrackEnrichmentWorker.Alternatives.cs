using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Providers;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Workers;

/// <summary>One result a source offers for an item's lyrics or subtitles (Plugin architecture spec C24).</summary>
/// <param name="Preview">The first lines of the text, when the source could be asked for it.</param>
/// <param name="IsCurrent">True for the result the Library is using now.</param>
public sealed record TextTrackAlternative(
    string Provider,
    string ProviderName,
    string SourceId,
    string Language,
    bool IsTimed,
    double Confidence,
    double? DurationMatchScore,
    bool IsHearingImpaired,
    string? ReleaseName,
    string? Preview,
    bool IsCurrent);

/// <param name="Status">
/// <c>Found</c>, <c>NoneFound</c>, <c>NoSourceOn</c>, <c>NotAllowed</c>, <c>Blocked</c> (the library never looks anything up),
/// <c>Unavailable</c> (a source could not be reached) or <c>AssetMissing</c> / <c>Unsupported</c>. For subtitles it can also be
/// <c>IdentityUnverified</c> (the movie or episode cannot be identified for an online search), <c>SourceKeyRequired</c> (a source is
/// switched on but its key is missing or was refused), <c>LimitReached</c> (a source's allowance or rate limit is used up) or
/// <c>LanguageUnknown</c> (the language asked for cannot be searched).
/// </param>
/// <param name="SourceName">The display name of the source the status is about, when there is one.</param>
/// <param name="Language">The language code that was searched, for subtitles.</param>
public sealed record TextTrackAlternativesResult(
    string Status,
    IReadOnlyList<TextTrackAlternative> Items,
    string Message,
    string? SourceName = null,
    string? Language = null);

/// <summary>
/// "Not right? Find another" (Plugin architecture spec C24). The list is asked of the sources live and never stored. The Library keeps the
/// current track and the one before it, remembers results marked wrong, never picks those again, and never touches a person's own file.
/// </summary>
public sealed partial class TextTrackEnrichmentWorker
{
    private const int MaxAlternatives = 5;
    private const int PreviewLines = 2;

    public async Task<TextTrackAlternativesResult> FindAlternativesAsync(
        Guid assetId,
        TextTrackKind kind,
        PluginAudience? requester,
        CancellationToken ct = default,
        string? language = null)
    {
        var session = await OpenSessionAsync(assetId, kind, requester, language, ct).ConfigureAwait(false);
        if (session.Failure is not null)
        {
            return session.Failure;
        }

        string? searchLanguage = null;
        var providers = session.Providers;
        if (kind == TextTrackKind.Subtitles)
        {
            // The movie or episode must be identified before an online search can say anything (same rule as the refresh).
            if (SubtitleIdentityMissing(session.Lookup!, session.MediaType))
            {
                return new("IdentityUnverified", [], "The movie or episode identity could not be verified for an online subtitle search.");
            }

            // A language the source cannot be asked for is reported, never searched as "unknown".
            var searchable = new List<(ITextTrackProvider Provider, string Code)>();
            foreach (var provider in providers)
            {
                if (provider.NormalizeSearchLanguage(session.Lookup!.Language) is { } code)
                {
                    searchable.Add((provider, code));
                }
            }

            if (searchable.Count == 0)
            {
                return new("LanguageUnknown", [], "That language cannot be searched.", providers.FirstOrDefault()?.DisplayName);
            }

            providers = searchable.Select(entry => entry.Provider).ToList();
            searchLanguage = searchable[0].Code;
        }

        var current = await _trackRepo.GetPreferredAsync(assetId, kind, null, ct).ConfigureAwait(false);
        var rejected = (await _trackRepo.GetRejectionsAsync(assetId, kind, ct).ConfigureAwait(false))
            .Select(entry => (entry.Provider, entry.SourceId))
            .ToHashSet();
        var items = new List<TextTrackAlternative>();
        string? unreachable = null;
        TextTrackAlternativesResult? failed = null;
        foreach (var provider in providers)
        {
            IReadOnlyList<TextTrackCandidate> candidates;
            try
            {
                candidates = await provider.SearchAsync(session.Lookup!, ct).ConfigureAwait(false);
            }
            catch (TextTrackLookupException ex)
            {
                unreachable = ex.Message;
                failed ??= new(
                    kind == TextTrackKind.Subtitles ? StatusFor(ex) : "Unavailable", [], ex.Message, provider.DisplayName, searchLanguage);
                continue;
            }

            foreach (var candidate in candidates
                         .Where(candidate => !candidate.IsInstrumental && !rejected.Contains((candidate.Provider, candidate.SourceId)))
                         .OrderByDescending(candidate => candidate.DurationMatchScore ?? 0)
                         .ThenByDescending(candidate => candidate.Confidence)
                         .Take(MaxAlternatives))
            {
                // A subtitle list never downloads anything: a download happens only when one is chosen. Lyrics keep their preview.
                var download = kind == TextTrackKind.Subtitles
                    ? null
                    : await TryDownloadAsync(provider, candidate, ct).ConfigureAwait(false);
                items.Add(new TextTrackAlternative(
                    candidate.Provider,
                    provider.DisplayName,
                    candidate.SourceId,
                    candidate.Language,
                    kind == TextTrackKind.Subtitles
                        || (download is not null && !string.Equals(download.SourceFormat, "txt", StringComparison.OrdinalIgnoreCase)),
                    candidate.Confidence,
                    candidate.DurationMatchScore,
                    candidate.IsHearingImpaired,
                    candidate.ReleaseName,
                    download is null ? null : Preview(download),
                    current is not null
                        && string.Equals(current.Provider, candidate.Provider, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(current.SourceId, candidate.SourceId, StringComparison.OrdinalIgnoreCase)));
            }
        }

        if (items.Count > 0)
        {
            return new("Found", items.Take(MaxAlternatives).ToList(), $"{items.Count} result(s) found.",
                kind == TextTrackKind.Subtitles ? SourceNameOf(items) ?? providers.FirstOrDefault()?.DisplayName : null, searchLanguage);
        }

        if (failed is not null)
        {
            return failed;
        }

        return unreachable is not null
            ? new("Unavailable", [], unreachable)
            : new("NoneFound", [], "No other results were found.",
                kind == TextTrackKind.Subtitles ? providers.FirstOrDefault()?.DisplayName : null, searchLanguage);
    }

    private static string? SourceNameOf(IReadOnlyList<TextTrackAlternative> items) =>
        items.Select(item => item.ProviderName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? items[0].ProviderName : null;

    /// <summary>What a source's refusal means to the person looking at the list.</summary>
    private static string StatusFor(TextTrackLookupException ex) => ex.Status switch
    {
        "AuthenticationRequired" => "SourceKeyRequired",
        "QuotaExceeded" or "RateLimited" => "LimitReached",
        _ => "Unavailable",
    };

    /// <summary>The same rule as the refresh: a movie or an episode must be identified before an online subtitle search.</summary>
    private static bool SubtitleIdentityMissing(TextTrackLookup lookup, MediaType mediaType) =>
        lookup.SubtitleContext is null
        || (mediaType == MediaType.TV && lookup.SubtitleContext.TmdbEpisode is null)
        || (mediaType == MediaType.Movies && lookup.SubtitleContext.Movie is null);

    /// <summary>
    /// Whether a source is switched on for this item and this person, answered from settings alone: no source is contacted and the movie or
    /// episode is not looked up. <c>Ready</c>, <c>NoSourceOn</c>, <c>SourceKeyRequired</c>, <c>Blocked</c>, <c>NotAllowed</c>,
    /// <c>Unsupported</c> or <c>AssetMissing</c>.
    /// </summary>
    public async Task<string> GetSourceStatusAsync(Guid assetId, TextTrackKind kind, PluginAudience? requester, CancellationToken ct = default)
    {
        var session = await CheckSourcesAsync(assetId, kind, requester, ct).ConfigureAwait(false);
        // A source that is switched on but temporarily down is still switched on.
        return session.Failure is null || session.Failure.Status == "Unavailable" ? "Ready" : session.Failure.Status;
    }

    /// <summary>Uses the result a person picked: it becomes the item's choice (the one before it is kept for "Go back"), and write-back replaces the one file beside the song when it is on.</summary>
    public async Task<TextTrackEnrichmentResult> ChooseAlternativeAsync(
        Guid assetId,
        TextTrackKind kind,
        string provider,
        string sourceId,
        PluginAudience? requester,
        CancellationToken ct = default,
        string? language = null)
    {
        var session = await OpenSessionAsync(assetId, kind, requester, language, ct).ConfigureAwait(false);
        if (session.Failure is not null)
        {
            return new(session.Failure.Status, kind, 0, 0, null, session.Failure.Message);
        }

        var before = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
        foreach (var source in session.Providers.Where(source => string.Equals(source.Name, provider, StringComparison.OrdinalIgnoreCase)))
        {
            IReadOnlyList<TextTrackCandidate> candidates;
            try
            {
                candidates = await source.SearchAsync(session.Lookup!, ct).ConfigureAwait(false);
            }
            catch (TextTrackLookupException ex)
            {
                return new("Unavailable", kind, before.Count, before.Count, null, ex.Message);
            }

            var chosen = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
            {
                break;
            }

            var download = await TryDownloadAsync(source, chosen, ct).ConfigureAwait(false);
            var saved = download is null ? null : await SaveDownloadAsync(session.Asset!, download, ct).ConfigureAwait(false);
            if (saved is null)
            {
                return new("Unavailable", kind, before.Count, before.Count, null, "The result could not be downloaded.");
            }

            await _trackRepo.SetPreferredAsync(saved.Id, ct).ConfigureAwait(false);
            await AfterChoiceAsync(session.Asset!, saved.Id, kind, ct).ConfigureAwait(false);
            var after = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
            return new("Updated", kind, before.Count, after.Count, saved.Id, $"Now using the {source.DisplayName} result.");
        }

        return new("NotFound", kind, before.Count, before.Count, null, "That result is no longer offered by the source.");
    }

    /// <summary>
    /// "This one is wrong": the result is never picked again for this item. If it was the one in use, the one before it comes back, or
    /// nothing is used until the sources are asked again. A person's own file cannot be marked wrong.
    /// </summary>
    public async Task<TextTrackEnrichmentResult> MarkWrongAsync(Guid assetId, Guid trackId, CancellationToken ct = default)
    {
        var track = await _trackRepo.FindByIdAsync(trackId, ct).ConfigureAwait(false);
        if (track is null || track.AssetId != assetId)
        {
            return new("NotFound", TextTrackKind.Lyrics, 0, 0, null, "Text track not found for this asset.");
        }

        var all = await _trackRepo.GetByAssetAsync(assetId, track.Kind, ct).ConfigureAwait(false);
        if (track.IsUserOwned)
        {
            return new("PreservedUserOwned", track.Kind, all.Count, all.Count, track.Id, "A person's own file is never marked wrong or removed.");
        }

        if (!string.IsNullOrWhiteSpace(track.SourceId))
        {
            await _trackRepo.AddRejectionAsync(assetId, track.Kind, track.Provider, track.SourceId, ct).ConfigureAwait(false);
        }

        var wasPreferred = track.IsPreferred;
        await RemoveWrittenFileAsync(await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false), track, ct).ConfigureAwait(false);
        RemoveFileQuietly(track.LocalPath);
        await _trackRepo.DeleteAsync(track.Id, ct).ConfigureAwait(false);

        Guid? next = null;
        if (wasPreferred)
        {
            // The one before it (the most recently set aside) comes back; a person's own file is preferred over it when there is one.
            var remaining = (await _trackRepo.GetByAssetAsync(assetId, track.Kind, ct).ConfigureAwait(false))
                .Where(other => string.Equals(other.Language, track.Language, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(other => other.IsUserOwned)
                .ThenByDescending(other => other.UpdatedAt ?? other.CreatedAt)
                .FirstOrDefault();
            if (remaining is not null)
            {
                await _trackRepo.SetPreferredAsync(remaining.Id, ct).ConfigureAwait(false);
                next = remaining.Id;
                if (await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false) is { } asset)
                {
                    await AfterChoiceAsync(asset, remaining.Id, track.Kind, ct).ConfigureAwait(false);
                }
            }
        }

        var after = await _trackRepo.GetByAssetAsync(assetId, track.Kind, ct).ConfigureAwait(false);
        return new("Updated", track.Kind, all.Count, after.Count, next, "That result will not be picked again for this item.");
    }

    /// <summary>Keeps the current track and the one before it, and (lyrics) replaces the file beside the song when write-back allows.</summary>
    private async Task AfterChoiceAsync(MediaAsset asset, Guid chosenId, TextTrackKind kind, CancellationToken ct)
    {
        var chosen = await _trackRepo.FindByIdAsync(chosenId, ct).ConfigureAwait(false);
        if (chosen is null)
        {
            return;
        }

        var older = (await _trackRepo.GetByAssetAsync(asset.Id, kind, ct).ConfigureAwait(false))
            .Where(other => other.Id != chosen.Id
                && !other.IsUserOwned
                && string.Equals(other.Language, chosen.Language, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(other => other.UpdatedAt ?? other.CreatedAt)
            .Skip(1)
            .ToList();
        foreach (var stale in older)
        {
            await RemoveWrittenFileAsync(asset, stale, ct).ConfigureAwait(false);
            RemoveFileQuietly(stale.LocalPath);
            await _trackRepo.DeleteAsync(stale.Id, ct).ConfigureAwait(false);
        }

        if (kind == TextTrackKind.Lyrics && _textTrackExportService is not null)
        {
            await ExportPreferredLyricsAsync(asset, chosen, ct).ConfigureAwait(false);
        }
    }

    private async Task<TextTrackDownload?> TryDownloadAsync(ITextTrackProvider provider, TextTrackCandidate candidate, CancellationToken ct)
    {
        try
        {
            return await provider.DownloadAsync(candidate, ct).ConfigureAwait(false);
        }
        catch (TextTrackLookupException ex)
        {
            _logger.LogDebug(ex, "Could not fetch a {Provider} result for a preview", provider.Name);
            return null;
        }
    }

    private static string? Preview(TextTrackDownload download)
    {
        var lines = download.Content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^(\[[^\]]*\])+\s*", string.Empty))
            .Where(line => line.Length > 0 && !string.Equals(line, "WEBVTT", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("-->", StringComparison.Ordinal) && !int.TryParse(line, out _))
            .Take(PreviewLines)
            .ToList();
        return lines.Count == 0 ? null : string.Join(" / ", lines);
    }

    /// <summary>Removes the lyrics file the Library wrote beside the song, only through the export service's write-back and source rules.</summary>
    private async Task RemoveWrittenFileAsync(MediaAsset? asset, TextTrack track, CancellationToken ct)
    {
        if (asset is not null && track.Kind == TextTrackKind.Lyrics && _textTrackExportService is not null)
        {
            await _textTrackExportService.RemoveWrittenLyricsAsync(asset, track, ct).ConfigureAwait(false);
        }
    }

    private void RemoveFileQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be removed now is only clutter; the Library no longer lists it.
            _logger.LogDebug(ex, "Could not remove {Path}", path);
        }
    }

    private sealed record Session(
        MediaAsset? Asset,
        WorkLineage? Lineage,
        MediaType MediaType,
        TextTrackLookup? Lookup,
        IReadOnlyList<ITextTrackProvider> Providers,
        TextTrackAlternativesResult? Failure);

    /// <summary>
    /// Works out which sources may be asked for this item and by this person, with the same rules as an ordinary lookup, and builds the lookup.
    /// <paramref name="language"/> (a name or a code) replaces the title's own language for a subtitle search.
    /// </summary>
    private async Task<Session> OpenSessionAsync(Guid assetId, TextTrackKind kind, PluginAudience? requester, string? language, CancellationToken ct)
    {
        var session = await CheckSourcesAsync(assetId, kind, requester, ct).ConfigureAwait(false);
        if (session.Failure is not null)
        {
            return session;
        }

        var lookup = await BuildLookupAsync(session.Asset!, session.Lineage, session.MediaType, kind, ct).ConfigureAwait(false);
        if (kind == TextTrackKind.Subtitles && !string.IsNullOrWhiteSpace(language))
        {
            lookup = lookup with { Language = language.Trim() };
        }

        return session with { Lookup = lookup };
    }

    /// <summary>The checks of <see cref="OpenSessionAsync"/> that need no lookup of the title and contact no source.</summary>
    private async Task<Session> CheckSourcesAsync(Guid assetId, TextTrackKind kind, PluginAudience? requester, CancellationToken ct)
    {
        static Session Fail(string status, string message, string? sourceName = null) =>
            new(null, null, MediaType.Unknown, null, [], new(status, [], message, sourceName));

        var asset = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return Fail("AssetMissing", "The owned media file could not be found.");
        }

        var lineage = await _workRepo.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        var mediaType = lineage?.MediaType ?? InferMediaType(asset.FilePathRoot);
        if (!IsRelevant(kind, mediaType))
        {
            return Fail("Unsupported", $"{kind} are not supported for {mediaType}.");
        }

        if (BypassesExternalProviders(asset))
        {
            return Fail("Blocked", "This library never looks anything up.");
        }

        var matching = _providers.Where(provider => provider.Kind == kind && provider.CanHandle(mediaType)).ToList();
        var available = matching.Where(provider => provider.GetAvailability(mediaType).IsAvailable).ToList();
        if (available.Count == 0)
        {
            if (kind == TextTrackKind.Subtitles)
            {
                // Subtitles say why: a missing or refused key, or a source that is switched on but down (lyrics keep "no source").
                ITextTrackProvider? Match(string status) =>
                    matching.FirstOrDefault(provider => string.Equals(provider.GetAvailability(mediaType).Status, status, StringComparison.OrdinalIgnoreCase));
                // A profile that may not use a source is not told about that source's key or health.
                if (_accessGate is not null && matching.Count > 0)
                {
                    var anyAllowed = false;
                    foreach (var provider in matching)
                    {
                        if (await _accessGate.CheckAsync(new PluginAccessQuery(provider.Name, requester, asset.LibraryId, asset.Id), ct).ConfigureAwait(false) == PluginAccessOutcome.Allowed)
                        {
                            anyAllowed = true;
                            break;
                        }
                    }

                    if (!anyAllowed)
                    {
                        return Fail("NotAllowed", $"No {kind.ToString().ToLowerInvariant()} source can be used for this item.");
                    }
                }

                if (Match("AuthenticationRequired") is { } keyless)
                {
                    return Fail("SourceKeyRequired", $"{keyless.DisplayName} needs an API key.", keyless.DisplayName);
                }

                if (Match("ProviderUnavailable") is { } down)
                {
                    return Fail("Unavailable", $"{down.DisplayName} is temporarily unavailable.", down.DisplayName);
                }
            }

            return Fail("NoSourceOn", $"No {kind.ToString().ToLowerInvariant()} source is switched on.");
        }

        if (_accessGate is not null)
        {
            var permitted = new List<ITextTrackProvider>();
            foreach (var provider in available)
            {
                var access = await _accessGate.CheckAsync(new PluginAccessQuery(provider.Name, requester, asset.LibraryId, asset.Id), ct).ConfigureAwait(false);
                if (access == PluginAccessOutcome.Allowed)
                {
                    permitted.Add(provider);
                }
            }

            if (permitted.Count == 0)
            {
                return Fail("NotAllowed", $"No {kind.ToString().ToLowerInvariant()} source can be used for this item.");
            }

            available = permitted;
        }

        return new(asset, lineage, mediaType, null, available, null);
    }
}
