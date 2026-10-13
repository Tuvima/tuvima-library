using System.Text.Json;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Processors.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Services;

public sealed class MediaTypeResolver : IMediaTypeResolver
{
    private const double RootWatchFolderMaxConfidence = 0.40;

    private readonly IOptionsMonitor<IngestionOptions> _options;
    private readonly ILibraryFolderResolver _libraryFolderResolver;
    private readonly IMediaTypeAdvisor _typeAdvisor;
    private readonly IMediaTypeExtensionCatalog _extensionCatalog;
    private readonly IImportFolderService? _importFolders;
    private readonly ILogger<MediaTypeResolver> _logger;

    public MediaTypeResolver(
        IOptionsMonitor<IngestionOptions> options,
        ILibraryFolderResolver libraryFolderResolver,
        IMediaTypeAdvisor typeAdvisor,
        IMediaTypeExtensionCatalog extensionCatalog,
        ILogger<MediaTypeResolver> logger,
        IImportFolderService? importFolders = null)
    {
        _importFolders = importFolders;
        _options = options;
        _libraryFolderResolver = libraryFolderResolver;
        _typeAdvisor = typeAdvisor;
        _extensionCatalog = extensionCatalog;
        _logger = logger;
    }

    public async Task<MediaTypeResolution> ResolveAsync(
        string filePath,
        ProcessorResult processorResult,
        double categoryConfidencePrior,
        CancellationToken ct = default)
    {
        var options = _options.CurrentValue;
        var resolvedMediaType = processorResult.DetectedType;
        var candidateList = processorResult.MediaTypeCandidates.ToList();
        var adjustedPrior = categoryConfidencePrior;

        ApplyLibraryFolderPrior(filePath, candidateList, ref resolvedMediaType);

        await ApplyAdvisorAsync(filePath, processorResult, candidateList, options, ct)
            .ConfigureAwait(false);

        bool rootWatchFolderReview = ApplyRootWatchFolderCap(
            filePath,
            candidateList,
            options,
            ref adjustedPrior,
            out var rootWatchFolderDetail);

        bool mediaTypeIsConflicted = false;
        bool mediaTypeNeedsReview = false;

        if (candidateList.Count > 0)
        {
            candidateList = [.. candidateList.OrderByDescending(c => c.Confidence)];
            var topCandidate = candidateList[0];

            if (topCandidate.Confidence >= options.MediaTypeAutoAssignThreshold)
            {
                resolvedMediaType = topCandidate.Type;
                _logger.LogInformation(
                    "Media type auto-assigned: {Type} ({Confidence:P0}) for {Path}",
                    topCandidate.Type, topCandidate.Confidence, filePath);
            }
            else if (topCandidate.Confidence >= options.MediaTypeReviewThreshold)
            {
                resolvedMediaType = topCandidate.Type;
                mediaTypeIsConflicted = true;
                mediaTypeNeedsReview = true;
                _logger.LogInformation(
                    "Media type provisional: {Type} ({Confidence:P0}) for {Path} - flagged for review",
                    topCandidate.Type, topCandidate.Confidence, filePath);
            }
            else
            {
                resolvedMediaType = MediaType.Unknown;
                mediaTypeIsConflicted = true;
                mediaTypeNeedsReview = true;
                _logger.LogWarning(
                    "Media type ambiguous ({Confidence:P0}) for {Path} - assigned Unknown, flagged for review",
                    topCandidate.Confidence, filePath);
            }
        }
        else if (rootWatchFolderReview)
        {
            resolvedMediaType = MediaType.Unknown;
            mediaTypeIsConflicted = true;
            mediaTypeNeedsReview = true;
        }

        return new MediaTypeResolution(
            resolvedMediaType,
            mediaTypeIsConflicted,
            mediaTypeNeedsReview,
            adjustedPrior,
            candidateList,
            rootWatchFolderReview,
            rootWatchFolderDetail);
    }

    private void ApplyLibraryFolderPrior(
        string filePath,
        List<MediaTypeCandidate> candidateList,
        ref MediaType resolvedMediaType)
    {
        var matchedFolder = ResolveFolderForPrior(filePath);
        if (matchedFolder is null || matchedFolder.MediaTypes.Count == 0)
        {
            return;
        }

        var extension = Path.GetExtension(filePath);
        if (candidateList.Count == 0
            && resolvedMediaType != MediaType.Unknown
            && !CanSingleTypeFolderOverride(filePath, resolvedMediaType, matchedFolder.MediaTypes))
        {
            return;
        }

        var folderTypes = matchedFolder.MediaTypes;
        var matchingCandidate = candidateList.FirstOrDefault(c => folderTypes.Contains(c.Type));

        if (matchingCandidate is not null)
        {
            if (folderTypes.Count == 1
                && matchingCandidate.Type != resolvedMediaType
                && !CanSingleTypeFolderOverride(filePath, resolvedMediaType, folderTypes))
            {
                _logger.LogInformation(
                    "Library folder prior: kept processor type {ProcessorType} for {Path}; extension {Extension} is strong format evidence outside the folder media type [{Types}]",
                    resolvedMediaType,
                    filePath,
                    extension,
                    string.Join(", ", folderTypes));
                return;
            }

            var topConfidence = candidateList.Max(c => c.Confidence);
            var boostedConfidence = Math.Max(Math.Max(topConfidence + 0.01, matchingCandidate.Confidence), 0.98);
            var index = candidateList.IndexOf(matchingCandidate);
            candidateList[index] = new MediaTypeCandidate
            {
                Type = matchingCandidate.Type,
                Confidence = boostedConfidence,
                Reason = matchingCandidate.Reason,
            };
            candidateList.Sort((left, right) => right.Confidence.CompareTo(left.Confidence));

            _logger.LogInformation(
                "Library folder prior applied: boosted {Type} to {Confidence:P0} for {Path} (folder configured for [{Types}])",
                matchingCandidate.Type, boostedConfidence, filePath, string.Join(", ", folderTypes));
        }
        else if (folderTypes.Count == 1
                 && CanSingleTypeFolderOverride(filePath, resolvedMediaType, folderTypes))
        {
            candidateList.Insert(0, new MediaTypeCandidate
            {
                Type = folderTypes[0],
                Confidence = 0.95,
                Reason = $"Library folder configured for {folderTypes[0]}",
            });

            _logger.LogInformation(
                "Library folder prior override: assigned {Type} at 0.95 for {Path}",
                folderTypes[0], filePath);
        }
        else if (folderTypes.Count == 1)
        {
            _logger.LogInformation(
                "Library folder prior: kept processor type {ProcessorType} for {Path}; extension {Extension} is strong format evidence outside the folder media type [{Types}]",
                resolvedMediaType,
                filePath,
                extension,
                string.Join(", ", folderTypes));
        }
        else if (candidateList.Count == 0)
        {
            foreach (var folderType in folderTypes)
            {
                candidateList.Add(new MediaTypeCandidate
                {
                    Type = folderType,
                    Confidence = 0.65,
                    Reason = $"Library folder configured for {folderType} (multi-type folder, needs disambiguation)",
                });
            }

            _logger.LogInformation(
                "Library folder prior: injected {Count} candidates from multi-type folder [{Types}] for {Path}",
                folderTypes.Count, string.Join(", ", folderTypes), filePath);
        }
        else
        {
            _logger.LogInformation(
                "Library folder prior: processor top {ProcessorTop} not in folder types [{Types}] for {Path} - no override applied",
                candidateList[0].Type, string.Join(", ", folderTypes), filePath);
        }
    }

    /// <summary>
    /// The library folder that gives a file its media type prior. A file in one of the Import folder's per-type folders
    /// (Movies, TV, Music, Books, Audiobooks, Comics) gets that type, even before its type has a library of its own.
    /// </summary>
    private LibraryFolderEntry? ResolveFolderForPrior(string filePath)
    {
        var library = _libraryFolderResolver.ResolveForPath(filePath);
        if (library is not null || _importFolders?.ResolveTypePrior(filePath) is not { } importType)
        {
            return library;
        }

        return new LibraryFolderEntry
        {
            Id = ImportFolderRules.LocationId,
            Name = "Import folder",
            MediaTypes = [importType],
        };
    }

    private bool CanSingleTypeFolderOverride(
        string filePath,
        MediaType processorType,
        IReadOnlyList<MediaType> folderTypes)
    {
        if (folderTypes.Count != 1)
        {
            return false;
        }

        var folderType = folderTypes[0];
        if (folderType == processorType)
        {
            return true;
        }

        var extension = Path.GetExtension(filePath);
        if (_extensionCatalog.IsStrongFormatExtension(extension))
        {
            return false;
        }

        return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || _extensionCatalog.IsVideoExtension(extension)
            || processorType == MediaType.Unknown;
    }

    private bool ApplyRootWatchFolderCap(
        string filePath,
        List<MediaTypeCandidate> candidateList,
        IngestionOptions options,
        ref double categoryConfidencePrior,
        out string? detail)
    {
        detail = null;
        var matchedFolder = ResolveFolderForPrior(filePath);
        var sourcePath = _libraryFolderResolver.ResolveSourcePath(filePath)
            ?? options.EffectiveWatchDirectories
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderByDescending(path => path.Length)
                .FirstOrDefault(path => IsUnderRoot(filePath, path));

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        var watchDir = Path.GetFullPath(sourcePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.Equals(
                fileDir?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                watchDir,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        if (_extensionCatalog.IsUnambiguousExtension(ext))
        {
            return false;
        }

        if (matchedFolder?.MediaTypes.Count == 1)
        {
            _logger.LogDebug(
                "Root watch folder cap skipped for {Path}; source folder is configured for {Type}",
                filePath, matchedFolder.MediaTypes[0]);
            return false;
        }

        for (var i = 0; i < candidateList.Count; i++)
        {
            if (candidateList[i].Confidence <= RootWatchFolderMaxConfidence)
            {
                continue;
            }

            candidateList[i] = new MediaTypeCandidate
            {
                Type = candidateList[i].Type,
                Confidence = RootWatchFolderMaxConfidence,
                Reason = $"Confidence capped at {RootWatchFolderMaxConfidence} - root watch folder drop (ambiguous extension {ext})",
            };
        }

        categoryConfidencePrior = 0.0;
        detail = $"File dropped into root watch folder - please confirm the media type (extension: {ext})";

        _logger.LogInformation(
            "Root watch folder drop detected for {Path} (ext: {Ext}) - confidence capped at {Cap:P0}, routed to review",
            filePath, ext, RootWatchFolderMaxConfidence);

        return true;
    }

    private async Task ApplyAdvisorAsync(
        string filePath,
        ProcessorResult processorResult,
        List<MediaTypeCandidate> candidateList,
        IngestionOptions options,
        CancellationToken ct)
    {
        bool advisorNeeded = processorResult.DetectedType == MediaType.Unknown && candidateList.Count == 0;
        bool advisorLowConfidence = candidateList.Count > 0
            && candidateList[0].Confidence < options.MediaTypeAutoAssignThreshold;

        if (!advisorNeeded && !advisorLowConfidence)
        {
            return;
        }

        try
        {
            var genreClaim = processorResult.Claims.FirstOrDefault(c =>
                c.Key.Equals(MetadataFieldConstants.Genre, StringComparison.OrdinalIgnoreCase));
            var durationClaim = processorResult.Claims.FirstOrDefault(c =>
                c.Key.Equals("duration_sec", StringComparison.OrdinalIgnoreCase));
            var bitrateClaim = processorResult.Claims.FirstOrDefault(c =>
                c.Key.Equals("audio_bitrate", StringComparison.OrdinalIgnoreCase));
            var containerClaim = processorResult.Claims.FirstOrDefault(c =>
                c.Key.Equals("container", StringComparison.OrdinalIgnoreCase));

            double? duration = durationClaim is not null && double.TryParse(durationClaim.Value, out var parsedDuration)
                ? parsedDuration
                : null;
            int? bitrate = bitrateClaim is not null && int.TryParse(bitrateClaim.Value, out var parsedBitrate)
                ? parsedBitrate
                : null;

            var aiCandidate = await _typeAdvisor.ClassifyAsync(
                Path.GetFileName(filePath),
                containerClaim?.Value,
                duration,
                bitrate,
                genreClaim?.Value,
                processorResult.Claims.Any(c => c.Key.Equals("chapter_count", StringComparison.OrdinalIgnoreCase)),
                Path.GetDirectoryName(filePath),
                ct).ConfigureAwait(false);

            if (aiCandidate.Type == MediaType.Unknown)
            {
                return;
            }

            if (advisorNeeded)
            {
                candidateList.Insert(0, aiCandidate);
                _logger.LogInformation(
                    "MediaTypeAdvisor classified {Path} as {Type} ({Conf:F2}) (processor returned Unknown)",
                    filePath, aiCandidate.Type, aiCandidate.Confidence);
            }
            else if (candidateList.Count == 0 || aiCandidate.Confidence > candidateList[0].Confidence)
            {
                candidateList.Insert(0, aiCandidate);
                _logger.LogInformation(
                    "MediaTypeAdvisor classified {Path} as {Type} ({Conf:F2}), overriding processor top",
                    filePath, aiCandidate.Type, aiCandidate.Confidence);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MediaTypeAdvisor failed for {Path} - using processor classification", filePath);
        }
    }

    private static bool IsUnderRoot(string filePath, string rootPath)
    {
        var normalizedFile = Path.GetFullPath(filePath).Replace('\\', '/').TrimEnd('/');
        var normalizedRoot = Path.GetFullPath(rootPath).Replace('\\', '/').TrimEnd('/');

        return normalizedFile.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedFile.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }
}
