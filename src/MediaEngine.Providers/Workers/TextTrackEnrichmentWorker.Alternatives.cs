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
/// <c>Unavailable</c> (a source could not be reached) or <c>AssetMissing</c> / <c>Unsupported</c>.
/// </param>
public sealed record TextTrackAlternativesResult(string Status, IReadOnlyList<TextTrackAlternative> Items, string Message);

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
        CancellationToken ct = default)
    {
        var session = await OpenSessionAsync(assetId, kind, requester, ct).ConfigureAwait(false);
        if (session.Failure is not null)
        {
            return session.Failure;
        }

        var current = await _trackRepo.GetPreferredAsync(assetId, kind, null, ct).ConfigureAwait(false);
        var rejected = (await _trackRepo.GetRejectionsAsync(assetId, kind, ct).ConfigureAwait(false))
            .Select(entry => (entry.Provider, entry.SourceId))
            .ToHashSet();
        var items = new List<TextTrackAlternative>();
        string? unreachable = null;
        foreach (var provider in session.Providers)
        {
            IReadOnlyList<TextTrackCandidate> candidates;
            try
            {
                candidates = await provider.SearchAsync(session.Lookup!, ct).ConfigureAwait(false);
            }
            catch (TextTrackLookupException ex)
            {
                unreachable = ex.Message;
                continue;
            }

            foreach (var candidate in candidates
                         .Where(candidate => !candidate.IsInstrumental && !rejected.Contains((candidate.Provider, candidate.SourceId)))
                         .OrderByDescending(candidate => candidate.DurationMatchScore ?? 0)
                         .ThenByDescending(candidate => candidate.Confidence)
                         .Take(MaxAlternatives))
            {
                var download = await TryDownloadAsync(provider, candidate, ct).ConfigureAwait(false);
                items.Add(new TextTrackAlternative(
                    candidate.Provider,
                    provider.DisplayName,
                    candidate.SourceId,
                    candidate.Language,
                    download is not null && !string.Equals(download.SourceFormat, "txt", StringComparison.OrdinalIgnoreCase),
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
            return new("Found", items.Take(MaxAlternatives).ToList(), $"{items.Count} result(s) found.");
        }

        return unreachable is not null
            ? new("Unavailable", [], unreachable)
            : new("NoneFound", [], "No other results were found.");
    }

    /// <summary>Uses the result a person picked: it becomes the item's choice (the one before it is kept for "Go back"), and write-back replaces the one file beside the song when it is on.</summary>
    public async Task<TextTrackEnrichmentResult> ChooseAlternativeAsync(
        Guid assetId,
        TextTrackKind kind,
        string provider,
        string sourceId,
        PluginAudience? requester,
        CancellationToken ct = default)
    {
        var session = await OpenSessionAsync(assetId, kind, requester, ct).ConfigureAwait(false);
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
        TextTrackLookup? Lookup,
        IReadOnlyList<ITextTrackProvider> Providers,
        TextTrackAlternativesResult? Failure);

    /// <summary>Works out which sources may be asked for this item and by this person, with the same rules as an ordinary lookup.</summary>
    private async Task<Session> OpenSessionAsync(Guid assetId, TextTrackKind kind, PluginAudience? requester, CancellationToken ct)
    {
        static Session Fail(string status, string message) => new(null, null, [], new(status, [], message));

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

        var available = _providers
            .Where(provider => provider.Kind == kind && provider.CanHandle(mediaType) && provider.GetAvailability(mediaType).IsAvailable)
            .ToList();
        if (available.Count == 0)
        {
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

        var lookup = await BuildLookupAsync(asset, lineage, mediaType, kind, ct).ConfigureAwait(false);
        return new(asset, lookup, available, null);
    }
}
