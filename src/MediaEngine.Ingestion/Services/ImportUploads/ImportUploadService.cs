using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using MediaEngine.Contracts.Imports;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Services.ImportUploads;

/// <summary>
/// Resumable uploads of big files into the Import folder (Library setup spec D44). A start call is checked completely before any bytes
/// move: the kind must map to a Managed destination, the Import folder must be set and writable, and the drives must have room.
/// The file then arrives in numbered pieces, each written at its place in a hidden working file under the Import folder's <c>.data</c>
/// folder (so the last step is a rename on the same drive, and the file scanner never sees it). Complete moves the finished file into
/// the kind's Import sub-folder under a name that never overwrites anything, and the normal filing rules take over from there.
/// Everything an upload needs to resume, including after an Engine restart, is in the data store.
/// </summary>
public sealed class ImportUploadService
{
    private const string UploadsFolderName = "uploads";
    private const string WorkingFileExtension = ".part";
    private const int CopyBufferBytes = 256 * 1024;
    private const int MaxNumberedNames = 10_000;

    // How long a finished (completed or cancelled) upload row is kept before the sweep removes it.
    private static readonly TimeSpan ClosedRowRetention = TimeSpan.FromDays(7);

    private readonly IImportUploadRepository _uploads;
    private readonly IImportFolderService _importFolders;
    private readonly IConfigurationLoader _configuration;
    private readonly IIngestionEngine _engine;
    private readonly IVaultIngestionHook _vault;
    private readonly IOptions<IngestionOptions> _options;
    private readonly ILibraryKindSource? _kindSource;
    private readonly TimeProvider _time;
    private readonly Func<string, long?> _availableBytes;
    private readonly ILogger<ImportUploadService> _logger;
    private readonly ConcurrentDictionary<Guid, ImportUploadGate> _gates = new();

    /// <param name="availableBytes">Free bytes on the drive holding a path, or null when unknown. Tests pass their own; the Engine uses the real drive.</param>
    public ImportUploadService(
        IImportUploadRepository uploads,
        IImportFolderService importFolders,
        IConfigurationLoader configuration,
        IIngestionEngine engine,
        IVaultIngestionHook vault,
        IOptions<IngestionOptions> options,
        ILogger<ImportUploadService> logger,
        ILibraryKindSource? kindSource = null,
        TimeProvider? time = null,
        Func<string, long?>? availableBytes = null)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(importFolders);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _uploads = uploads;
        _importFolders = importFolders;
        _configuration = configuration;
        _engine = engine;
        _vault = vault;
        _options = options;
        _logger = logger;
        _kindSource = kindSource;
        _time = time ?? TimeProvider.System;
        _availableBytes = availableBytes ?? VolumeProbe.AvailableBytes;
    }

    /// <summary>The largest request body the piece action accepts, in bytes: one piece. The Engine sets it on that action only.</summary>
    public long MaxPieceRequestBytes => PieceBytes(_configuration.LoadMaintenance());

    /// <summary>Checks everything about the destination, then creates the upload and its hidden working file. A refusal creates nothing.</summary>
    public async Task<ImportUploadResult<ImportUploadStartResponse>> StartAsync(
        Guid owner, ImportUploadStartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var maintenance = _configuration.LoadMaintenance();

        if (!ImportUploadFileNames.TryValidate(request.FileName, out var nameProblem))
        {
            return Refuse<ImportUploadStartResponse>(ImportUploadRefusalKind.BadRequest, ImportUploadRefusalCodes.InvalidFileName, nameProblem);
        }

        if (request.TotalBytes <= 0)
        {
            return Refuse<ImportUploadStartResponse>(ImportUploadRefusalKind.BadRequest, ImportUploadRefusalCodes.InvalidSize, "The file is empty.");
        }

        if (request.TotalBytes > maintenance.MaxUploadBytes)
        {
            return Refuse<ImportUploadStartResponse>(
                ImportUploadRefusalKind.TooLarge,
                ImportUploadRefusalCodes.TooLarge,
                $"This file is larger than the {FormatBytes(maintenance.MaxUploadBytes)} the Library accepts in one upload. Copy it straight into the Import folder on the server instead.");
        }

        var libraries = _configuration.LoadLibraries();
        if (!libraries.PersonalLibraryPolicy.AllowDragAndDrop)
        {
            return Refuse<ImportUploadStartResponse>(
                ImportUploadRefusalKind.Conflict,
                ImportUploadRefusalCodes.DragAndDropOff,
                "An administrator turned off adding files by dropping them here.");
        }

        var kind = FindKind(request.KindKey);
        var mediaType = kind?.Format is { } format ? MediaTypeParser.Parse(format) : MediaType.Unknown;
        if (kind is null || mediaType == MediaType.Unknown)
        {
            return Refuse<ImportUploadStartResponse>(ImportUploadRefusalKind.BadRequest, ImportUploadRefusalCodes.UnknownKind, "That kind of media cannot be added this way.");
        }

        var importRoot = _importFolders.GetImportPath();
        if (string.IsNullOrWhiteSpace(importRoot))
        {
            return Refuse<ImportUploadStartResponse>(ImportUploadRefusalKind.Conflict, ImportUploadRefusalCodes.NoImportFolder, "Set an Import folder first.");
        }

        if (!PathSafety.TryNormalizeRoot(importRoot, out var root, out _)
            || !Directory.Exists(root)
            || _importFolders.CreateImportPolicy() is not { IsWritable: true })
        {
            return Refuse<ImportUploadStartResponse>(
                ImportUploadRefusalKind.Conflict,
                ImportUploadRefusalCodes.DestinationUnavailable,
                "The Import folder can't be reached or isn't writable right now.");
        }

        var destination = _importFolders.ResolveDestination(mediaType, request.TotalBytes);
        switch (destination.State)
        {
            case ImportDestinationState.NoManagedFolder:
                return Refuse<ImportUploadStartResponse>(
                    ImportUploadRefusalKind.Conflict,
                    ImportUploadRefusalCodes.NoManagedDestination,
                    $"{destination.TypeLabel} has no folder Library manages yet, so there is nowhere to file this. Set one up first.");
            case ImportDestinationState.Unavailable:
                return IsOutOfSpace(destination, request.TotalBytes)
                    ? Refuse<ImportUploadStartResponse>(
                        ImportUploadRefusalKind.InsufficientStorage,
                        ImportUploadRefusalCodes.NotEnoughSpace,
                        $"There isn't enough room in the {destination.TypeLabel} folder for this file.")
                    : Refuse<ImportUploadStartResponse>(
                        ImportUploadRefusalKind.Conflict,
                        ImportUploadRefusalCodes.DestinationUnavailable,
                        $"The {destination.TypeLabel} folder can't take files right now. {destination.Reason}".TrimEnd());
        }

        if (destination.Library is null)
        {
            return Refuse<ImportUploadStartResponse>(
                ImportUploadRefusalKind.Conflict,
                ImportUploadRefusalCodes.NoManagedDestination,
                $"{destination.TypeLabel} has no folder Library manages yet, so there is nowhere to file this. Set one up first.");
        }

        // Dropping files onto the Vault is deferred to the Vault sections spec, with the rest of the Vault extras (spec D57). Until then
        // a Vault library is refused, and the reason says so plainly.
        if (await _vault.IsVaultLibraryAsync(destination.Library.Id, ct).ConfigureAwait(false))
        {
            return VaultRefusal();
        }

        string? libraryId = null;
        if (!string.IsNullOrWhiteSpace(request.LibraryId))
        {
            var named = _importFolders.ResolveLibrariesForType(mediaType).FirstOrDefault(library =>
                string.Equals(library.Id, request.LibraryId.Trim(), StringComparison.OrdinalIgnoreCase)
                && library.PrimaryDestination is not null
                && (string.IsNullOrEmpty(library.KindKey) || string.Equals(library.KindKey, kind.Key, StringComparison.OrdinalIgnoreCase)));
            if (named is null)
            {
                return Refuse<ImportUploadStartResponse>(ImportUploadRefusalKind.BadRequest, ImportUploadRefusalCodes.UnknownLibrary, "That library isn't one this kind of file can be added to.");
            }

            // Deferred to the Vault sections spec (D57), as above.
            if (await _vault.IsVaultLibraryAsync(named.Id, ct).ConfigureAwait(false))
            {
                return VaultRefusal();
            }

            libraryId = named.Id;
        }

        // The Import folder's own drive holds the file while it arrives. Every open upload's working file was set aside at its full
        // size when it started, so the drive's free space already leaves those out: only this file is still to be counted, never the
        // other uploads' unsent bytes a second time.
        var required = request.TotalBytes + Math.Max(0, _options.Value.UploadFreeSpaceBufferBytes);
        if (_availableBytes(root!) is { } free && free < required)
        {
            return Refuse<ImportUploadStartResponse>(
                ImportUploadRefusalKind.InsufficientStorage,
                ImportUploadRefusalCodes.NotEnoughSpace,
                "There isn't enough room in the Import folder for this file.");
        }

        var pieceBytes = PieceBytes(maintenance);
        var pieceCount = checked((int)((request.TotalBytes + pieceBytes - 1) / pieceBytes));
        var folder = Path.Combine(root!, ImportFolderRules.SubfolderFor(destination.TypeLabel));
        var workingFolder = Path.Combine(root!, ImportFolderRules.WorkingFolderName, UploadsFolderName);
        // Judged by where links and junctions lead: a sub-folder or the working folder that points out of the Import folder is refused
        // (the final move must be a rename on the Import folder's own drive).
        if (!PathSafety.TryNormalizePath(folder, out var destinationFolder, out _)
            || !ImportUploadPaths.IsInside(destinationFolder!, root!)
            || !ImportUploadPaths.IsInside(workingFolder, root!))
        {
            return Refuse<ImportUploadStartResponse>(ImportUploadRefusalKind.Conflict, ImportUploadRefusalCodes.DestinationUnavailable, "The Import folder can't take files right now.");
        }

        var id = Guid.NewGuid();
        var workingPath = WorkingPathFor(root!, id);
        var created = await CreateWorkingFileAsync(workingPath, request.TotalBytes).ConfigureAwait(false);
        if (!created)
        {
            return Refuse<ImportUploadStartResponse>(
                ImportUploadRefusalKind.InsufficientStorage,
                ImportUploadRefusalCodes.NotEnoughSpace,
                "There isn't enough room in the Import folder for this file.");
        }

        var now = _time.GetUtcNow();
        try
        {
            await _uploads.CreateAsync(
                new ImportUpload(
                    id, owner, kind.Key, libraryId, destination.TypeLabel, request.FileName, request.TotalBytes, pieceBytes, pieceCount,
                    workingPath, destinationFolder!, ImportUpload.OpenState, null, now, now, null),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing may be left behind by a start that did not complete.
            DeleteWorkingFile(workingPath);
            _logger.LogWarning(ex, "Could not record the upload of {FileName}; its working file was removed", request.FileName);
            throw;
        }

        _logger.LogInformation(
            "Started an upload of {FileName} ({Bytes} bytes in {Pieces} pieces) for the {Kind} Import folder",
            request.FileName, request.TotalBytes, pieceCount, kind.Key);
        return ImportUploadResult<ImportUploadStartResponse>.Ok(new ImportUploadStartResponse
        {
            UploadId = id,
            PieceBytes = pieceBytes,
            PieceCount = pieceCount,
            TotalBytes = request.TotalBytes,
        });
    }

    /// <summary>The pieces received so far, so a client that lost its connection resumes without resending them.</summary>
    public async Task<ImportUploadResult<ImportUploadStatusResponse>> GetStatusAsync(
        Guid owner, Guid uploadId, CancellationToken ct = default)
    {
        var upload = await FindOwnedAsync(owner, uploadId, ct).ConfigureAwait(false);
        return upload is null
            ? NotFound<ImportUploadStatusResponse>()
            : ImportUploadResult<ImportUploadStatusResponse>.Ok(await BuildStatusAsync(upload, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Writes one piece at its place in the working file and records it. Sending a piece that was already received is accepted and
    /// changes nothing. <paramref name="declaredLength"/> is the request's Content-Length when it has one.
    /// </summary>
    public async Task<ImportUploadResult<ImportUploadStatusResponse>> WritePieceAsync(
        Guid owner, Guid uploadId, int index, long? declaredLength, Stream body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var upload = await FindOwnedAsync(owner, uploadId, ct).ConfigureAwait(false);
        if (upload is null)
        {
            return NotFound<ImportUploadStatusResponse>();
        }

        if (!upload.IsOpen)
        {
            return Closed<ImportUploadStatusResponse>(upload);
        }

        if (index < 0 || index >= upload.PieceCount)
        {
            return BadPiece("That piece number is not part of this upload.");
        }

        var expected = upload.ExpectedPieceLength(index);
        if (declaredLength is { } declared && declared != expected)
        {
            return BadPiece($"Piece {index} must be exactly {expected} bytes.");
        }

        var gate = _gates.GetOrAdd(uploadId, static _ => new ImportUploadGate());
        if (!gate.TryEnterWriter(out var stopToken))
        {
            return Closed<ImportUploadStatusResponse>(upload);
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopToken);

            // Two copies of the same piece arriving at once are written one after the other: the second waits, then finds the piece
            // already received and changes nothing.
            var pieceLock = gate.PieceLock(index);
            await pieceLock.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var alreadyReceived = (await _uploads.ListPiecesAsync(uploadId, ct).ConfigureAwait(false)).Contains(index);
                if (alreadyReceived)
                {
                    // The piece is on disk already: read the request out so the connection stays usable, and change nothing.
                    await body.CopyToAsync(Stream.Null, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    var outcome = await WriteToWorkingFileAsync(upload, index, expected, body, linked.Token).ConfigureAwait(false);
                    if (outcome is not null)
                    {
                        return BadPiece(outcome);
                    }

                    await _uploads.RecordPieceAsync(uploadId, index, _time.GetUtcNow(), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                pieceLock.Release();
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Cancel (or the abandoned-upload sweep) stopped this piece on purpose.
            return Closed<ImportUploadStatusResponse>(upload);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The working file is gone (cancelled or swept at the same moment, or deleted by hand): the upload cannot go on.
            _logger.LogWarning(ex, "The working file of upload {UploadId} is gone", uploadId);
            return Closed<ImportUploadStatusResponse>(upload);
        }
        finally
        {
            gate.ExitWriter();
        }

        var current = await _uploads.GetAsync(uploadId, ct).ConfigureAwait(false) ?? upload;
        return current.IsOpen
            ? ImportUploadResult<ImportUploadStatusResponse>.Ok(await BuildStatusAsync(current, ct).ConfigureAwait(false))
            : Closed<ImportUploadStatusResponse>(current);
    }

    /// <summary>
    /// Checks every piece arrived and the file is the size promised, then moves it into the kind's Import sub-folder under a safe unique
    /// name (never overwriting) and queues it for filing. Calling it again for an upload that already completed answers the same.
    /// </summary>
    public async Task<ImportUploadResult<ImportUploadCompleteResponse>> CompleteAsync(
        Guid owner, Guid uploadId, CancellationToken ct = default)
    {
        var found = await FindOwnedAsync(owner, uploadId, ct).ConfigureAwait(false);
        if (found is null)
        {
            return NotFound<ImportUploadCompleteResponse>();
        }

        var gate = _gates.GetOrAdd(uploadId, static _ => new ImportUploadGate());
        await gate.Exclusive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var upload = await _uploads.GetAsync(uploadId, ct).ConfigureAwait(false);
            if (upload is null || upload.CreatedBy != owner)
            {
                return NotFound<ImportUploadCompleteResponse>();
            }

            if (upload.State == ImportUpload.CompletedState)
            {
                return ImportUploadResult<ImportUploadCompleteResponse>.Ok(ToCompleteResponse(upload, upload.FinalPath ?? string.Empty));
            }

            if (!upload.IsOpen)
            {
                return Closed<ImportUploadCompleteResponse>(upload);
            }

            var pieces = await _uploads.ListPiecesAsync(uploadId, ct).ConfigureAwait(false);
            if (pieces.Count != upload.PieceCount)
            {
                return Refuse<ImportUploadCompleteResponse>(
                    ImportUploadRefusalKind.Conflict,
                    ImportUploadRefusalCodes.PiecesMissing,
                    $"{upload.PieceCount - pieces.Count} piece(s) have not arrived yet.");
            }

            await gate.CloseWritersAsync().ConfigureAwait(false);

            // From here the upload is either settled for good (completed, or cancelled because it can never succeed) or opened again
            // so the client can ask for Complete once more. It is never left closed to pieces and still open in the data store.
            var settled = false;
            string? movedTo = null;
            try
            {
                // Integrity rests on the piece count: every piece has a fixed place and an exact length, and one is only recorded once
                // all its bytes are on disk. All of them recorded plus the promised size therefore means the file is whole; there is
                // no checksum to compare against, because the browser sends none.
                var working = new FileInfo(upload.WorkingPath);
                if (!working.Exists || working.Length != upload.TotalBytes)
                {
                    // The working file was changed or removed from outside, so it can never be right: cancel it for good.
                    await AbandonAsync(upload, ct).ConfigureAwait(false);
                    settled = true;
                    return Refuse<ImportUploadCompleteResponse>(
                        ImportUploadRefusalKind.Conflict,
                        ImportUploadRefusalCodes.SizeMismatch,
                        "The file that arrived is not the size that was promised, so it was not saved. Start the upload again.");
                }

                if (!StillInsideImportFolder(upload))
                {
                    // The Import folder was moved or re-pointed: the destination this upload promised is gone, so it can never succeed.
                    await AbandonAsync(upload, ct).ConfigureAwait(false);
                    settled = true;
                    return Refuse<ImportUploadCompleteResponse>(
                        ImportUploadRefusalKind.Conflict,
                        ImportUploadRefusalCodes.DestinationUnavailable,
                        "The Import folder was changed while this file was uploading, so it was not saved. Start the upload again.");
                }

                try
                {
                    Directory.CreateDirectory(upload.DestinationFolder);
                    movedTo = MoveIntoPlace(upload);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Nothing was moved and the file is still whole: a busy or full drive can clear, so the client may ask again.
                    _logger.LogWarning(ex, "Upload {UploadId} could not be moved into {Folder}; it stays open to be completed again", uploadId, upload.DestinationFolder);
                    return Refuse<ImportUploadCompleteResponse>(
                        ImportUploadRefusalKind.Conflict,
                        ImportUploadRefusalCodes.DestinationUnavailable,
                        "The file could not be moved into the Import folder right now. Try finishing the upload again in a moment.");
                }

                if (!await _uploads.CloseAsync(uploadId, ImportUpload.CompletedState, movedTo, _time.GetUtcNow(), ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"Upload {uploadId} was closed by something else while it was being completed.");
                }

                settled = true;
                await QueueForFilingAsync(upload, movedTo, ct).ConfigureAwait(false);
                _gates.TryRemove(uploadId, out _);
                _logger.LogInformation("Upload {UploadId} completed: {FileName} is in the {Kind} Import folder", uploadId, Path.GetFileName(movedTo), upload.KindKey);
                return ImportUploadResult<ImportUploadCompleteResponse>.Ok(ToCompleteResponse(upload, movedTo));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !settled)
            {
                // Something unexpected (the data store, say) failed before the upload was settled. The caller gets a plain refusal
                // and can try again; the file goes back to its working place first if it had already been moved.
                _logger.LogError(ex, "Completing upload {UploadId} failed; it stays open to be completed again", uploadId);
                if (movedTo is not null)
                {
                    MoveBack(movedTo, upload.WorkingPath);
                }

                return Refuse<ImportUploadCompleteResponse>(
                    ImportUploadRefusalKind.Conflict,
                    ImportUploadRefusalCodes.DestinationUnavailable,
                    "The file could not be saved right now. Try finishing the upload again in a moment.");
            }
            finally
            {
                if (!settled)
                {
                    gate.Reopen();
                }
            }
        }
        finally
        {
            gate.Exclusive.Release();
        }
    }

    /// <summary>Stops the upload and deletes its hidden working file. Calling it again for a cancelled upload answers the same; a completed upload cannot be cancelled.</summary>
    public async Task<ImportUploadResult<ImportUploadStatusResponse>> CancelAsync(
        Guid owner, Guid uploadId, CancellationToken ct = default)
    {
        var found = await FindOwnedAsync(owner, uploadId, ct).ConfigureAwait(false);
        if (found is null)
        {
            return NotFound<ImportUploadStatusResponse>();
        }

        var upload = await CancelCoreAsync(uploadId, ct).ConfigureAwait(false);
        if (upload is null)
        {
            return NotFound<ImportUploadStatusResponse>();
        }

        return upload.State == ImportUpload.CancelledState
            ? ImportUploadResult<ImportUploadStatusResponse>.Ok(await BuildStatusAsync(upload, ct).ConfigureAwait(false))
            : Closed<ImportUploadStatusResponse>(upload);
    }

    /// <summary>
    /// The maintenance pass: abandons every open upload that has had no piece for <c>upload_abandon_hours</c> (deleting its working file),
    /// removes working files that belong to no open upload, and forgets finished uploads after a week. Returns how many uploads were abandoned.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var maintenance = _configuration.LoadMaintenance();
        var now = _time.GetUtcNow();
        var cutoff = now - TimeSpan.FromHours(Math.Clamp(maintenance.UploadAbandonHours, ImportUploadLimits.MinAbandonHours, ImportUploadLimits.MaxAbandonHours));

        var abandoned = 0;
        foreach (var idle in await _uploads.ListIdleOpenAsync(cutoff, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await CancelCoreAsync(idle.Id, ct).ConfigureAwait(false) is { State: ImportUpload.CancelledState })
                {
                    abandoned++;
                    _logger.LogInformation("Abandoned the upload of {FileName}: no piece for {Hours} hours", idle.FileName, maintenance.UploadAbandonHours);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete the working file of the abandoned upload {UploadId}; the next pass tries again", idle.Id);
            }
        }

        await RemoveStrayWorkingFilesAsync(cutoff, ct).ConfigureAwait(false);
        await _uploads.DeleteClosedBeforeAsync(now - ClosedRowRetention, ct).ConfigureAwait(false);
        return abandoned;
    }

    /// <summary>The path of the hidden working file for an upload id inside an Import folder.</summary>
    public static string WorkingPathFor(string importRoot, Guid uploadId) =>
        Path.Combine(importRoot, ImportFolderRules.WorkingFolderName, UploadsFolderName, uploadId.ToString("N", CultureInfo.InvariantCulture) + WorkingFileExtension);

    // Cancels an upload that can never be completed. The caller holds the upload's exclusive lock and has stopped its writers.
    private async Task AbandonAsync(ImportUpload upload, CancellationToken ct)
    {
        try
        {
            DeleteWorkingFile(upload.WorkingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Already logged by DeleteWorkingFile; the upload is closed regardless and the sweep removes a stray file later.
            _logger.LogDebug(ex, "The working file of upload {UploadId} is left for the sweep", upload.Id);
        }

        await _uploads.CloseAsync(upload.Id, ImportUpload.CancelledState, null, _time.GetUtcNow(), ct).ConfigureAwait(false);
        _gates.TryRemove(upload.Id, out _);
    }

    private void MoveBack(string movedTo, string workingPath)
    {
        try
        {
            File.Move(movedTo, workingPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file stays in the Import folder, where the normal scan files it; the upload then fails its size check and is cancelled.
            _logger.LogWarning(ex, "Could not put {File} back after a failed completion", Path.GetFileName(movedTo));
        }
    }

    private async Task<ImportUpload?> CancelCoreAsync(Guid uploadId, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(uploadId, static _ => new ImportUploadGate());
        await gate.Exclusive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var upload = await _uploads.GetAsync(uploadId, ct).ConfigureAwait(false);
            if (upload is null || !upload.IsOpen)
            {
                return upload;
            }

            // Stop the pieces still being written and wait until they let go of the file, then delete it (Windows refuses otherwise).
            await gate.CloseWritersAsync().ConfigureAwait(false);
            DeleteWorkingFile(upload.WorkingPath);
            await _uploads.CloseAsync(uploadId, ImportUpload.CancelledState, null, _time.GetUtcNow(), ct).ConfigureAwait(false);
            _gates.TryRemove(uploadId, out _);
            return await _uploads.GetAsync(uploadId, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Exclusive.Release();
        }
    }

    private async Task RemoveStrayWorkingFilesAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        if (_importFolders.GetImportPath() is not { } importRoot
            || !PathSafety.TryNormalizeRoot(importRoot, out var root, out _))
        {
            return;
        }

        var folder = Path.Combine(root!, ImportFolderRules.WorkingFolderName, UploadsFolderName);
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*" + WorkingFileExtension))
            {
                ct.ThrowIfCancellationRequested();
                var stem = Path.GetFileNameWithoutExtension(file);
                var owned = Guid.TryParseExact(stem, "N", out var id)
                    && await _uploads.GetAsync(id, ct).ConfigureAwait(false) is { IsOpen: true };
                if (!owned && File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    DeleteWorkingFile(file);
                    _logger.LogInformation("Removed the working file {File}: no open upload owns it", Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not look through the upload working folder {Folder}", folder);
        }
    }

    private async Task<string?> WriteToWorkingFileAsync(ImportUpload upload, int index, long expected, Stream body, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            await using var file = new FileStream(
                upload.WorkingPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1, useAsync: true);
            file.Seek(upload.PieceBytes * index, SeekOrigin.Begin);

            long written = 0;
            while (written < expected)
            {
                var wanted = (int)Math.Min(buffer.Length, expected - written);
                var read = await body.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
            }

            if (written != expected)
            {
                return $"Piece {index} ended after {written} bytes; it must be exactly {expected}.";
            }

            if (await body.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) > 0)
            {
                return $"Piece {index} is longer than the {expected} bytes it must be.";
            }

            // The piece is only recorded once it is safely on disk, so a restart never loses a piece it thinks it has.
            file.Flush(flushToDisk: true);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string MoveIntoPlace(ImportUpload upload)
    {
        for (var attempt = 0; attempt < MaxNumberedNames; attempt++)
        {
            var candidate = Path.Combine(upload.DestinationFolder, ImportUploadFileNames.WithNumber(upload.FileName, attempt));
            try
            {
                // Without overwrite, a name that is taken (even one taken a moment ago) is an error, never a replacement.
                File.Move(upload.WorkingPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // That name is taken: try the next number.
            }
        }

        throw new IOException($"No free name was found for {upload.FileName} in {upload.DestinationFolder}.");
    }

    private bool StillInsideImportFolder(ImportUpload upload) =>
        _importFolders.GetImportPath() is { } root
        && PathSafety.TryNormalizeRoot(root, out var normalizedRoot, out _)
        && PathSafety.TryNormalizePath(upload.DestinationFolder, out var folder, out _)
        && ImportUploadPaths.IsInside(folder!, normalizedRoot!)
        && PathSafety.TryNormalizePath(upload.WorkingPath, out var working, out _)
        && ImportUploadPaths.IsInside(working!, Path.Combine(normalizedRoot!, ImportFolderRules.WorkingFolderName));

    private async Task QueueForFilingAsync(ImportUpload upload, string finalPath, CancellationToken ct)
    {
        try
        {
            await _engine.EnqueueIntakeAsync(
                new IntakeFileRequest
                {
                    Path = finalPath,
                    SourceKind = IntakeSourceKinds.BrowserUpload,
                    SourceId = ImportFolderRules.LocationId,
                    DestinationLibraryId = upload.LibraryId,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The upload is already complete and the file is safely in the Import folder, which the Engine watches and scans, so it is
            // still found and filed. A failure to queue it must not turn a finished upload into an error.
            _logger.LogWarning(ex, "Could not queue {File} for filing straight away; the Import folder scan will pick it up", Path.GetFileName(finalPath));
        }
    }

    private async Task<bool> CreateWorkingFileAsync(string path, long totalBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var file = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    PreallocationSize = totalBytes,
                    Options = FileOptions.Asynchronous,
                });
            file.SetLength(totalBytes);
            return true;
        }
        catch (IOException ex)
        {
            // A drive that cannot hold the file says so here, before any piece is sent.
            _logger.LogWarning(ex, "Could not set aside {Bytes} bytes for an upload in the Import folder", totalBytes);
            DeleteWorkingFile(path);
            return false;
        }
    }

    private void DeleteWorkingFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the working file {File}", path);
            throw;
        }
    }

    private bool IsOutOfSpace(ImportDestination destination, long requiredBytes) =>
        destination.Source is { } source && _availableBytes(source.Path) is { } free && free < requiredBytes;

    private LibraryKindDefinition? FindKind(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var kinds = _kindSource?.GetKinds() ?? _configuration.LoadLibraryKinds().Kinds;
        return kinds.FirstOrDefault(kind =>
            kind.Available && !string.IsNullOrWhiteSpace(kind.Format)
            && string.Equals(kind.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ImportUpload?> FindOwnedAsync(Guid owner, Guid uploadId, CancellationToken ct)
    {
        var upload = await _uploads.GetAsync(uploadId, ct).ConfigureAwait(false);
        // An upload someone else started answers exactly as one that does not exist.
        return upload is not null && upload.CreatedBy == owner ? upload : null;
    }

    private async Task<ImportUploadStatusResponse> BuildStatusAsync(ImportUpload upload, CancellationToken ct)
    {
        var pieces = await _uploads.ListPiecesAsync(upload.Id, ct).ConfigureAwait(false);
        return new ImportUploadStatusResponse
        {
            UploadId = upload.Id,
            State = upload.State,
            FileName = upload.FileName,
            TotalBytes = upload.TotalBytes,
            PieceBytes = upload.PieceBytes,
            PieceCount = upload.PieceCount,
            ReceivedPieces = [.. pieces],
            ReceivedBytes = pieces.Sum(upload.ExpectedPieceLength),
        };
    }

    private static ImportUploadCompleteResponse ToCompleteResponse(ImportUpload upload, string finalPath) => new()
    {
        UploadId = upload.Id,
        FileName = Path.GetFileName(finalPath),
        Renamed = !string.Equals(Path.GetFileName(finalPath), upload.FileName, StringComparison.Ordinal),
        DestinationPath = finalPath,
    };

    private static long PieceBytes(MaintenanceSettings maintenance) =>
        Math.Clamp(maintenance.UploadPieceBytes, ImportUploadLimits.MinPieceBytes, ImportUploadLimits.MaxPieceBytes);

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB");

    private static ImportUploadResult<T> Refuse<T>(ImportUploadRefusalKind kind, string code, string message) =>
        ImportUploadResult<T>.Refused(kind, code, message);

    private static ImportUploadResult<T> NotFound<T>() =>
        Refuse<T>(ImportUploadRefusalKind.NotFound, ImportUploadRefusalCodes.UploadNotFound, "There is no such upload.");

    private static ImportUploadResult<T> Closed<T>(ImportUpload upload) =>
        Refuse<T>(
            ImportUploadRefusalKind.Conflict,
            ImportUploadRefusalCodes.UploadClosed,
            upload.State == ImportUpload.CompletedState ? "This upload is already complete." : "This upload was cancelled, or is no longer open.");

    private static ImportUploadResult<ImportUploadStartResponse> VaultRefusal() =>
        Refuse<ImportUploadStartResponse>(
            ImportUploadRefusalKind.Conflict,
            ImportUploadRefusalCodes.VaultNotAvailable,
            "Adding files to the Vault by drag and drop isn't available yet.");

    private static ImportUploadResult<ImportUploadStatusResponse> BadPiece(string message) =>
        Refuse<ImportUploadStatusResponse>(ImportUploadRefusalKind.BadRequest, ImportUploadRefusalCodes.InvalidPiece, message);
}
