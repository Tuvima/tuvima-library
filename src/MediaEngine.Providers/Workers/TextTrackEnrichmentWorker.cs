using System.Globalization;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Workers;

public sealed record TextTrackEnrichmentResult(
    string Status,
    TextTrackKind Kind,
    int BeforeCount,
    int AfterCount,
    Guid? TrackId,
    string Message);

public sealed class TextTrackEnrichmentWorker
{
    private readonly IMediaAssetRepository _assetRepo;
    private readonly IWorkRepository _workRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IBridgeIdRepository _bridgeRepo;
    private readonly ITextTrackRepository _trackRepo;
    private readonly IEnumerable<ITextTrackProvider> _providers;
    private readonly AssetPathService _assetPaths;
    private readonly ILogger<TextTrackEnrichmentWorker> _logger;
    private readonly ITextTrackExportService? _textTrackExportService;
    private readonly IConfigurationLoader? _configurationLoader;
    private readonly ITvEpisodeCrosswalk? _episodeCrosswalk;

    public TextTrackEnrichmentWorker(
        IMediaAssetRepository assetRepo,
        IWorkRepository workRepo,
        ICanonicalValueRepository canonicalRepo,
        IBridgeIdRepository bridgeRepo,
        ITextTrackRepository trackRepo,
        IEnumerable<ITextTrackProvider> providers,
        AssetPathService assetPaths,
        ILogger<TextTrackEnrichmentWorker> logger,
        ITextTrackExportService? textTrackExportService = null,
        IConfigurationLoader? configurationLoader = null,
        ITvEpisodeCrosswalk? episodeCrosswalk = null)
    {
        _assetRepo = assetRepo;
        _workRepo = workRepo;
        _canonicalRepo = canonicalRepo;
        _bridgeRepo = bridgeRepo;
        _trackRepo = trackRepo;
        _providers = providers;
        _assetPaths = assetPaths;
        _logger = logger;
        _textTrackExportService = textTrackExportService;
        _configurationLoader = configurationLoader;
        _episodeCrosswalk = episodeCrosswalk;
    }

    public async Task<TextTrackEnrichmentResult> EnrichAsync(Guid assetId, TextTrackKind kind, CancellationToken ct = default)
    {
        var asset = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            _logger.LogDebug("Skipping {Kind} enrichment; asset {AssetId} was not found", kind, assetId);
            return new("AssetMissing", kind, 0, 0, null, "The owned media file could not be found.");
        }

        var lineage = await _workRepo.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        var mediaType = lineage?.MediaType ?? InferMediaType(asset.FilePathRoot);
        if (!IsRelevant(kind, mediaType))
        {
            return new("Unsupported", kind, 0, 0, null, $"{kind} are not supported for {mediaType}.");
        }

        var before = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);

        var importedLocal = await ImportLocalSidecarsAsync(asset, kind, ct).ConfigureAwait(false);

        var existingPreferred = await _trackRepo.GetPreferredAsync(asset.Id, kind, null, ct).ConfigureAwait(false);
        if (existingPreferred?.IsUserOwned == true)
        {
            var afterLocal = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
            return new(
                importedLocal ? "Updated" : "PreservedUserOwned",
                kind,
                before.Count,
                afterLocal.Count,
                existingPreferred.Id,
                importedLocal
                    ? "Local text tracks were refreshed and the user-owned choice was preserved."
                    : "The user-owned preferred text track was preserved.");
        }

        if (BypassesExternalProviders(asset))
        {
            var afterLocal = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
            return new(
                importedLocal ? "Updated" : "ExternalLookupBlocked",
                kind,
                before.Count,
                afterLocal.Count,
                afterLocal.FirstOrDefault(track => track.IsPreferred)?.Id,
                importedLocal
                    ? "Local text tracks were refreshed."
                    : "This library uses local-only or manual metadata, so no external text-track provider was contacted.");
        }

        var matchingProviders = _providers
            .Where(provider => provider.Kind == kind && provider.CanHandle(mediaType))
            .Select(provider => (Provider: provider, Availability: provider.GetAvailability(mediaType)))
            .ToList();
        if (matchingProviders.Count == 0)
        {
            var afterLocal = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
            return new(
                importedLocal ? "Updated" : "NoProviderConfigured",
                kind,
                before.Count,
                afterLocal.Count,
                afterLocal.FirstOrDefault(track => track.IsPreferred)?.Id,
                importedLocal
                    ? "Local text tracks were refreshed."
                    : $"No {kind.ToString().ToLowerInvariant()} provider is configured for {mediaType}.");
        }

        var availableProviders = matchingProviders
            .Where(candidate => candidate.Availability.IsAvailable)
            .Select(candidate => candidate.Provider)
            .ToList();
        if (availableProviders.Count == 0)
        {
            var providerState = matchingProviders
                .Select(candidate => candidate.Availability)
                .OrderBy(candidate => candidate.Status == "AuthenticationRequired" ? 0 : candidate.Status == "ProviderUnavailable" ? 1 : 2)
                .First();
            var afterLocal = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
            return new(
                importedLocal ? "Updated" : providerState.Status,
                kind,
                before.Count,
                afterLocal.Count,
                afterLocal.FirstOrDefault(track => track.IsPreferred)?.Id,
                importedLocal ? "Local text tracks were refreshed." : providerState.Message ?? "The text-track provider is unavailable.");
        }

        var lookup = await BuildLookupAsync(asset, lineage, mediaType, kind, ct).ConfigureAwait(false);
        if (kind == TextTrackKind.Subtitles &&
            (lookup.SubtitleContext is null ||
             (mediaType == MediaType.TV && lookup.SubtitleContext.TmdbEpisode is null) ||
             (mediaType == MediaType.Movies && lookup.SubtitleContext.Movie is null)))
        {
            var afterUnverified = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
            return new(importedLocal ? "Updated" : "IdentityUnverified", kind, before.Count,
                afterUnverified.Count, afterUnverified.FirstOrDefault(track => track.IsPreferred)?.Id,
                importedLocal ? "Local text tracks were refreshed." :
                    "The movie or episode identity could not be verified for an online subtitle search.");
        }
        var instrumentalDetected = false;
        var incompatibleSubtitleDetected = false;
        foreach (var provider in availableProviders)
        {
            IReadOnlyList<TextTrackCandidate> candidates;
            try
            {
                candidates = await provider.SearchAsync(lookup, ct).ConfigureAwait(false);
            }
            catch (SubdlLookupException ex)
            {
                var current = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
                return new(importedLocal ? "Updated" : ex.Status, kind, before.Count, current.Count,
                    current.FirstOrDefault(track => track.IsPreferred)?.Id,
                    importedLocal ? "Local text tracks were refreshed." : ex.Message);
            }
            instrumentalDetected |= candidates.Any(candidate => candidate.IsInstrumental);
            foreach (var candidate in candidates
                         .Where(candidate => !candidate.IsInstrumental)
                         .OrderBy(candidate => candidate.IsForced)
                         .ThenBy(candidate => candidate.IsHearingImpaired)
                         .ThenByDescending(candidate => candidate.Confidence)
                         .Take(1))
            {
                TextTrackDownload? download;
                try
                {
                    download = await provider.DownloadAsync(candidate, ct).ConfigureAwait(false);
                }
                catch (SubdlLookupException ex)
                {
                    var current = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
                    return new(importedLocal ? "Updated" : ex.Status, kind, before.Count, current.Count,
                        current.FirstOrDefault(track => track.IsPreferred)?.Id,
                        importedLocal ? "Local text tracks were refreshed." : ex.Message);
                }
                if (download is null)
                {
                    incompatibleSubtitleDetected |= provider is SubdlTextTrackProvider;
                    continue;
                }

                var saved = await SaveDownloadAsync(asset, download, ct).ConfigureAwait(false);
                if (saved is not null)
                {
                    // An existing preference may have been selected by the user, including a
                    // previously downloaded OpenSubtitles track. Keep it across provider changes.
                    var canAutoPrefer = kind != TextTrackKind.Lyrics || saved.TimingMode != "Line"
                        || candidate.DurationMatchScore is null or >= 0.8;
                    if (existingPreferred is null && canAutoPrefer)
                        await _trackRepo.SetPreferredAsync(saved.Id, ct).ConfigureAwait(false);
                    if (existingPreferred is null && kind == TextTrackKind.Subtitles
                        && _assetPaths.ShouldKeepPreferredSubtitlesLocal
                        && _textTrackExportService is not null)
                    {
                        await ExportPreferredSubtitleAsync(asset, saved, ct).ConfigureAwait(false);
                    }

                    var afterDownload = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
                    return new("Updated", kind, before.Count, afterDownload.Count,
                        existingPreferred?.Id ?? (canAutoPrefer ? saved.Id : null),
                        $"{kind} were refreshed from {candidate.Provider}.");
                }
            }
        }

        var after = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
        return new(
            importedLocal ? "Updated" : incompatibleSubtitleDetected ? "IncompatibleSubtitle" : instrumentalDetected ? "Instrumental" : "NoResult",
            kind,
            before.Count,
            after.Count,
            after.FirstOrDefault(track => track.IsPreferred)?.Id,
            importedLocal
                ? "Local text tracks were refreshed."
                : instrumentalDetected
                    ? "The provider identifies this track as instrumental, so there are no lyrics to download."
                    : incompatibleSubtitleDetected
                    ? "SubDL found a subtitle, but its file could not be safely matched or imported."
                    : $"No matching {kind.ToString().ToLowerInvariant()} were found.");
    }

    private bool BypassesExternalProviders(MediaAsset asset)
    {
        if (_configurationLoader is null || string.IsNullOrWhiteSpace(asset.LibraryId))
        {
            return false;
        }

        try
        {
            var library = _configurationLoader.LoadLibraries().Libraries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, asset.LibraryId, StringComparison.OrdinalIgnoreCase));
            return library is not null && LibraryMetadataPolicies.BypassesExternalIdentity(library.MetadataPolicy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve metadata policy for library {LibraryId}; external text-track lookup was skipped.", asset.LibraryId);
            return true;
        }
    }

    public async Task<TextTrackEnrichmentResult> ImportAsync(
        Guid assetId,
        TextTrackKind kind,
        string fileName,
        string content,
        string? language,
        CancellationToken ct = default)
    {
        var asset = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return new("AssetMissing", kind, 0, 0, null, "The owned media file could not be found.");
        }

        var lineage = await _workRepo.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        var mediaType = lineage?.MediaType ?? InferMediaType(asset.FilePathRoot);
        if (!IsRelevant(kind, mediaType))
        {
            return new("Unsupported", kind, 0, 0, null, $"{kind} are not supported for {mediaType}.");
        }

        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        var allowed = kind == TextTrackKind.Lyrics
            ? extension is "lrc" or "txt"
            : extension is "vtt" or "srt" or "ass";
        if (!allowed)
        {
            return new("UnsupportedFormat", kind, 0, 0, null, $".{extension} is not a supported {kind.ToString().ToLowerInvariant()} format.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new("InvalidContent", kind, 0, 0, null, "The selected text-track file is empty.");
        }

        var before = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
        var normalizedLanguage = string.IsNullOrWhiteSpace(language) || string.Equals(language, "und", StringComparison.OrdinalIgnoreCase)
            ? ExtractLanguage(fileName, Path.GetFileNameWithoutExtension(asset.FilePathRoot))
            : language.Trim().ToLowerInvariant();
        var normalized = kind == TextTrackKind.Subtitles
            ? SubtitleNormalizer.NormalizeToWebVtt(content, extension)
            : extension == "lrc" ? LrcParser.Normalize(content) : content.Trim();
        var normalizedFormat = kind == TextTrackKind.Subtitles ? "vtt" : extension;
        var identity = BuildTrackIdentity(assetId, kind, "user", Hashing.Sha256Hex(normalized), normalizedLanguage);
        var storageProvider = $"user-{Hashing.Sha256Hex(identity)[..12]}";
        var path = _assetPaths.GetCentralTextTrackPath(
            assetId,
            kind == TextTrackKind.Lyrics ? "Lyrics" : "Subtitles",
            storageProvider,
            normalizedLanguage,
            kind == TextTrackKind.Subtitles ? ".vtt" : $".{normalizedFormat}");
        await WriteTextAtomicallyAsync(path, normalized, ct).ConfigureAwait(false);

        var track = new TextTrack
        {
            Id = Hashing.DeterministicGuid(identity),
            AssetId = assetId,
            Kind = kind,
            Language = normalizedLanguage,
            Provider = "user",
            Confidence = 1,
            SourceId = Path.GetFileName(fileName),
            SourceFormat = extension,
            NormalizedFormat = normalizedFormat,
            LocalPath = path,
            TimingMode = kind == TextTrackKind.Lyrics && extension == "txt" ? "Plain" : kind == TextTrackKind.Lyrics ? "Line" : "Cue",
            IsPreferred = true,
            IsUserOwned = true,
        };
        await _trackRepo.UpsertAsync(track, ct).ConfigureAwait(false);
        await _trackRepo.SetPreferredAsync(track.Id, ct).ConfigureAwait(false);
        var after = await _trackRepo.GetByAssetAsync(assetId, kind, ct).ConfigureAwait(false);
        return new("Updated", kind, before.Count, after.Count, track.Id, $"Imported {Path.GetFileName(fileName)} and selected it as preferred.");
    }

    private async Task<bool> ImportLocalSidecarsAsync(MediaAsset asset, TextTrackKind kind, CancellationToken ct)
    {
        var mediaPath = asset.FilePathRoot;
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(mediaPath);
        var basename = Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(basename) || !Directory.Exists(directory))
        {
            return false;
        }

        var patterns = kind == TextTrackKind.Lyrics
            ? new[] { $"{basename}.lrc", $"{basename}.txt" }
            : new[] { $"{basename}.vtt", $"{basename}.srt", $"{basename}.*.vtt", $"{basename}.*.srt", $"{basename}.*.ass" };

        var imported = false;
        foreach (var pattern in patterns)
        {
            foreach (var path in Directory.EnumerateFiles(directory, pattern))
            {
                var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                var language = ExtractLanguage(path, basename);
                var identity = BuildTrackIdentity(asset.Id, kind, "local", path, language);
                var storageProvider = $"local-{Hashing.Sha256Hex(identity)[..12]}";
                var localPath = path;
                var normalizedFormat = extension;

                if (kind == TextTrackKind.Subtitles && extension is "srt" or "ass")
                {
                    var normalized = SubtitleNormalizer.NormalizeToWebVtt(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), extension);
                    localPath = _assetPaths.GetCentralTextTrackPath(asset.Id, "Subtitles", storageProvider, language, ".vtt");
                    await WriteTextAtomicallyAsync(localPath, normalized, ct).ConfigureAwait(false);
                    normalizedFormat = "vtt";
                }

                if (kind == TextTrackKind.Lyrics && extension == "lrc")
                {
                    var normalized = LrcParser.Normalize(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
                    localPath = _assetPaths.GetCentralTextTrackPath(asset.Id, "Lyrics", storageProvider, language, ".lrc");
                    await WriteTextAtomicallyAsync(localPath, normalized, ct).ConfigureAwait(false);
                }

                var track = new TextTrack
                {
                    Id = Hashing.DeterministicGuid(identity),
                    AssetId = asset.Id,
                    Kind = kind,
                    Language = language,
                    Provider = "local",
                    Confidence = 1,
                    SourceFormat = extension,
                    NormalizedFormat = normalizedFormat,
                    LocalPath = localPath,
                    SidecarPath = path,
                    TimingMode = kind == TextTrackKind.Lyrics && extension == "txt" ? "Plain" : kind == TextTrackKind.Lyrics ? "Line" : "Cue",
                    IsPreferred = true,
                    IsUserOwned = true,
                };
                await _trackRepo.UpsertAsync(track, ct).ConfigureAwait(false);
                await _trackRepo.SetPreferredAsync(track.Id, ct).ConfigureAwait(false);
                imported = true;
            }
        }

        return imported;
    }

    private async Task<TextTrackLookup> BuildLookupAsync(MediaAsset asset, WorkLineage? lineage, MediaType mediaType, TextTrackKind kind, CancellationToken ct)
    {
        var entityIds = new List<Guid> { asset.Id };
        if (lineage is not null)
        {
            entityIds.Add(lineage.TargetForSelfScope);
            entityIds.Add(lineage.TargetForParentScope);
        }

        var canonicalGroups = await _canonicalRepo.GetByEntitiesAsync(entityIds.Distinct().ToList(), ct).ConfigureAwait(false);
        var bridgeGroups = await _bridgeRepo.GetByEntitiesAsync(entityIds.Distinct().ToList(), ct).ConfigureAwait(false);
        var values = canonicalGroups.Values.SelectMany(v => v)
            .GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        var bridgeIds = bridgeGroups.Values.SelectMany(v => v)
            .GroupBy(v => v.IdType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().IdValue, StringComparer.OrdinalIgnoreCase);

        foreach (var key in new[] { MetadataFieldConstants.SeasonNumber, MetadataFieldConstants.EpisodeNumber })
        {
            if (values.TryGetValue(key, out var value))
            {
                bridgeIds[key] = value;
            }
        }

        SubtitleLookupContext? subtitleContext = null;
        if (kind == TextTrackKind.Subtitles && lineage is not null)
        {
            string? ScopedBridge(Guid entityId, string key)
            {
                if (!bridgeGroups.TryGetValue(entityId, out var entries)) return null;
                var distinct = entries.Where(entry => string.Equals(entry.IdType, key, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.IdValue.Trim()).Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return distinct.Count == 1 ? distinct[0] : null;
            }

            if (mediaType == MediaType.Movies)
            {
                var tmdb = ScopedBridge(lineage.WorkId, BridgeIdKeys.TmdbId);
                var imdb = ScopedBridge(lineage.WorkId, BridgeIdKeys.ImdbId);
                if (!string.IsNullOrWhiteSpace(tmdb) || !string.IsNullOrWhiteSpace(imdb))
                    subtitleContext = new SubtitleLookupContext(Movie: new MovieSubtitleIdentity(imdb, tmdb));
            }
            else if (mediaType == MediaType.TV)
            {
                var showId = ScopedBridge(lineage.RootParentWorkId, BridgeIdKeys.TvdbId);
                var episodeId = ScopedBridge(lineage.WorkId, BridgeIdKeys.TvdbEpisodeId);
                if (!string.IsNullOrWhiteSpace(showId) && !string.IsNullOrWhiteSpace(episodeId))
                {
                    var ownValues = canonicalGroups.TryGetValue(lineage.WorkId, out var own) ? own : [];
                    int? Number(string key)
                    {
                        var raw = ownValues.FirstOrDefault(value => string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
                        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
                    }
                    var tvdb = new TvdbEpisodeIdentity(showId, episodeId,
                        Number(MetadataFieldConstants.SeasonNumber), Number(MetadataFieldConstants.EpisodeNumber));
                    var verified = _episodeCrosswalk is null ? null :
                        await _episodeCrosswalk.ResolveAsync(tvdb,
                            ScopedBridge(lineage.RootParentWorkId, BridgeIdKeys.TmdbId), ct).ConfigureAwait(false);
                    subtitleContext = new SubtitleLookupContext(TvdbEpisode: tvdb, TmdbEpisode: verified);
                }
            }
        }

        return new TextTrackLookup(
            asset,
            mediaType,
            First(values, MetadataFieldConstants.Title, MetadataFieldConstants.EpisodeTitle, MetadataFieldConstants.ShowName),
            First(values, MetadataFieldConstants.Artist, MetadataFieldConstants.Author),
            First(values, MetadataFieldConstants.Album, MetadataFieldConstants.Series, MetadataFieldConstants.ShowName),
            First(values, MetadataFieldConstants.Year),
            First(values, MetadataFieldConstants.Language) ?? (kind == TextTrackKind.Subtitles ? "en" : null),
            ParseDurationSeconds(First(values, MetadataFieldConstants.DurationField, MetadataFieldConstants.Runtime)),
            bridgeIds,
            subtitleContext);
    }

    private async Task<TextTrack?> SaveDownloadAsync(MediaAsset asset, TextTrackDownload download, CancellationToken ct)
    {
        var candidate = download.Candidate;
        var plainLyrics = candidate.Kind == TextTrackKind.Lyrics
            && string.Equals(download.SourceFormat, "txt", StringComparison.OrdinalIgnoreCase);
        var normalized = candidate.Kind == TextTrackKind.Lyrics
            ? plainLyrics ? download.Content.Trim() : LrcParser.Normalize(download.Content)
            : SubtitleNormalizer.NormalizeToWebVtt(download.Content, download.SourceFormat);
        var extension = candidate.Kind == TextTrackKind.Lyrics ? plainLyrics ? ".txt" : ".lrc" : ".vtt";
        var kindName = candidate.Kind == TextTrackKind.Lyrics ? "Lyrics" : "Subtitles";
        var sourceIdentity = string.IsNullOrWhiteSpace(candidate.SourceId) ? candidate.SourceUrl : candidate.SourceId;
        var identity = BuildTrackIdentity(asset.Id, candidate.Kind, candidate.Provider, sourceIdentity, candidate.Language);
        var storageProvider = $"{candidate.Provider}-{Hashing.Sha256Hex(identity)[..12]}";
        var path = _assetPaths.GetCentralTextTrackPath(asset.Id, kindName, storageProvider, candidate.Language, extension);
        await WriteTextAtomicallyAsync(path, normalized, ct).ConfigureAwait(false);

        var track = new TextTrack
        {
            Id = Hashing.DeterministicGuid(identity),
            AssetId = asset.Id,
            Kind = candidate.Kind,
            Language = candidate.Language,
            Provider = candidate.Provider,
            Confidence = candidate.Confidence,
            SourceId = candidate.SourceId,
            SourceUrl = candidate.SourceUrl,
            SourceFormat = download.SourceFormat,
            NormalizedFormat = download.NormalizedFormat,
            LocalPath = path,
            TimingMode = candidate.Kind == TextTrackKind.Lyrics ? plainLyrics ? "Plain" : "Line" : "Cue",
            DurationMatchScore = candidate.DurationMatchScore,
            IsHearingImpaired = candidate.IsHearingImpaired,
        };

        await _trackRepo.UpsertAsync(track, ct).ConfigureAwait(false);
        return track;
    }

    internal static string BuildTrackIdentity(
        Guid assetId,
        TextTrackKind kind,
        string provider,
        string? sourceIdentity,
        string language) =>
        $"text-track|{assetId:D}|{kind}|{provider.Trim().ToLowerInvariant()}|{sourceIdentity?.Trim().ToLowerInvariant()}|{language.Trim().ToLowerInvariant()}";

    private static async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken ct)
    {
        AssetPathService.EnsureDirectory(path);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, ct).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task ExportPreferredSubtitleAsync(MediaAsset asset, TextTrack track, CancellationToken ct)
    {
        var exportPath = await _textTrackExportService!.ExportPreferredSubtitleAsync(asset, track, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(exportPath))
        {
            return;
        }

        track.SidecarPath = exportPath;
        await _trackRepo.UpsertAsync(track, ct).ConfigureAwait(false);
    }

    private static bool IsRelevant(TextTrackKind kind, MediaType mediaType) =>
        kind == TextTrackKind.Lyrics
            ? mediaType == MediaType.Music
            : mediaType is MediaType.Movies or MediaType.TV;

    private static MediaType InferMediaType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp3" or ".m4a" or ".flac" or ".wav" or ".ogg" ? MediaType.Music
            : ext is ".mp4" or ".mkv" or ".m4v" or ".webm" or ".avi" ? MediaType.Movies
            : MediaType.Unknown;
    }

    private static string ExtractLanguage(string path, string basename)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var suffix = name.StartsWith($"{basename}.", StringComparison.OrdinalIgnoreCase)
            ? name[(basename.Length + 1)..]
            : name.Contains('.')
                ? name[(name.LastIndexOf('.') + 1)..]
                : string.Empty;
        var candidate = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        return candidate is { Length: >= 2 and <= 8 } && candidate.All(character => char.IsLetter(character) || character == '-')
            ? candidate
            : "und";
    }

    private static string? First(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static double? ParseDurationSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds > 0 ? seconds : null;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span))
        {
            return span.TotalSeconds;
        }

        return null;
    }
}
