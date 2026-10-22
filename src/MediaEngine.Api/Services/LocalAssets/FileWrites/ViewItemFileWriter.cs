using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
///   <item>The write goes to a hidden temporary copy beside the file. The copy is read back, and only then does it replace the original
///   (a rename in the same folder, so one step). A file that changed while the copy was written is left alone. On any failure the copy is
///   deleted and the original is exactly as it was; the copy is never deleted while the original is missing.</item>
///   <item>Afterwards the file's new fingerprint and size are recorded, so the file is not read as a new or changed one. A file that
///   changed outside Library since it was indexed is not written: it is indexed again first and written on the next run.</item>
/// </list>
/// </summary>
public sealed partial class ViewItemFileWriter(
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
    internal const string TransferNotFinishedReason = "The file is part of a Shared Library transfer that has not finished; it waits for the next run.";

    /// <summary>How old a leftover hidden copy must be before it is cleared away (a crash left it; no write takes this long to start).</summary>
    internal static readonly TimeSpan StaleCopyAge = TimeSpan.FromHours(1);

    private static readonly TimeSpan SameTimeTolerance = TimeSpan.FromSeconds(2);

    private IExifToolSession? _session;
    private bool _exifToolUnavailable;
    private readonly HashSet<string> _foldersChecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How the verified copy takes the original's place. A seam so a test can make the swap fail after the original is gone.</summary>
    internal IViewFileSwap FileSwap { get; set; } = new MoveViewFileSwap();

    public string TargetKind => MediaFileWriteIntentRepository.ViewItemKind;

    public async Task BeginRunAsync(CancellationToken ct)
    {
        await CloseSessionAsync();
        _exifToolUnavailable = false;
        _foldersChecked.Clear();
    }

    public async Task EndRunAsync(CancellationToken ct)
    {
        await CloseSessionAsync();
        _exifToolUnavailable = false;
        _foldersChecked.Clear();
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

        if (target.TransferNotFinished)
        {
            // A move to the Shared Library checks the bytes of both copies before it removes the original; writing now would break that.
            return new FileWriteResult(intent.Id, WriteBackOutcome.Failed(TransferNotFinishedReason), ReadAgainFirst: true);
        }

        var path = refusal.Path!;
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
            : string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase) ? PdfWriteSafety.Inspect(path, logger) : null;
        if (keep is not null)
        {
            return Done(WriteBackOutcome.Unsupported(keep));
        }

        if ((target.PlacesInLibrary > 1 || target.PlacesElsewhere > 0) && await CarriesNameAsync(kind, path, name, ct))
        {
            // A copy of a file that was already written (a shared copy of a named photo) has nothing left to write.
            return Done(WriteBackOutcome.Verified());
        }

        if (target.PlacesInLibrary > 1)
        {
            // One file record at two places in this library: the new fingerprint could be recorded for only one of them.
            return Done(WriteBackOutcome.Unsupported(ViewProfileNameFormats.KeptCopiedReason));
        }

        var original = new FileInfo(path);
        var snapshot = new OriginalSnapshot(original.Length, original.LastWriteTimeUtc);
        ClearStaleCopies(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!, ViewStorageService.WriteCopyPrefix + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        var attempt = new SwapAttempt();
        try
        {
            var early = kind == ViewProfileNameWriter.ExifTool
                ? await WriteCopyWithExifToolAsync(intent, path, temporary, name, snapshot.Length, ct)
                : WriteCopyWithOpenXml(intent, path, temporary, name);
            if (early is not null)
            {
                return early;
            }

            return await ReplaceOriginalAsync(intent, target, fileId, path, temporary, snapshot, attempt, ct);
        }
        finally
        {
            FinishTemporaryCopy(temporary, path, attempt);
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
        MediaFileWriteIntent intent, string path, string temporary, string name, long fileBytes, CancellationToken ct)
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
            ExifToolWriteOptions.ToCopy(temporary, fileBytes),
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

    /// <summary>
    /// Swaps the verified copy in for the original, then records the file as Library left it (D8). The original is only replaced if it is
    /// still the file the copy was made from; an edit made while the copy was written is never overwritten.
    /// </summary>
    private async Task<FileWriteResult> ReplaceOriginalAsync(
        MediaFileWriteIntent intent, ViewItemWriteTarget target, Guid fileId, string path, string temporary,
        OriginalSnapshot snapshot, SwapAttempt attempt, CancellationToken ct)
    {
        var hash = await HashAsync(temporary, ct);
        CarryOverFileProperties(path, temporary, snapshot);

        if (!snapshot.Matches(path))
        {
            logger.LogInformation(
                "The file of View item {ItemId} changed while its copy was being written; the copy was dropped and the file is read again.",
                intent.LocalItemId);
            return await ReadAgainFirstAsync(intent, target, ct);
        }

        attempt.Started = true;
        FileSwap.Replace(temporary, path);

        var size = new FileInfo(path).Length;
        var recorded = await files.RecordOwnWriteAsync(
            intent.LocalItemId!.Value, fileId, target.LibraryId, target.FilePath!, hash, size,
            new DateTimeOffset(snapshot.LastWriteUtc, TimeSpan.Zero), ct);
        if (!recorded)
        {
            logger.LogInformation(
                "The file of View item {ItemId} now matches another file Library already knows; its record keeps the old fingerprint until the folder is looked at again.",
                intent.LocalItemId);
        }

        return new FileWriteResult(intent.Id, WriteBackOutcome.Verified());
    }

    /// <summary>
    /// Gives the copy the original's creation time, attributes, permissions and modified time, so the swap changes the bytes and nothing
    /// else (a photo does not move on the Timeline, and an outside edit is still noticed by its modified time).
    /// </summary>
    private void CarryOverFileProperties(string path, string temporary, OriginalSnapshot snapshot)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            }

            File.SetCreationTimeUtc(temporary, File.GetCreationTimeUtc(path));
            File.SetAttributes(temporary, File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Cosmetic details only: the file is still replaced with the right bytes.
            logger.LogDebug(ex, "Some properties of a View file could not be carried over to its new copy.");
        }

        File.SetLastWriteTimeUtc(temporary, snapshot.LastWriteUtc);
    }

    /// <summary>
    /// Removes the hidden copy once the write is over. The one rule that never bends: the copy is not deleted while the original is
    /// missing after a swap was started, because then it is the only copy of the file. It is moved into place instead.
    /// </summary>
    private void FinishTemporaryCopy(string temporary, string path, SwapAttempt attempt)
    {
        try
        {
            if (!File.Exists(temporary))
            {
                return;
            }

            if (attempt.Started && !File.Exists(path))
            {
                File.Move(temporary, path);
                logger.LogWarning(
                    "Replacing a View file failed after the original was gone; the new copy was put in its place so the file is not lost.");
                return;
            }

            File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The hidden copy is left where it is (a later scan ignores it, and the next write clears it once it is old).
            logger.LogWarning(ex, "A temporary copy beside a View file could not be removed or put in place.");
        }
    }

    /// <summary>Clears hidden copies a crash left in this folder: only files of exactly the hidden-copy name, over an hour old, never through a link.</summary>
    private void ClearStaleCopies(string folder)
    {
        if (!_foldersChecked.Add(folder))
        {
            return;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateFiles(folder, ViewStorageService.WriteCopyPrefix + "*"))
            {
                var info = new FileInfo(candidate);
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || !StaleCopyName().IsMatch(info.Name))
                {
                    continue;
                }

                var newest = info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
                if (DateTime.UtcNow - newest > StaleCopyAge)
                {
                    info.Delete();
                    logger.LogInformation("Removed a leftover temporary copy beside a View file.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Leftover temporary copies in a View folder could not be cleared.");
        }
    }

    [GeneratedRegex(@"^\.tuvima-write-[0-9a-f]{32}(\.[A-Za-z0-9]+)?$")]
    private static partial Regex StaleCopyName();

    /// <summary>True when the file already holds exactly this profile name.</summary>
    private async Task<bool> CarriesNameAsync(ViewProfileNameWriter kind, string path, string name, CancellationToken ct)
    {
        try
        {
            if (kind == ViewProfileNameWriter.OpenXml)
            {
                return string.Equals(OfficeProfileName.Read(path), name, StringComparison.Ordinal);
            }

            var session = await GetSessionAsync(ct);
            if (session is null || !session.IsRunning)
            {
                return false;
            }

            var read = await session.ReadAsync(path, [ViewProfileNameFormats.ExifToolTag], ct);
            return read.TryGetValue(ViewProfileNameFormats.ExifToolTag, out var value) && string.Equals(value.Trim(), name, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Whether a View file already carries the profile name could not be read; it is treated as not carrying it.");
            return false;
        }
    }

    /// <summary>The length and modified time the original had when it was checked, to notice an edit made while the copy was written.</summary>
    private sealed record OriginalSnapshot(long Length, DateTime LastWriteUtc)
    {
        public bool Matches(string path)
        {
            var now = new FileInfo(path);
            return now.Exists && now.Length == Length && now.LastWriteTimeUtc == LastWriteUtc;
        }
    }

    /// <summary>Whether the swap of the copy for the original was begun, so clean-up knows the original may have been taken away.</summary>
    private sealed class SwapAttempt
    {
        public bool Started { get; set; }
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

/// <summary>How a verified copy takes an original's place (batched write-back D9).</summary>
internal interface IViewFileSwap
{
    void Replace(string temporary, string path);
}

/// <summary>
/// A rename over the original. The copy is beside the original, so on the same volume this is one step and keeps the folder entry; it
/// never needs a backup file. (<c>File.Replace</c> without a backup can, on Windows, leave only the copy when it fails part-way.)
/// </summary>
internal sealed class MoveViewFileSwap : IViewFileSwap
{
    public void Replace(string temporary, string path) => File.Move(temporary, path, overwrite: true);
}
