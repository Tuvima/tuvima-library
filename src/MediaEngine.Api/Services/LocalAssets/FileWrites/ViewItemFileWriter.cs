using System.Security.Cryptography;
using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Api.Services.Tools.ExifTool;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.LocalAssets.FileWrites;

/// <summary>
/// Writes the name of the profile that added a View item into the item's file (batched write-back D7 to D9), for the scheduler. The only
/// place in the Engine that changes a View file.
/// <list type="bullet">
///   <item>Only a file in a Managed folder on writable storage, and only where the shared source-file safety gate allows a metadata write
///   for that exact file. A Read-only (linked) folder's files are never written; a refusal is reported as blocked.</item>
///   <item>JPEG, PNG, TIFF, GIF, WebP, HEIC, AVIF, camera RAW, MP4/MOV/M4V and PDF go through ExifTool, one started process for the whole
///   run. Word, Excel and PowerPoint go through Open XML. Every other kind, and a signed or encrypted PDF or Office file, is kept in
///   Library only and the file is not opened for writing.</item>
///   <item>The write goes to a hidden temporary copy beside the file. The copy is read back, and only then does it replace the original in
///   one step. On any failure the copy is deleted and the original is exactly as it was.</item>
///   <item>Afterwards the file's new fingerprint and size are recorded, so the file is not read as a new or changed one. A file that
///   changed outside Library since it was indexed is not written: it is indexed again first and written on the next run.</item>
/// </list>
/// </summary>
public sealed class ViewItemFileWriter(
    ViewItemFileWriteRepository files,
    IProfileRepository profiles,
    IViewPersonalSpaceRepository spaces,
    ViewStorageService storage,
    ISourceMutationPolicyGate gate,
    IExifToolRunner exifTool,
    IViewPathIndexer indexer,
    IConfigurationLoader configuration,
    ILogger<ViewItemFileWriter> logger) : IFileWriteTargetWriter
{
    internal const string WaitingForExifToolReason = "Waiting for ExifTool.";
    internal const string ExifToolStoppedReason = "ExifTool stopped; this file waits for the next run.";
    internal const string FileChangedReason = "The file changed outside Library; it will be read again before writing.";
    internal const string WriteBackOffReason = "Write-back is off, so the profile name was not written.";
    internal const string NotManagedReason = "This file is not in a folder Library manages.";

    private static readonly TimeSpan SameTimeTolerance = TimeSpan.FromSeconds(2);

    private IExifToolSession? _session;
    private bool _exifToolUnavailable;

    public string TargetKind => MediaFileWriteIntentRepository.ViewItemKind;

    public async Task BeginRunAsync(CancellationToken ct)
    {
        await CloseSessionAsync();
        _exifToolUnavailable = false;
    }

    public async Task EndRunAsync(CancellationToken ct)
    {
        await CloseSessionAsync();
        _exifToolUnavailable = false;
    }

    public async Task<IReadOnlyList<FileWriteResult>> WriteBatchAsync(IReadOnlyList<MediaFileWriteIntent> intents, CancellationToken ct)
    {
        var settings = configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback") ?? new WriteBackConfiguration();
        var results = new List<FileWriteResult>(intents.Count);
        foreach (var intent in intents)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await WriteOneSafelyAsync(intent, settings, ct));
        }

        return results;
    }

    private async Task<FileWriteResult> WriteOneSafelyAsync(MediaFileWriteIntent intent, WriteBackConfiguration settings, CancellationToken ct)
    {
        try
        {
            return await WriteOneAsync(intent, settings, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A stop leaves the row claimed; its lease runs out and the next run writes it.
            throw;
        }
        catch (Exception ex)
        {
            // One file failing never stops the others; the row is retried up to the attempt limit.
            logger.LogWarning(ex, "Writing the profile name into the file of View item {ItemId} failed.", intent.LocalItemId);
            return new FileWriteResult(intent.Id, WriteBackOutcome.Failed(ex.Message));
        }
    }

    private async Task<FileWriteResult> WriteOneAsync(MediaFileWriteIntent intent, WriteBackConfiguration settings, CancellationToken ct)
    {
        FileWriteResult Done(WriteBackOutcome outcome) => new(intent.Id, outcome);

        if (intent.LocalItemId is not { } itemId)
        {
            return Done(WriteBackOutcome.Failed("The entry names no View item."));
        }

        if (!settings.Enabled)
        {
            return Done(WriteBackOutcome.Blocked(WriteBackOffReason));
        }

        var target = await files.GetTargetAsync(itemId, ct);
        if (target is null || target.AddedByProfileId is not { } adder)
        {
            // The item is gone, or nobody is recorded as having added it: there is nothing to write.
            return Done(WriteBackOutcome.Verified());
        }

        // The newest value wins: the name is read now, not when the entry was queued.
        var name = ViewProfileNameFormats.Clean((await profiles.GetByIdAsync(adder, ct))?.DisplayName);
        if (name.Length == 0 || target.FileId is not { } fileId || target.FilePath is not { } recordedPath
            || string.IsNullOrWhiteSpace(target.ContentHash))
        {
            return Done(WriteBackOutcome.Verified());
        }

        var kind = ViewProfileNameFormats.For(Path.GetExtension(recordedPath));
        if (kind == ViewProfileNameWriter.KeptInLibrary)
        {
            return Done(WriteBackOutcome.Unsupported(ViewProfileNameFormats.KeptFormatReason));
        }

        var refusal = await CheckSafetyAsync(target, recordedPath, ct);
        if (refusal.Reason is not null)
        {
            return Done(WriteBackOutcome.Blocked(refusal.Reason));
        }

        var path = refusal.Path!;
        if (target.SourceCount > 1)
        {
            // One file record at two places (a copy kept in the shared library): the new fingerprint could be recorded for only one.
            return Done(WriteBackOutcome.Unsupported(ViewProfileNameFormats.KeptCopiedReason));
        }

        if (!File.Exists(path))
        {
            return Done(WriteBackOutcome.Failed("The file is no longer where Library recorded it."));
        }

        if (await ChangedOutsideAsync(target, path, ct))
        {
            return await ReadAgainFirstAsync(intent, target, ct);
        }

        var keep = kind == ViewProfileNameWriter.OpenXml
            ? OfficeProfileName.Inspect(path)
            : string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase) ? PdfWriteSafety.Inspect(path) : null;
        if (keep is not null)
        {
            return Done(WriteBackOutcome.Unsupported(keep));
        }

        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!, ViewStorageService.WriteCopyPrefix + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        try
        {
            var early = kind == ViewProfileNameWriter.ExifTool
                ? await WriteCopyWithExifToolAsync(intent, path, temporary, name, ct)
                : WriteCopyWithOpenXml(intent, path, temporary, name);
            if (early is not null)
            {
                return early;
            }

            return await ReplaceOriginalAsync(intent, target, fileId, path, temporary, ct);
        }
        finally
        {
            DeleteTemporaryCopy(temporary);
        }
    }

    // ----- Safety -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Follows the file's path through every link and asks the shared source-file safety gate whether a metadata write is allowed for
    /// exactly this file, now. Returns the real path to write, or the reason it must be left alone.
    /// </summary>
    private async Task<(string? Path, string? Reason)> CheckSafetyAsync(ViewItemWriteTarget target, string recordedPath, CancellationToken ct)
    {
        if (target.SourceId is not { } sourceId || target.StorageMode is null)
        {
            return (null, NotManagedReason);
        }

        bool writable;
        string root;
        try
        {
            writable = storage.IsStorageWritable();
            if (!string.Equals(target.StorageMode, "managed", StringComparison.Ordinal))
            {
                // A Read-only folder: the gate is asked all the same, and says so in its own words.
                var linked = ViewSourceMutationPolicyFactory.CreateForRoot(
                    target.LibraryId, sourceId, managed: false, Path.GetDirectoryName(recordedPath) ?? recordedPath, writable);
                return (null, Evaluate(linked, recordedPath).Reason ?? NotManagedReason);
            }

            if (target.PersonalSpaceId is null)
            {
                root = storage.GetSharedRoot();
            }
            else
            {
                var space = await spaces.GetByLibraryAsync(target.LibraryId, ct);
                if (space is null || space.Id != target.PersonalSpaceId)
                {
                    return (null, NotManagedReason);
                }

                root = storage.GetProfileRoot(space, requireWrite: false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException
            or MediaEngine.Storage.Configuration.ConfigValidationException)
        {
            // Without a verified folder nothing can be shown to be inside it, so the file is left alone.
            logger.LogWarning(ex, "View storage could not be resolved while writing a profile name.");
            return (null, "View storage could not be checked, so the file was left as it is.");
        }

        if (!ViewStorageService.TryResolveContainedPath(root, recordedPath, out var resolved, out var reason))
        {
            return (null, reason);
        }

        var policy = ViewSourceMutationPolicyFactory.CreateForRoot(target.LibraryId, sourceId, managed: true, resolved.Root, writable);
        var verdict = Evaluate(policy, resolved.Path);
        return verdict.Reason is null ? (resolved.Path, null) : (null, verdict.Reason);
    }

    private (bool Allowed, string? Reason) Evaluate(FileSourceMutationPolicy policy, string path)
    {
        var verdict = gate.Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = SourceMutationKind.MetadataWriteback,
            Path = path,
        });
        return verdict.Allowed ? (true, null) : (false, verdict.Reason ?? "The source-file safety gate refused the write.");
    }

    // ----- Outside changes (D8) ---------------------------------------------------------------------------------------------

    /// <summary>
    /// True when the file is not the one Library recorded: a different size, or a different modified time and a different fingerprint.
    /// Most files are not read at all, because their size and time still match.
    /// </summary>
    private static async Task<bool> ChangedOutsideAsync(ViewItemWriteTarget target, string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length != target.ByteSize)
        {
            return true;
        }

        if (target.ModifiedAt is not { } recorded)
        {
            return false;
        }

        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if ((modified - recorded).Duration() <= SameTimeTolerance)
        {
            return false;
        }

        return !string.Equals(await HashAsync(path, ct), target.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<FileWriteResult> ReadAgainFirstAsync(MediaFileWriteIntent intent, ViewItemWriteTarget target, CancellationToken ct)
    {
        try
        {
            var indexed = await indexer.IndexPathAsync(target.LibraryId, target.FilePath!, ct);
            if (indexed is not null)
            {
                return new FileWriteResult(intent.Id, WriteBackOutcome.Failed(FileChangedReason), ReadAgainFirst: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Reading the changed file of View item {ItemId} again failed.", intent.LocalItemId);
        }

        return new FileWriteResult(
            intent.Id, WriteBackOutcome.Failed("The file changed outside Library and could not be read again; it was not written."));
    }

    // ----- Writing the copy (D9) --------------------------------------------------------------------------------------------

    private async Task<FileWriteResult?> WriteCopyWithExifToolAsync(
        MediaFileWriteIntent intent, string path, string temporary, string name, CancellationToken ct)
    {
        var session = await GetSessionAsync(ct);
        if (session is null)
        {
            // ExifTool is not ready (missing, or no Perl): the file waits and the attempt is not spent.
            return new FileWriteResult(intent.Id, WriteBackOutcome.Failed(WaitingForExifToolReason), ReadAgainFirst: true);
        }

        if (!session.IsRunning)
        {
            return new FileWriteResult(intent.Id, WriteBackOutcome.Failed(ExifToolStoppedReason), ReadAgainFirst: true);
        }

        var write = await session.WriteAsync(
            path,
            new Dictionary<string, string> { [ViewProfileNameFormats.ExifToolTag] = name },
            ExifToolWriteOptions.ToCopy(temporary),
            ct);
        if (write.Outcome == ExifToolWriteOutcome.Unsupported)
        {
            return new FileWriteResult(
                intent.Id, WriteBackOutcome.Unsupported("Kept in Library only: " + (write.Reason ?? "ExifTool does not write this kind of file.")));
        }

        if (!write.IsSuccess)
        {
            return new FileWriteResult(intent.Id, WriteBackOutcome.Failed(write.Reason ?? "ExifTool could not write the file."));
        }

        if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
        {
            return new FileWriteResult(intent.Id, WriteBackOutcome.Failed("ExifTool produced no copy of the file."));
        }

        var back = await session.ReadAsync(temporary, [ViewProfileNameFormats.ExifToolTag], ct);
        return back.TryGetValue(ViewProfileNameFormats.ExifToolTag, out var value) && string.Equals(value.Trim(), name, StringComparison.Ordinal)
            ? null
            : new FileWriteResult(intent.Id, WriteBackOutcome.Failed("The profile name could not be read back from the new copy of the file."));
    }

    private static FileWriteResult? WriteCopyWithOpenXml(MediaFileWriteIntent intent, string path, string temporary, string name)
    {
        File.Copy(path, temporary);
        OfficeProfileName.Write(temporary, name);
        return string.Equals(OfficeProfileName.Read(temporary), name, StringComparison.Ordinal)
            ? null
            : new FileWriteResult(intent.Id, WriteBackOutcome.Failed("The profile name could not be read back from the new copy of the file."));
    }

    /// <summary>Swaps the verified copy in for the original in one step, then records the file as Library left it (D8).</summary>
    private async Task<FileWriteResult> ReplaceOriginalAsync(
        MediaFileWriteIntent intent, ViewItemWriteTarget target, Guid fileId, string path, string temporary, CancellationToken ct)
    {
        var hash = await HashAsync(temporary, ct);
        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
        }

        File.Replace(temporary, path, null, ignoreMetadataErrors: true);

        // The file keeps the modified time it had, so a photo does not move on the Timeline and an outside edit is still noticed.
        File.SetLastWriteTimeUtc(path, lastWrite);
        var size = new FileInfo(path).Length;
        var recorded = await files.RecordOwnWriteAsync(
            fileId, target.LibraryId, target.FilePath!, hash, size, new DateTimeOffset(lastWrite, TimeSpan.Zero), ct);
        if (!recorded)
        {
            logger.LogInformation(
                "The file of View item {ItemId} now matches another file Library already knows; its record keeps the old fingerprint until the folder is looked at again.",
                intent.LocalItemId);
        }

        return new FileWriteResult(intent.Id, WriteBackOutcome.Verified());
    }

    private void DeleteTemporaryCopy(string temporary)
    {
        try
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original is untouched; only a hidden temporary file is left behind, which a later scan ignores.
            logger.LogWarning(ex, "A temporary copy beside a View file could not be removed.");
        }
    }

    // ----- ExifTool session (D6) --------------------------------------------------------------------------------------------

    private async Task<IExifToolSession?> GetSessionAsync(CancellationToken ct)
    {
        if (_session is not null)
        {
            return _session;
        }

        if (_exifToolUnavailable)
        {
            return null;
        }

        _session = await exifTool.OpenSessionAsync(ct);
        _exifToolUnavailable = _session is null;
        return _session;
    }

    private async Task CloseSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "ExifTool did not close cleanly after the run.");
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
