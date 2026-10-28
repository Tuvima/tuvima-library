using System.Security.Cryptography;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Services.ImportUploads;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>How an Add files request ended: a result, or a refusal with the reason code and plain sentence (nothing was copied).</summary>
public sealed record LibraryAddFilesOutcome(
    LibraryAddFilesResultDto? Result,
    string? RefusalCode,
    string? RefusalMessage,
    int RefusalStatus,
    string? DestinationPath)
{
    public static LibraryAddFilesOutcome Refused(int status, string code, string message) => new(null, code, message, status, null);
}

/// <summary>
/// Add files (Libraries spec D8, D26): copies server files into one library's import destination. The originals are only ever read, never
/// moved or changed. Every path must sit inside an administrator-approved storage location (links that leave it are refused, and one
/// refused path refuses the whole request before anything is copied). The free-space check runs before the first byte moves and refuses
/// with "Not enough space: N needed, M free". A file that is already in the library (same contents) is skipped when the resolved duplicate
/// rule (folder, then library, then server default) is Keep one copy; with Keep both it is copied under a numbered name.
/// </summary>
public sealed class LibraryAddFilesService(
    IConfigurationLoader configuration,
    ServerFolderBrowserService folders,
    IMediaTypeExtensionCatalog extensionCatalog,
    ILogger<LibraryAddFilesService> logger,
    ILibraryKindSource? kindSource = null,
    Func<string, long?>? freeBytesAt = null)
{
    private const int CopyBufferBytes = 1024 * 1024;

    private readonly Func<string, long?> _freeBytesAt = freeBytesAt ?? FreeSpaceCheck.FreeBytesAt;

    public async Task<LibraryAddFilesOutcome> AddAsync(
        string libraryId, IReadOnlyList<string> paths, IReadOnlyCollection<string> hiddenRoots, CancellationToken ct)
    {
        if (paths.Count == 0)
        {
            return LibraryAddFilesOutcome.Refused(StatusCodes.Status400BadRequest, LibraryAddFilesRefusalCodes.NothingSelected, "Choose at least one file to add.");
        }

        var snapshot = configuration.LoadLibraries();
        var library = snapshot.Libraries.FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        if (library is null)
        {
            return LibraryAddFilesOutcome.Refused(StatusCodes.Status404NotFound, "library_not_found", $"Library '{libraryId}' was not found.");
        }

        var destination = library.PrimaryDestination;
        if (destination is null || string.IsNullOrWhiteSpace(destination.Path))
        {
            return LibraryAddFilesOutcome.Refused(
                StatusCodes.Status409Conflict, LibraryAddFilesRefusalCodes.NoImportDestination, "Choose an import folder first. Add files copies into it.");
        }

        if (!Directory.Exists(destination.Path))
        {
            return LibraryAddFilesOutcome.Refused(
                StatusCodes.Status409Conflict, LibraryAddFilesRefusalCodes.DestinationUnavailable, "The import folder is offline, so nothing was copied.");
        }

        // One refused path refuses the request: nothing is copied from a selection that includes a file Library may not read.
        var resolved = new List<string>(paths.Count);
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!folders.TryResolveApprovedFile(path, hiddenRoots, out var physical, out var refusal))
            {
                return LibraryAddFilesOutcome.Refused(StatusCodes.Status400BadRequest, LibraryAddFilesRefusalCodes.PathNotApproved, refusal);
            }

            resolved.Add(physical);
        }

        var extensions = ExtensionsFor(library.KindKey);
        var unsupported = 0;
        var unreadable = 0;
        var candidates = new List<(string Path, long Size)>();
        foreach (var path in resolved)
        {
            var name = Path.GetFileName(path);
            if (!ImportUploadFileNames.TryValidate(name, out _))
            {
                unreadable++;
            }
            else if (!extensions.Contains(Path.GetExtension(path)))
            {
                unsupported++;
            }
            else if (SizeOf(path) is { } size)
            {
                candidates.Add((path, size));
            }
            else
            {
                unreadable++;
            }
        }

        var space = FreeSpaceCheck.Check(candidates.Select(candidate => candidate.Size), _freeBytesAt(destination.Path));
        if (!space.Fits)
        {
            return LibraryAddFilesOutcome.Refused(StatusCodes.Status507InsufficientStorage, LibraryAddFilesRefusalCodes.NotEnoughSpace, space.Message ?? "Not enough space.");
        }

        var policy = library.ResolveDuplicatePolicy(destination, snapshot.FileHandling.DuplicatePolicy);
        var keepOne = LibraryDuplicatePolicies.DeletesIncomingDuplicate(policy);
        var known = keepOne ? IndexExistingFiles(library, candidates.Select(candidate => candidate.Size).ToHashSet(), ct) : null;
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        var copied = 0;
        var skipped = 0;
        long bytes = 0;
        foreach (var (path, size) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (known is not null && await IsDuplicateAsync(path, size, known, hashes, ct))
                {
                    skipped++;
                    continue;
                }

                var target = await CopyAsync(path, destination.Path, ct);
                copied++;
                bytes += size;
                if (known is not null && known.TryGetValue(size, out var sameSize))
                {
                    sameSize.Add(target);
                }
                else if (known is not null)
                {
                    known[size] = [target];
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Add files could not copy {FileName} into library {LibraryId}", Path.GetFileName(path), libraryId);
                unreadable++;
            }
        }

        return new LibraryAddFilesOutcome(
            new LibraryAddFilesResultDto { Copied = copied, SkippedDuplicates = skipped, Unreadable = unreadable, Unsupported = unsupported, BytesCopied = bytes },
            null, null, StatusCodes.Status200OK, destination.Path);
    }

    private IReadOnlySet<string> ExtensionsFor(string kindKey)
    {
        var kind = (kindSource?.GetKinds() ?? configuration.LoadLibraryKinds().Kinds)
            .FirstOrDefault(candidate => string.Equals(candidate.Key, kindKey, StringComparison.OrdinalIgnoreCase));
        return kind is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : SupportedFileCounter.ExtensionsFor(extensionCatalog, kind.Format);
    }

    private static long? SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file whose size cannot be read cannot be copied either; it is counted as one that could not be read.
            _ = exception;
            return null;
        }
    }

    // Files already in the library whose size matches one of the candidates, by size. Only those can be exact copies, so only those are hashed.
    private static Dictionary<long, List<string>> IndexExistingFiles(LibraryFolderConfig library, IReadOnlySet<long> sizes, CancellationToken ct)
    {
        var index = new Dictionary<long, List<string>>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        foreach (var source in library.CataloguedSources.Where(source => Directory.Exists(source.Path)))
        {
            foreach (var file in new DirectoryInfo(source.Path).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                long length;
                try
                {
                    length = file.Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A file that vanished or cannot be read mid-walk cannot be a copy we can compare against.
                    _ = exception;
                    continue;
                }

                if (!sizes.Contains(length))
                {
                    continue;
                }

                if (!index.TryGetValue(length, out var list))
                {
                    index[length] = list = [];
                }

                list.Add(file.FullName);
            }
        }

        return index;
    }

    private static async Task<bool> IsDuplicateAsync(
        string path, long size, Dictionary<long, List<string>> known, Dictionary<string, string> hashes, CancellationToken ct)
    {
        if (!known.TryGetValue(size, out var same) || same.Count == 0)
        {
            return false;
        }

        var incoming = await HashOfAsync(path, hashes, ct);
        foreach (var existing in same)
        {
            if (string.Equals(incoming, await HashOfAsync(existing, hashes, ct), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string> HashOfAsync(string path, Dictionary<string, string> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(path, out var known))
        {
            return known;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CopyBufferBytes, useAsync: true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        cache[path] = hash;
        return hash;
    }

    // Copies through a hidden working file in the destination, then gives it its final (unique) name, so a half-copied file never looks like media.
    private static async Task<string> CopyAsync(string source, string destinationFolder, CancellationToken ct)
    {
        var working = Path.Combine(destinationFolder, $".tuvima-add-{Guid.NewGuid():N}.part");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, useAsync: true))
            await using (var output = new FileStream(working, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true))
            {
                await input.CopyToAsync(output, CopyBufferBytes, ct);
                await output.FlushAsync(ct);
            }

            var name = Path.GetFileName(source);
            for (var attempt = 0; ; attempt++)
            {
                var target = Path.Combine(destinationFolder, ImportUploadFileNames.WithNumber(name, attempt));
                if (File.Exists(target))
                {
                    continue;
                }

                try
                {
                    File.Move(working, target, overwrite: false);
                    return target;
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Another file took the name between the check and the move; the next number is tried.
                }
            }
        }
        catch
        {
            if (File.Exists(working))
            {
                File.Delete(working);
            }

            throw;
        }
    }
}
