using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MediaEngine.Api.Services.LocalAssets.Filing;
using MediaEngine.Api.Services.LocalAssets.Intake;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using SkiaSharp;

namespace MediaEngine.Api.Services.LocalAssets;

/// <summary>
/// Indexes personal View libraries in place. This service performs local-only
/// extraction and deliberately has no dependency on catalogue identity or
/// external metadata providers.
/// </summary>
public sealed class ViewLibraryService(
    ILocalAssetRepository repository,
    IConfigurationLoader configuration,
    IViewPersonalSpaceRepository spaces,
    ViewStorageService storage,
    ILogger<ViewLibraryService> logger,
    IFFmpegService? ffmpeg = null,
    IMediaOperationRepository? operations = null,
    DocumentOcrQueue? ocrQueue = null,
    DocumentRulesQueue? rulesQueue = null,
    PersonalMediaActivityRecorder? activityRecorder = null,
    IFileWriteQueue? fileWrites = null) : IViewPathIndexer, IVaultImageIndexer
{
    private const int MaxDocumentCharacters = 256 * 1024;

    private static readonly IReadOnlyDictionary<string, FileType> SupportedTypes =
        new Dictionary<string, FileType>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = new(LocalAssetMediaKinds.Image, "image/jpeg"),
            [".jpeg"] = new(LocalAssetMediaKinds.Image, "image/jpeg"),
            [".png"] = new(LocalAssetMediaKinds.Image, "image/png"),
            [".webp"] = new(LocalAssetMediaKinds.Image, "image/webp"),
            [".gif"] = new(LocalAssetMediaKinds.Image, "image/gif"),
            [".bmp"] = new(LocalAssetMediaKinds.Image, "image/bmp"),
            [".tif"] = new(LocalAssetMediaKinds.Image, "image/tiff"),
            [".tiff"] = new(LocalAssetMediaKinds.Image, "image/tiff"),
            [".heic"] = new(LocalAssetMediaKinds.Image, "image/heic"),
            [".heif"] = new(LocalAssetMediaKinds.Image, "image/heif"),
            [".avif"] = new(LocalAssetMediaKinds.Image, "image/avif"),
            [".dng"] = new(LocalAssetMediaKinds.Image, "image/x-adobe-dng"),
            [".cr2"] = new(LocalAssetMediaKinds.Image, "image/x-canon-cr2"),
            [".cr3"] = new(LocalAssetMediaKinds.Image, "image/x-canon-cr3"),
            [".nef"] = new(LocalAssetMediaKinds.Image, "image/x-nikon-nef"),
            [".arw"] = new(LocalAssetMediaKinds.Image, "image/x-sony-arw"),
            [".raf"] = new(LocalAssetMediaKinds.Image, "image/x-fuji-raf"),
            [".mp4"] = new(LocalAssetMediaKinds.Video, "video/mp4"),
            [".m4v"] = new(LocalAssetMediaKinds.Video, "video/x-m4v"),
            [".mov"] = new(LocalAssetMediaKinds.Video, "video/quicktime"),
            [".avi"] = new(LocalAssetMediaKinds.Video, "video/x-msvideo"),
            [".wmv"] = new(LocalAssetMediaKinds.Video, "video/x-ms-wmv"),
            [".mkv"] = new(LocalAssetMediaKinds.Video, "video/x-matroska"),
            [".webm"] = new(LocalAssetMediaKinds.Video, "video/webm"),
            [".pdf"] = new(LocalAssetMediaKinds.Document, "application/pdf"),
            [".doc"] = new(LocalAssetMediaKinds.Document, "application/msword"),
            [".docx"] = new(LocalAssetMediaKinds.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            [".xls"] = new(LocalAssetMediaKinds.Document, "application/vnd.ms-excel"),
            [".xlsx"] = new(LocalAssetMediaKinds.Document, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            [".ppt"] = new(LocalAssetMediaKinds.Document, "application/vnd.ms-powerpoint"),
            [".pptx"] = new(LocalAssetMediaKinds.Document, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            [".txt"] = new(LocalAssetMediaKinds.Document, "text/plain"),
            [".md"] = new(LocalAssetMediaKinds.Document, "text/markdown"),
            [".rtf"] = new(LocalAssetMediaKinds.Document, "application/rtf"),
            [".odt"] = new(LocalAssetMediaKinds.Document, "application/vnd.oasis.opendocument.text"),
            [".ods"] = new(LocalAssetMediaKinds.Document, "application/vnd.oasis.opendocument.spreadsheet"),
            [".odp"] = new(LocalAssetMediaKinds.Document, "application/vnd.oasis.opendocument.presentation"),
            [".csv"] = new(LocalAssetMediaKinds.Document, "text/csv"),
            [".json"] = new(LocalAssetMediaKinds.Document, "application/json"),
            [".xml"] = new(LocalAssetMediaKinds.Document, "application/xml"),
            [".mp3"] = new(LocalAssetMediaKinds.Audio, "audio/mpeg"),
            [".m4a"] = new(LocalAssetMediaKinds.Audio, "audio/mp4"),
            [".aac"] = new(LocalAssetMediaKinds.Audio, "audio/aac"),
            [".wav"] = new(LocalAssetMediaKinds.Audio, "audio/wav"),
            [".flac"] = new(LocalAssetMediaKinds.Audio, "audio/flac"),
            [".ogg"] = new(LocalAssetMediaKinds.Audio, "audio/ogg"),
            [".opus"] = new(LocalAssetMediaKinds.Audio, "audio/opus"),
            [".aiff"] = new(LocalAssetMediaKinds.Audio, "audio/aiff"),
            // XMP files are indexed only as companions to an adjacent RAW set.
            [".xmp"] = new(LocalAssetMediaKinds.Other, "application/rdf+xml"),
        };

    private static readonly IReadOnlySet<string> RawExtensions = new HashSet<string>(
        [".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> JpegExtensions = new HashSet<string>(
        [".jpg", ".jpeg"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<LocalAssetScanResultDto?> ScanAsync(
        Guid libraryId,
        CancellationToken ct = default)
    {
        var space = await spaces.GetByLibraryAsync(libraryId, ct);
        if (space is null)
        {
            return null;
        }

        var filesSeen = 0;
        var itemsAdded = 0;
        var filesAdded = 0;
        var sourcesAdded = 0;
        var duplicates = 0;
        var errors = 0;

        foreach (var source in (await spaces.GetSourcesAsync(space.Id, ct)).Where(source => source.Enabled))
        {
            var sourcePath = storage.GetSourcePath(space, source);
            if (!Directory.Exists(sourcePath))
            {
                continue;
            }

            IReadOnlyList<string> paths;
            try
            {
                paths = Directory.GetFiles(
                    sourcePath,
                    "*",
                    source.IncludeSubdirectories
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not enumerate View source {Root}", sourcePath);
                errors++;
                continue;
            }

            // Empty Trash parks files in a hidden folder inside the profile folder for a moment; they are never indexed.
            var candidates = paths
                .Where(path => !ViewStorageService.IsInsideStagingFolder(path))
                .Select(TryCreateCandidate)
                .Where(candidate => candidate is not null)
                .Cast<FileCandidate>()
                .ToList();
            filesSeen += candidates.Count;

            // A folder takes only what fits its kind. The rest is left exactly where it is (never moved, deleted or
            // changed) and counted on the folder's row, reset by every full scan.
            var skipped = 0;
            foreach (var group in BuildGroups(candidates))
            {
                ct.ThrowIfCancellationRequested();
                if (!ViewKindAcceptance.SourceAccepts(source.ContentKind, source.SourceKey, group.Primary.Type.MediaKind))
                {
                    skipped += group.Files.Count;
                    continue;
                }

                try
                {
                    var result = await IndexGroupAsync(space, source, group, ct);
                    if (result.ItemAdded)
                    {
                        itemsAdded++;
                    }

                    filesAdded += result.FilesAdded;
                    sourcesAdded += result.SourcesAdded;
                    if (!result.ItemAdded && result.SourcesAdded > 0)
                    {
                        duplicates++;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    logger.LogWarning(exception, "Could not index local View item {Path}", group.Primary.Path);
                    errors++;
                }
            }

            await spaces.SetSourceSkippedFileCountAsync(space.Id, source.Id, skipped, ct);
        }

        if (activityRecorder is not null)
        {
            // A finished scan writes what it added straight away instead of waiting out the quiet period.
            await activityRecorder.FlushAsync(libraryId, ct);
        }

        return new LocalAssetScanResultDto(
            libraryId,
            filesSeen,
            itemsAdded,
            filesAdded,
            sourcesAdded,
            duplicates,
            errors);
    }

    /// <summary>
    /// Indexes one newly arrived file through the provider-isolated View path.
    /// Adjacent Live Photo/RAW companions with the same stem are included so a
    /// direct upload produces the same logical grouping as a later full scan.
    /// </summary>
    public Task<LocalAssetUpsertResult?> IndexPathAsync(
        Guid libraryId,
        string path,
        CancellationToken ct = default) =>
        IndexPathAsync(libraryId, path, null, ct);

    /// <param name="addedByProfileId">
    /// The profile that put the file there (an upload's requester). Without it the item is tagged with the folder's
    /// own profile, or the space's owner for the upload/backup source.
    /// </param>
    public async Task<LocalAssetUpsertResult?> IndexPathAsync(
        Guid libraryId,
        string path,
        Guid? addedByProfileId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();

        var space = await spaces.GetByLibraryAsync(libraryId, ct);
        if (space is null)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        if (ViewStorageService.IsInsideStagingFolder(fullPath))
        {
            throw new InvalidDataException("Files that Empty Trash is removing are never indexed.");
        }

        var sourceCandidates = new List<(ViewSource Source, string Path)>();
        foreach (var candidate in (await spaces.GetSourcesAsync(space.Id, ct)).Where(candidate => candidate.Enabled))
        {
            var root = storage.GetSourcePath(space, candidate);
            if (ViewStorageService.Contains(root, fullPath, candidate.IncludeSubdirectories))
            {
                sourceCandidates.Add((candidate, root));
            }
        }
        var source = sourceCandidates.OrderByDescending(candidate => candidate.Path.Length)
            .Select(candidate => candidate.Source).FirstOrDefault();
        if (source is null)
        {
            throw new InvalidOperationException("The local asset path is outside the View library's configured sources.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The local asset file does not exist.", fullPath);
        }

        var uploaded = TryCreateCandidate(fullPath)
            ?? throw new InvalidDataException($"The file type '{Path.GetExtension(fullPath)}' is not supported by View.");
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("The local asset directory could not be resolved.");
        var uploadedGroupKey = GetCompoundGroupKey(uploaded);
        var candidates = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(TryCreateCandidate)
            .Where(candidate => candidate is not null)
            .Cast<FileCandidate>()
            .Where(candidate => string.Equals(GetCompoundGroupKey(candidate), uploadedGroupKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var group = BuildGroups(candidates).FirstOrDefault(candidate =>
            candidate.Files.Any(member => string.Equals(
                member.Candidate.Path,
                uploaded.Path,
                StringComparison.OrdinalIgnoreCase)));
        if (group is null)
        {
            throw new InvalidDataException("The uploaded local asset could not be grouped for indexing.");
        }

        if (!ViewKindAcceptance.SourceAccepts(source.ContentKind, source.SourceKey, group.Primary.Type.MediaKind))
        {
            throw new InvalidDataException($"The file type '{Path.GetExtension(fullPath)}' does not fit the folder's kind.");
        }

        return await IndexGroupAsync(space, source, group, ct, addedByProfileId);
    }

    /// <param name="ownerProfileId">Whose personal space the file lands in.</param>
    /// <param name="addedByProfileId">
    /// The profile that made the request, recorded as who added the item. It differs from the owner when a paired phone
    /// backs up to someone else's space; without it the owner is recorded.
    /// </param>
    public async Task<LocalAssetUpsertResult> UploadAsync(
        Guid ownerProfileId,
        string fileName,
        Stream content,
        CancellationToken ct = default,
        Guid? addedByProfileId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        var settings = configuration.LoadLibraries();
        if (!settings.PersonalLibraryPolicy.AllowBrowserUpload)
        {
            throw new InvalidOperationException("Browser upload is disabled by administrator policy.");
        }

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)
            || TryCreateCandidate(safeName) is not { } named
            || !ViewKindAcceptance.IntakeAccepts(named.Type.MediaKind))
        {
            throw new InvalidDataException("The uploaded file type is not supported by View.");
        }

        var space = await storage.EnsurePersonalSpaceAsync(ownerProfileId, ct);
        var destination = await storage.EnsureManagedSourceAsync(
            space, "Browser uploads", ViewSourceType.BrowserUpload, "builtin:browser-uploads", ct);
        var destinationPath = storage.GetSourcePath(space, destination);
        var extension = Path.GetExtension(safeName);
        // Stage and inspect first, so rejected or cancelled uploads leave no empty profile tree.
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"tuvima-{Guid.NewGuid():N}.uploading{extension}");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await content.CopyToAsync(output, ct);
            }
            var candidate = TryCreateCandidate(temporaryPath)
                ?? throw new InvalidDataException("The uploaded file type is not supported by View.");
            var date = await ReadCaptureDateAsync(candidate, ct, safeName);
            var layout = ViewFilingLayout.FolderFor(configuration.LoadPhotoFiling().LayoutPattern, date.Value);
            var calendarDirectory = Path.Combine(destinationPath, layout);
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(calendarDirectory);
            var finalPath = Path.Combine(calendarDirectory,
                ViewFilingLayout.NamesFor(calendarDirectory, [safeName], File.Exists)[0]);
            File.Move(temporaryPath, finalPath);
            return await IndexPathAsync(space.LibraryId, finalPath, addedByProfileId ?? ownerProfileId, ct)
                ?? throw new InvalidOperationException("The uploaded file could not be indexed.");
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    /// <summary>
    /// Indexes the images under one Vault folder (Videos spec D44) with the same image reading View uses. Only images are taken. The
    /// content hash is salted so a Vault file never shares a file record with the same bytes in View, GPS and place are not kept,
    /// there is no Personal Space, source, device, activity entry or text recognition, and a failure is logged as a kind of error only.
    /// </summary>
    public async Task<VaultImageIndexResult> IndexFolderAsync(
        Guid libraryId, string root, bool includeSubdirectories, CancellationToken ct = default)
    {
        var images = new List<VaultIndexedImage>();
        if (libraryId == Guid.Empty || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return new VaultImageIndexResult(0, 0, 0, images);
        }

        IReadOnlyList<string> paths;
        try
        {
            paths = Directory.GetFiles(root, "*", includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("A Vault image folder could not be read ({Kind}).", exception.GetType().Name);
            return new VaultImageIndexResult(0, 0, 1, images);
        }

        var candidates = paths
            .Where(path => !ViewStorageService.IsInsideStagingFolder(path))
            .Select(TryCreateCandidate)
            .Where(candidate => candidate is { Type.MediaKind: LocalAssetMediaKinds.Image })
            .Cast<FileCandidate>()
            .ToList();
        var itemsAdded = 0;
        var errors = 0;
        foreach (var group in BuildGroups(candidates))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var metadata = await ReadMetadataAsync(group.Primary, ct);
                var registrations = new List<LocalAssetFileRegistration>(group.Files.Count);
                foreach (var member in group.Files)
                {
                    var info = new FileInfo(member.Candidate.Path);
                    registrations.Add(new LocalAssetFileRegistration(
                        Path.GetFullPath(member.Candidate.Path),
                        SaltVaultHash(await HashAsync(member.Candidate.Path, ct)),
                        info.Name,
                        member.Candidate.Type.MimeType,
                        info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                        member.Role));
                }

                var upserted = await repository.UpsertAsync(new LocalAssetRegistration(
                    LibraryId: libraryId,
                    PersonalSpaceId: null,
                    OwnerProfileId: null,
                    MediaKind: LocalAssetMediaKinds.Image,
                    Title: metadata.Title ?? Path.GetFileNameWithoutExtension(group.Primary.Path),
                    CapturedAt: metadata.CapturedAt,
                    Files: registrations,
                    Width: metadata.Width,
                    Height: metadata.Height,
                    DeviceMake: metadata.DeviceMake,
                    DeviceModel: metadata.DeviceModel,
                    LensModel: metadata.LensModel,
                    ExposureTime: metadata.ExposureTime,
                    Aperture: metadata.Aperture,
                    Iso: metadata.Iso,
                    FocalLengthMm: metadata.FocalLengthMm,
                    ScopeKind: LocalAssetScopeKinds.Vault), ct);
                if (upserted.ItemAdded)
                {
                    itemsAdded++;
                }

                images.Add(new VaultIndexedImage(
                    upserted.ItemId, Path.GetDirectoryName(group.Primary.Path) ?? root, upserted.ItemAdded));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogWarning("A Vault image could not be indexed ({Kind}).", exception.GetType().Name);
                errors++;
            }
        }

        return new VaultImageIndexResult(candidates.Count, itemsAdded, errors, images);
    }

    /// <summary>A Vault file's content hash, so the same bytes in View and in the Vault never share a file record.</summary>
    internal static string SaltVaultHash(string contentHash) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("vault-image:" + contentHash)));

    private int Count(Guid libraryId, string? mediaKind, CancellationToken ct) =>
        repository.Query(new LocalAssetQuery(
            libraryId,
            Limit: 1,
            MediaKinds: mediaKind is null ? null : [mediaKind],
            IncludeHidden: true), ct).Total;

    // The library's name as the person sees it ("Family photos"), read from the libraries configuration.
    private string LibraryDisplayName(ViewPersonalSpace space)
    {
        var library = configuration.LoadLibraries().Libraries
            .FirstOrDefault(candidate => Guid.TryParse(candidate.Id, out var id) && id == space.LibraryId);
        return string.IsNullOrWhiteSpace(library?.Name) ? "your personal library" : library.Name;
    }

    private async Task<LocalAssetUpsertResult> IndexGroupAsync(
        ViewPersonalSpace space,
        ViewSource source,
        AssetGroup group,
        CancellationToken ct,
        Guid? addedByProfileId = null)
    {
        var metadata = await ReadMetadataAsync(group.Primary, ct);
        var device = await ResolveDeviceAsync(space, source, metadata, ct);
        var registrations = new List<LocalAssetFileRegistration>(group.Files.Count);
        foreach (var member in group.Files)
        {
            var info = new FileInfo(member.Candidate.Path);
            registrations.Add(new LocalAssetFileRegistration(
                Path.GetFullPath(member.Candidate.Path),
                await HashAsync(member.Candidate.Path, ct),
                info.Name,
                member.Candidate.Type.MimeType,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                member.Role,
                SourceId: source.Id,
                DeviceId: device?.Id));

            if (metadata.DurationSeconds is null
                && member.Candidate.Type.MediaKind is LocalAssetMediaKinds.Video or LocalAssetMediaKinds.Audio)
            {
                metadata = metadata with { DurationSeconds = ReadDuration(member.Candidate.Path) };
            }
        }

        // Who added it: the profile making the request for an upload, otherwise the profile that added the folder.
        // The upload/backup source has no adder of its own, so its files default to the space's owner.
        var addedBy = addedByProfileId
            ?? source.AddedByProfileId
            ?? (ViewKindAcceptance.IsIntakeSource(source.SourceKey) ? space.OwnerProfileId : null);
        var upserted = await repository.UpsertAsync(new LocalAssetRegistration(
            LibraryId: space.LibraryId,
            PersonalSpaceId: space.Id,
            OwnerProfileId: space.OwnerProfileId,
            MediaKind: group.Primary.Type.MediaKind,
            Title: metadata.Title ?? Path.GetFileNameWithoutExtension(group.Primary.Path),
            ContentKind: ViewKindAcceptance.KindFor(source.ContentKind, source.SourceKey, group.Primary.Type.MediaKind),
            AddedByProfileId: addedBy,
            CapturedAt: metadata.CapturedAt,
            Files: registrations,
            Width: metadata.Width,
            Height: metadata.Height,
            DurationSeconds: metadata.DurationSeconds,
            PageCount: metadata.PageCount,
            DeviceMake: metadata.DeviceMake,
            DeviceModel: metadata.DeviceModel,
            Latitude: metadata.Latitude,
            Longitude: metadata.Longitude,
            LocationName: metadata.LocationName,
            LensModel: metadata.LensModel,
            ExposureTime: metadata.ExposureTime,
            Aperture: metadata.Aperture,
            Iso: metadata.Iso,
            FocalLengthMm: metadata.FocalLengthMm,
            VideoCodec: metadata.VideoCodec,
            FrameRate: metadata.FrameRate,
            DocumentText: metadata.DocumentText), ct);
        await QueueDocumentTextReadAsync(upserted.ItemId, group.Primary, registrations, ct);
        await QueueAddedByWriteAsync(upserted, source, addedBy, ct);
        if (upserted.ItemAdded && activityRecorder is not null
            && group.Primary.Type.MediaKind is LocalAssetMediaKinds.Image or LocalAssetMediaKinds.Video)
        {
            // Photos and personal videos reach the Activity log as one count per library, never one row per file.
            activityRecorder.Record(space.LibraryId, () => LibraryDisplayName(space), group.Primary.Type.MediaKind == LocalAssetMediaKinds.Video);
        }

        if (upserted.ItemAdded && rulesQueue is not null && group.Primary.Type.MediaKind == LocalAssetMediaKinds.Document)
        {
            // A new document waits in the Inbox: rules on its name, folder, source and kind apply at once, and the
            // words (read a moment later) queue the rules again.
            await rulesQueue.TryQueueAfterTextAsync(upserted.ItemId, metadata.DocumentText, ct);
        }

        return upserted;
    }

    /// <summary>
    /// A new item with an adder in a Managed folder goes on the write list, so the adder's name is put into the file on the next scheduled
    /// run (batched write-back D7). Every such item is queued, also kinds that cannot hold the name, so the folder can count the files
    /// kept in Library only. A Read-only (linked) folder is never queued. The item is already saved; a failure to queue never fails the
    /// scan, and the next scan does not queue it again (it is no longer new), so it is logged.
    /// </summary>
    private async Task QueueAddedByWriteAsync(LocalAssetUpsertResult upserted, ViewSource source, Guid? addedBy, CancellationToken ct)
    {
        if (!upserted.ItemAdded || fileWrites is null || addedBy is null || source.StorageMode != ViewSourceStorageMode.Managed)
        {
            return;
        }

        try
        {
            await fileWrites.EnqueueViewItemAsync(upserted.ItemId, MediaFileWriteTriggers.ViewAddedBy, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The profile name of View item {ItemId} could not be put on the file write list.", upserted.ItemId);
        }
    }

    /// <summary>
    /// PDFs and Office files are read by a throttled background job, never inline during the scan.
    /// A failure to queue must not fail the scan; the next scan queues it again.
    /// </summary>
    private async Task QueueDocumentTextReadAsync(
        Guid itemId,
        FileCandidate primary,
        IReadOnlyList<LocalAssetFileRegistration> registrations,
        CancellationToken ct)
    {
        var isPhotoOfPaper = primary.Type.MediaKind == LocalAssetMediaKinds.Image && OcrFormats.IsImage(primary.Extension);
        if (operations is null
            || (primary.Type.MediaKind != LocalAssetMediaKinds.Document && !isPhotoOfPaper)
            || (!isPhotoOfPaper && !DocumentTextReadService.NeedsBackgroundRead(primary.Extension)))
        {
            return;
        }

        var file = registrations.FirstOrDefault(candidate =>
            string.Equals(candidate.Role, LocalAssetFileRoles.Primary, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return;
        }

        try
        {
            if (isPhotoOfPaper)
            {
                // Photos go straight to text recognition; it only queues while recognition is on and installed.
                if (ocrQueue is not null)
                {
                    await ocrQueue.TryQueueAsync(itemId, file.ContentHash, file.FilePath, ct);
                }

                return;
            }

            await DocumentTextReadService.QueueAsync(operations, itemId, file.ContentHash, file.FilePath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not queue the text read for document {FileName}.", file.FileName);
        }
    }

    private async Task<ViewDevice?> ResolveDeviceAsync(
        ViewPersonalSpace space,
        ViewSource source,
        LocalMetadata metadata,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(metadata.DeviceMake)
            && string.IsNullOrWhiteSpace(metadata.DeviceModel))
        {
            return null;
        }

        var clientId = $"metadata:{metadata.DeviceMake}:{metadata.DeviceModel}";
        var existing = (await spaces.GetDevicesAsync(space.Id, ct))
            .FirstOrDefault(candidate => string.Equals(candidate.ClientDeviceId, clientId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        return await spaces.UpsertDeviceAsync(new ViewDevice(
            Guid.Empty,
            space.Id,
            source.Id,
            clientId,
            metadata.DeviceModel ?? metadata.DeviceMake ?? "Imported device",
            metadata.DeviceMake,
            metadata.DeviceModel,
            null,
            ViewDeviceBackupState.Unknown,
            now,
            now), ct);
    }

    /// <summary>
    /// True when View would index a file with this name as a document. Uploads use it to refuse documents before the
    /// file is stored, so no answer about an existing document (such as "already added") can reach a profile that
    /// may not use Documents.
    /// </summary>
    public static bool IsDocumentFileName(string fileName) =>
        TryCreateCandidate(Path.GetFileName(fileName)) is { Type.MediaKind: LocalAssetMediaKinds.Document };

    /// <summary>
    /// The View kind a file of this name takes when it arrives through an upload or backup (a picture is a photo, a video a
    /// personal video, a document a document), or null when View takes no such file. Uploads use it to check the right
    /// permission before anything is stored.
    /// </summary>
    public static string? OwnKindOfFileName(string fileName) =>
        TryCreateCandidate(Path.GetFileName(fileName)) is { } candidate
            ? ViewKindAcceptance.OwnKindOf(candidate.Type.MediaKind)
            : null;

    internal static FileCandidate? TryCreateCandidate(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedTypes.TryGetValue(extension, out var type)
            ? new FileCandidate(Path.GetFullPath(path), extension.ToLowerInvariant(), type)
            : null;
    }

    internal static IReadOnlyList<AssetGroup> BuildGroups(IReadOnlyList<FileCandidate> candidates)
    {
        var result = new List<AssetGroup>();
        foreach (var stemGroup in candidates
                     .GroupBy(
                         GetCompoundGroupKey,
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var members = stemGroup.OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).ToList();
            var consumed = new HashSet<FileCandidate>();

            var images = members.Where(candidate =>
                candidate.Extension is ".heic" or ".heif" || JpegExtensions.Contains(candidate.Extension)).ToList();
            var motions = members.Where(candidate => candidate.Extension == ".mov").ToList();
            foreach (var image in images)
            {
                var motion = motions
                    .FirstOrDefault(candidate => !consumed.Contains(candidate) && IsLivePhotoPair(image, candidate));
                if (motion is null)
                {
                    continue;
                }

                result.Add(new AssetGroup(image,
                [
                    new AssetMember(image, LocalAssetFileRoles.Primary),
                    new AssetMember(motion, LocalAssetFileRoles.LivePhotoVideo),
                ]));
                consumed.Add(image);
                consumed.Add(motion);
            }

            var raw = members.Where(candidate => RawExtensions.Contains(candidate.Extension)).ToList();
            var jpeg = members.Where(candidate => JpegExtensions.Contains(candidate.Extension)).ToList();
            var sidecars = members.Where(candidate => candidate.Extension == ".xmp").ToList();
            if (raw.Count > 0 && (jpeg.Count > 0 || sidecars.Count > 0))
            {
                var primary = jpeg.FirstOrDefault() ?? raw[0];
                var assetMembers = raw
                    .Select(candidate => new AssetMember(
                        candidate,
                        candidate == primary ? LocalAssetFileRoles.Primary : LocalAssetFileRoles.Raw))
                    .Concat(jpeg.Select(candidate => new AssetMember(
                        candidate,
                        candidate == primary ? LocalAssetFileRoles.Primary : LocalAssetFileRoles.Jpeg)))
                    .Concat(sidecars.Select(candidate => new AssetMember(candidate, LocalAssetFileRoles.Sidecar)))
                    .ToList();
                result.Add(new AssetGroup(primary, assetMembers));
                foreach (var candidate in raw.Concat(jpeg).Concat(sidecars))
                {
                    consumed.Add(candidate);
                }
            }

            foreach (var candidate in members.Where(candidate => !consumed.Contains(candidate)))
            {
                if (candidate.Extension == ".xmp")
                {
                    continue;
                }

                result.Add(new AssetGroup(candidate,
                    [new AssetMember(candidate, LocalAssetFileRoles.Primary)]));
            }
        }

        return result;
    }

    private static string GetCompoundGroupKey(FileCandidate candidate) =>
        Path.Combine(Path.GetDirectoryName(candidate.Path) ?? string.Empty, Path.GetFileNameWithoutExtension(candidate.Path));

    // A Live Photo is a picture and a short video that share one name.
    private static bool IsLivePhotoPair(FileCandidate image, FileCandidate motion) =>
        string.Equals(
            Path.GetFileNameWithoutExtension(image.Path),
            Path.GetFileNameWithoutExtension(motion.Path),
            StringComparison.OrdinalIgnoreCase)
        && (image.Extension is ".heic" or ".heif" || JpegExtensions.Contains(image.Extension));

    private async Task<LocalMetadata> ReadMetadataAsync(FileCandidate candidate, CancellationToken ct)
    {
        var info = new FileInfo(candidate.Path);
        var capturedAt = ResolveFileDate(info);
        string? title = null;
        int? width = null;
        int? height = null;
        double? durationSeconds = null;
        string? deviceMake = null;
        string? deviceModel = null;
        double? latitude = null;
        double? longitude = null;
        string? videoCodec = null;
        double? frameRate = null;
        var capturedFromMetadata = false;

        if (candidate.Type.MediaKind == LocalAssetMediaKinds.Image)
        {
            try
            {
                using var stream = System.IO.File.OpenRead(candidate.Path);
                using var codec = SKCodec.Create(stream);
                if (codec is not null)
                {
                    width = codec.Info.Width;
                    height = codec.Info.Height;
                }
            }
            catch
            {
                // An unsupported image codec leaves dimensions unknown.
            }
        }

        try
        {
            using var file = TagLib.File.Create(candidate.Path);
            title = NullIfWhiteSpace(file.Tag.Title);
            durationSeconds = file.Properties.Duration.TotalSeconds > 0
                ? file.Properties.Duration.TotalSeconds
                : null;
            width ??= file.Properties.VideoWidth > 0 ? file.Properties.VideoWidth : null;
            height ??= file.Properties.VideoHeight > 0 ? file.Properties.VideoHeight : null;
            if (file is TagLib.Image.File image)
            {
                var tag = image.ImageTag;
                if (tag.DateTime.HasValue)
                {
                    capturedAt = new DateTimeOffset(
                        DateTime.SpecifyKind(tag.DateTime.Value, DateTimeKind.Local)).ToUniversalTime();
                    capturedFromMetadata = true;
                }
                deviceMake = NullIfWhiteSpace(tag.Make);
                deviceModel = NullIfWhiteSpace(tag.Model);
                latitude = ValidCoordinate(tag.Latitude, -90, 90);
                longitude = ValidCoordinate(tag.Longitude, -180, 180);
            }
        }
        catch
        {
            // Unsupported or malformed local metadata is non-fatal.
        }

        if (ffmpeg is { IsAvailable: true }
            && (candidate.Type.MediaKind is LocalAssetMediaKinds.Image or LocalAssetMediaKinds.Video or LocalAssetMediaKinds.Audio))
        {
            var probe = await ffmpeg.ProbeAsync(candidate.Path, ct).ConfigureAwait(false);
            if (probe is not null)
            {
                title ??= NullIfWhiteSpace(probe.Title);
                capturedFromMetadata |= probe.CapturedAt is not null;
                capturedAt = probe.CapturedAt ?? capturedAt;
                width ??= probe.Width;
                height ??= probe.Height;
                durationSeconds ??= probe.Duration.TotalSeconds > 0 ? probe.Duration.TotalSeconds : null;
                deviceMake ??= NullIfWhiteSpace(probe.DeviceMake);
                deviceModel ??= NullIfWhiteSpace(probe.DeviceModel);
                latitude ??= ValidCoordinate(probe.Latitude, -90, 90);
                longitude ??= ValidCoordinate(probe.Longitude, -180, 180);
                videoCodec ??= NullIfWhiteSpace(probe.VideoCodec);
                frameRate ??= probe.FrameRate is > 0 ? probe.FrameRate : null;
            }
        }

        return new LocalMetadata(
            title,
            capturedAt,
            width,
            height,
            durationSeconds,
            null,
            deviceMake,
            deviceModel,
            latitude,
            longitude,
            null,
            ReadBoundedDocumentText(candidate),
            null,
            null,
            null,
            null,
            null,
            videoCodec,
            frameRate,
            capturedFromMetadata);
    }

    /// <summary>
    /// The date a photo or video was taken, for filing it (Photo and video filing spec D2): the camera or phone's own date, then a date in
    /// the file name, then the date of the file itself (the earlier of created and last changed, since a copy gets a new created time).
    /// </summary>
    internal async Task<ViewCaptureDate> ReadCaptureDateAsync(FileCandidate candidate, CancellationToken ct, string? originalName = null)
    {
        var metadata = await ReadMetadataAsync(candidate, ct);
        var info = new FileInfo(candidate.Path);
        var created = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var fileDate = created.Year >= 1990 && created < modified ? created : modified;
        return ViewCaptureDates.Resolve(
            metadata.CapturedAtFromMetadata ? metadata.CapturedAt : null,
            originalName ?? Path.GetFileName(candidate.Path),
            fileDate,
            DateTimeOffset.UtcNow);
    }

    private static string? ReadBoundedDocumentText(FileCandidate candidate)
    {
        if (candidate.Extension is not (".txt" or ".md" or ".csv" or ".json" or ".xml"))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                candidate.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxDocumentCharacters];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            return count == 0 ? null : new string(buffer, 0, count);
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadDuration(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return file.Properties.Duration.TotalSeconds > 0
                ? file.Properties.Duration.TotalSeconds
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static DateTimeOffset ResolveFileDate(FileInfo info)
    {
        var created = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        return created.Year >= 1990 && created <= DateTimeOffset.UtcNow.AddDays(1)
            ? created
            : modified;
    }

    private static double? ValidCoordinate(double? value, double minimum, double maximum) =>
        value.HasValue && value.Value >= minimum && value.Value <= maximum ? value : null;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record FileType(string MediaKind, string MimeType);
    internal sealed record FileCandidate(string Path, string Extension, FileType Type);
    internal sealed record AssetMember(FileCandidate Candidate, string Role);
    internal sealed record AssetGroup(FileCandidate Primary, IReadOnlyList<AssetMember> Files);
    private sealed record LocalMetadata(
        string? Title,
        DateTimeOffset CapturedAt,
        int? Width,
        int? Height,
        double? DurationSeconds,
        int? PageCount,
        string? DeviceMake,
        string? DeviceModel,
        double? Latitude,
        double? Longitude,
        string? LocationName,
        string? DocumentText,
        string? LensModel,
        string? ExposureTime,
        double? Aperture,
        int? Iso,
        double? FocalLengthMm,
        string? VideoCodec,
        double? FrameRate,
        bool CapturedAtFromMetadata = false);
}
