using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;

namespace MediaEngine.Api.Services.Extras;

/// <summary>What a Belongs to change does to one supporting file on disk: a move, or the plain reason the file stays where it is.</summary>
/// <param name="From">The file's place now; null when nothing moves.</param>
/// <param name="To">The place it moves to; null when nothing moves.</param>
/// <param name="Note">One plain line about the file; null when nothing needs saying (it already sits where the change would put it).</param>
public sealed record SupportingFileMovePlan(string? From, string? To, string? Note)
{
    public bool Moves => From is not null && To is not null;

    public static SupportingFileMovePlan Stays(string? note = null) => new(null, null, note);
}

/// <summary>What a delete did to the file on disk (<see cref="SupportingFileDiskOutcomes"/>).</summary>
public sealed record SupportingFileDiskResult(string Outcome, string? Reason);

/// <summary>
/// Everything that touches a supporting file on disk (universal editor v5 D29, D30): moving it when Belongs to changes in a Managed
/// folder, and deleting it by the library's file-management rules. The source-file safety gate decides, every time, whether a file may
/// move or go; a Read-only folder's files are only ever read. A file that stays on disk after a delete is remembered as excluded, so a
/// scan does not list it again (the same as deleting a title from the Library).
/// </summary>
public sealed class SupportingFileDiskService(
    ILibraryFolderResolver folders,
    ISourceMutationPolicyGate gate,
    IItemExtrasScanner scanner,
    IExcludedSourceFileRepository excludedFiles,
    IExcludedSourceFileFilter excludedFilter,
    AssetPathService assetPaths,
    ILogger<SupportingFileDiskService> logger)
{
    public const string StaysWhereItIs = "Set by you · file stays where it is";
    public const string NoFolderForPlace = "There is no folder for that place, so the file stays where it is.";
    public const string NameTaken = "A file with that name is already there, so this file stays where it is.";
    public const string FileNotFound = "The file is no longer where Library found it, so only its place in the Library changes.";
    public const string MoveFailed = "The file could not be moved (it may be in use). Your other changes are saved.";
    public const string InUse = "The file is in use";
    public const string NotAllowed = "Library isn't allowed to change this file";
    public const string OutsideLibrary = "This file is not inside any library folder Library manages.";

    private RecycleBinService? _bin;

    private RecycleBinService Bin => _bin ??= new RecycleBinService(assetPaths.DataRoot, logger);

    /// <summary>
    /// Works out where the file would go if it belonged to <paramref name="scope"/>. Nothing moves in a Read-only folder or a folder Library
    /// may not write to, for a file that is not on disk, or when the new place has no folder or already holds a file of that name.
    /// <paramref name="claimed"/> holds the targets other files of the same change have taken, so two files never take one name.
    /// </summary>
    public async Task<SupportingFileMovePlan> PlanMoveAsync(
        ItemExtra extra, ExtraScope scope, ISet<string> claimed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(extra);
        ArgumentNullException.ThrowIfNull(claimed);

        var resolved = folders.ResolveSourceForPath(extra.FilePath);
        if (resolved is null || !resolved.Source.IsManaged || !resolved.Source.IsWritable)
        {
            return SupportingFileMovePlan.Stays(StaysWhereItIs);
        }

        var policy = FileSourceMutationPolicyFactory.Create(resolved.Library, resolved.Source);
        var verdict = gate.Evaluate(new SourceMutationRequest { Source = policy, Mutation = SourceMutationKind.Move, Path = extra.FilePath });
        if (!verdict.Allowed)
        {
            return SupportingFileMovePlan.Stays(StaysWhereItIs);
        }

        if (!File.Exists(extra.FilePath))
        {
            return SupportingFileMovePlan.Stays(FileNotFound);
        }

        var destination = await scanner.ResolveScopeFolderAsync(extra.ItemFolder, scope, ct).ConfigureAwait(false);
        if (destination is null)
        {
            return SupportingFileMovePlan.Stays(NoFolderForPlace);
        }

        var from = Path.GetFullPath(extra.FilePath);
        var to = Path.Combine(Path.GetFullPath(destination), Path.GetFileName(from));
        if (string.Equals(Path.GetDirectoryName(from), Path.GetDirectoryName(to), StringComparison.OrdinalIgnoreCase))
        {
            return SupportingFileMovePlan.Stays();
        }

        if (File.Exists(to) || !claimed.Add(to))
        {
            return SupportingFileMovePlan.Stays(NameTaken);
        }

        return new SupportingFileMovePlan(from, to, null);
    }

    /// <summary>Moves the file. Returns null when it moved, otherwise the reason it did not (the data change stays saved).</summary>
    public string? Move(SupportingFileMovePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Moves)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.To!)!);
            File.Move(plan.From!, plan.To!);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "A supporting file could not be moved to its new place.");
            return MoveFailed;
        }
    }

    /// <summary>
    /// Deletes the file by the library's rules: through the safety gate, to the Recycle bin when the library has it on, otherwise from the
    /// disk. A file the gate keeps (a Read-only folder) or that could not be removed stays and is remembered as excluded.
    /// </summary>
    public async Task<SupportingFileDiskResult> DeleteFileAsync(
        ItemExtra extra, string? title, Guid? accountId, string? deletedBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(extra);
        var path = extra.FilePath;
        if (!File.Exists(path))
        {
            return new SupportingFileDiskResult(SupportingFileDiskOutcomes.Gone, null);
        }

        var resolved = folders.ResolveSourceForPath(path);
        if (resolved is null)
        {
            return new SupportingFileDiskResult(SupportingFileDiskOutcomes.Kept, OutsideLibrary);
        }

        var policy = FileSourceMutationPolicyFactory.Create(resolved.Library, resolved.Source, allowDelete: true);
        var verdict = gate.Evaluate(new SourceMutationRequest { Source = policy, Mutation = SourceMutationKind.Delete, Path = path });
        if (!verdict.Allowed)
        {
            var reason = resolved.Source.IsManaged ? ExcludedSourceFileReasons.Protected : ExcludedSourceFileReasons.AsIs;
            await ExcludeAsync(resolved, path, title, accountId, deletedBy, reason, ct).ConfigureAwait(false);
            return new SupportingFileDiskResult(SupportingFileDiskOutcomes.Kept, verdict.Reason ?? NotAllowed);
        }

        try
        {
            if (resolved.Library.RecycleBin)
            {
                var mediaType = resolved.Library.MediaTypes.Select(type => type.ToString()).FirstOrDefault(name => name != nameof(MediaType.Unknown));
                Bin.MoveIn(path, title, resolved.Library.Id, mediaType, string.IsNullOrWhiteSpace(deletedBy) ? null : deletedBy.Trim());
                return new SupportingFileDiskResult(SupportingFileDiskOutcomes.Recycled, null);
            }

            File.Delete(path);
            return new SupportingFileDiskResult(SupportingFileDiskOutcomes.Deleted, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Removed by something else a moment ago: the result is the one wanted.
            logger.LogDebug(ex, "A supporting file was already gone when it was deleted.");
            return new SupportingFileDiskResult(SupportingFileDiskOutcomes.Gone, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "A supporting file could not be deleted; it stays on disk.");
            await ExcludeAsync(resolved, path, title, accountId, deletedBy, ExcludedSourceFileReasons.DeleteFailed, ct).ConfigureAwait(false);
            return new SupportingFileDiskResult(
                SupportingFileDiskOutcomes.Kept, ex is UnauthorizedAccessException ? NotAllowed : InUse);
        }
    }

    private async Task ExcludeAsync(
        ResolvedLibrarySource resolved, string path, string? title, Guid? accountId, string? name, string reason, CancellationToken ct)
    {
        var relative = ExcludedSourceFilePaths.RelativePathOf(Path.GetFullPath(resolved.Source.Path), Path.GetFullPath(path));
        if (relative is null)
        {
            logger.LogWarning("A supporting file is not inside its source folder, so it was not remembered as excluded.");
            return;
        }

        long? size = null;
        DateTimeOffset? lastWrite = null;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The facts only help notice a changed file; the exclusion itself is still recorded.
            logger.LogDebug(ex, "The size of an excluded supporting file could not be read.");
        }

        try
        {
            await excludedFiles.AddOrReplaceAsync(
                new ExcludedSourceFile(
                    Guid.NewGuid(), resolved.Library.Id, resolved.Source.Id, relative, null, size, lastWrite,
                    title, DateTimeOffset.UtcNow, accountId, name, reason),
                ct).ConfigureAwait(false);
        }
        finally
        {
            excludedFilter.Invalidate();
        }
    }
}
