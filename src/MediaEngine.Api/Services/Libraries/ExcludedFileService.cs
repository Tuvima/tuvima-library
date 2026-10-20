using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Organise;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// The files that stayed on disk when their title was deleted from the library: lists them for Settings and puts one back. The list
/// carries only file names and readable folder labels, never a disk path. "Add back" removes the exclusion, makes the scans forget it
/// and asks the ingestion engine to take the file in as if a scan had just found it.
/// </summary>
public sealed class ExcludedFileService(
    IExcludedSourceFileRepository excludedFiles,
    ILibraryFolderResolver folders,
    IExcludedSourceFileFilter excludedFileFilter,
    IFileIntake intake,
    ILogger<ExcludedFileService> logger)
{
    public const string QueuedMessage = "Added back. Tuvima will scan it again.";
    public const string MissingMessage = "The file is no longer on disk.";
    public const string NotQueuedMessage = "Added back, but Tuvima could not start the scan. The next scan will pick it up.";
    public const string FolderGoneMessage = "Added back, but its library folder is no longer set up.";

    private const string LabelSeparator = " › ";

    /// <summary>The excluded files, newest first: one library's when <paramref name="libraryId"/> is given, otherwise all.</summary>
    public async Task<IReadOnlyList<ExcludedFileDto>> ListAsync(string? libraryId, CancellationToken ct = default)
    {
        var rows = string.IsNullOrWhiteSpace(libraryId)
            ? await excludedFiles.ListAllAsync(ct).ConfigureAwait(false)
            : await excludedFiles.ListByLibraryAsync(libraryId.Trim(), ct).ConfigureAwait(false);

        return rows.Select(ToDto).ToList();
    }

    /// <summary>Puts one file back. Null when no excluded file has this id.</summary>
    public async Task<ExcludedFileAddBackResultDto?> AddBackAsync(Guid id, CancellationToken ct = default)
    {
        var row = await excludedFiles.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        // The exclusion goes first so the scan that is about to be queued is not turned away by it.
        await excludedFiles.RemoveAsync(id, ct).ConfigureAwait(false);
        excludedFileFilter.Invalidate();

        var source = folders.ResolveById(row.LibraryId)?.Sources
            .FirstOrDefault(candidate => string.Equals(candidate.Id, row.SourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null || string.IsNullOrWhiteSpace(source.Path))
        {
            return Result(false, FolderGoneMessage);
        }

        var root = Path.GetFullPath(source.Path);
        var fullPath = Path.GetFullPath(Path.Combine(root, row.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (ExcludedSourceFilePaths.RelativePathOf(root, fullPath) is null)
        {
            // A stored place that points outside its folder is never read.
            logger.LogWarning("Not queueing {FileName}: its stored place is not inside its source folder", Path.GetFileName(row.RelativePath));
            return Result(false, MissingMessage);
        }

        if (!File.Exists(fullPath))
        {
            return Result(false, MissingMessage);
        }

        try
        {
            await intake.EnqueueAsync(fullPath, row.LibraryId, row.SourceId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            // The exclusion is already lifted, so the next scan takes the file in; the person is told the scan did not start now.
            logger.LogWarning(ex, "Added back {FileName} but could not queue its scan", Path.GetFileName(fullPath));
            return Result(false, NotQueuedMessage);
        }

        return Result(true, QueuedMessage);
    }

    private ExcludedFileDto ToDto(ExcludedSourceFile row)
    {
        var segments = row.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.Length == 0 ? row.RelativePath : segments[^1];
        var folderSegments = segments.Length > 1 ? segments[..^1] : [];
        var library = folders.ResolveById(row.LibraryId)?.Name;
        if (string.IsNullOrWhiteSpace(library))
        {
            library = "A removed library";
        }

        return new ExcludedFileDto
        {
            Id = row.Id,
            LibraryId = row.LibraryId,
            FileName = fileName,
            FolderLabel = folderSegments.Length == 0 ? library : library + LabelSeparator + string.Join(LabelSeparator, folderSegments),
            Title = row.Title,
            ExcludedAt = row.ExcludedAt,
            ExcludedBy = row.ExcludedByName,
            Reason = row.Reason,
        };
    }

    private static ExcludedFileAddBackResultDto Result(bool queued, string message) =>
        new() { Queued = queued, Message = message };
}
