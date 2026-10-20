using System.Text;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Organise;

/// <summary>What happened to one sidecar when its media file moved.</summary>
public sealed record SidecarMoveResult(string From, string To, string Outcome, string? Detail = null);

/// <summary>
/// Moves the subtitles, notes, artwork, cue sheets, lyrics and playlists that share a media file's base name to sit beside
/// its new name. Nothing is ever overwritten: a sidecar whose new name is already taken stays where it is and is reported.
/// </summary>
public sealed class SidecarMover
{
    private readonly IOrganiseFileMover _mover;
    private readonly IMediaTypeExtensionCatalog? _extensionCatalog;
    private readonly ILogger<SidecarMover> _logger;

    public SidecarMover(
        IOrganiseFileMover mover,
        ILogger<SidecarMover> logger,
        IMediaTypeExtensionCatalog? extensionCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(mover);
        ArgumentNullException.ThrowIfNull(logger);
        _mover = mover;
        _logger = logger;
        _extensionCatalog = extensionCatalog;
    }

    private IReadOnlySet<string> MediaExtensions => _extensionCatalog?.GetAllMediaExtensions() ?? FolderMediaEnumerator.DefaultMediaExtensions;

    /// <summary>
    /// Moves every sidecar of <paramref name="oldMediaPath"/> so it sits beside <paramref name="newMediaPath"/>.
    /// </summary>
    /// <param name="mayMove">Asks the folder's policy whether this file may move to that place.</param>
    /// <param name="mayDelete">
    /// Asks the folder's policy whether a file may be deleted. When it allows it, a sidecar that is byte-for-byte the same
    /// as the file already at its new name is removed (used when an exact duplicate of the media file is being dropped).
    /// Pass <see langword="null"/> to never remove anything.
    /// </param>
    /// <param name="includeArtwork">Also moves poster, fanart and logo pictures (named after the media file, or folder-level when nothing else is left in the old folder).</param>
    /// <param name="renameArtworkToFolder">
    /// Only with <paramref name="includeArtwork"/>: poster, fanart and logo pictures take the name the new folder expects, the
    /// folder-level name (<c>poster.jpg</c>) when the media file is alone there, <c>{new name}-poster.jpg</c> when the folder is
    /// shared. A picture whose resulting name is taken stays where it is.
    /// </param>
    public IReadOnlyList<SidecarMoveResult> Move(
        string oldMediaPath,
        string newMediaPath,
        Func<string, string, bool> mayMove,
        Func<string, bool>? mayDelete = null,
        bool includeArtwork = false,
        bool renameArtworkToFolder = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldMediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newMediaPath);
        ArgumentNullException.ThrowIfNull(mayMove);

        var results = new List<SidecarMoveResult>();
        RestoreInterruptedRenames(Path.GetDirectoryName(oldMediaPath)!, mayMove, results);
        var candidates = SidecarFiles.Find(oldMediaPath, MediaExtensions).ToList();
        if (includeArtwork)
        {
            // Folder-wide files (cover.jpg, metadata.json) travel only once the last media file has left the old folder.
            var oldFolder = Path.GetDirectoryName(oldMediaPath)!;
            candidates.AddRange(SidecarFiles.FindArtwork(oldMediaPath, MediaExtensions, includeFolderLevel: !HasMediaLeft(oldFolder)));
        }

        MediaFileArtScope? artScope = includeArtwork && renameArtworkToFolder
            ? AssetPathService.GetMediaFileArtScope(newMediaPath)
            : null;

        foreach (var sidecar in candidates)
        {
            var target = SidecarFiles.DestinationFor(sidecar, newMediaPath, artScope);
            if (string.Equals(sidecar.Path, target, StringComparison.Ordinal))
            {
                continue;
            }

            if (!mayMove(sidecar.Path, target))
            {
                results.Add(new SidecarMoveResult(sidecar.Path, sidecar.Path, FolderOrganiseOutcomes.NotMoved, "This folder's settings do not allow moving it."));
                continue;
            }

            // A name that differs only in letter case is this same file, so it is renamed, never treated as a copy to remove.
            var targetExists = File.Exists(target);
            var identity = targetExists ? _mover.CompareFiles(sidecar.Path, target) : FileIdentityResult.Different;
            var caseOnly = CaseSafeRename.IsCaseOnlyChange(sidecar.Path, target);
            if (targetExists && caseOnly && identity == FileIdentityResult.Unproven)
            {
                // Nothing proves it is one file, so it is left as it is, exactly like the media file.
                _logger.LogWarning(
                    "Left {Sidecar} alone: it differs from {Target} only in letter case and Library could not prove they are the same file",
                    sidecar.Path, target);
                results.Add(new SidecarMoveResult(sidecar.Path, sidecar.Path, FolderOrganiseOutcomes.Skipped, CaseSafeRename.UnprovenReason));
                continue;
            }

            var sameFile = identity != FileIdentityResult.Different;
            if (targetExists && !(sameFile && caseOnly))
            {
                results.Add(sameFile
                    ? new SidecarMoveResult(sidecar.Path, sidecar.Path, FolderOrganiseOutcomes.NotMoved, "That name already leads to this same file.")
                    : MeetExisting(sidecar, target, mayDelete));
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CaseSafeRename.Move(_mover, sidecar.Path, target, _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not move the sidecar {Sidecar} to {Target}", sidecar.Path, target);
                results.Add(new SidecarMoveResult(sidecar.Path, sidecar.Path, FolderOrganiseOutcomes.Failed, ex.Message));
                continue;
            }

            string? detail = null;
            if (sidecar.IsPlaylist)
            {
                detail = RewritePlaylistEntries(target, Path.GetFileName(oldMediaPath), Path.GetFileName(newMediaPath));
            }

            results.Add(new SidecarMoveResult(sidecar.Path, target, FolderOrganiseOutcomes.SidecarMoved, detail));
        }

        return results;
    }

    /// <summary>
    /// A letter-case rename that stopped between its two steps leaves the sidecar under a hidden temporary name, so it has no
    /// visible name at all and would never be found again. Before looking for sidecars, each such leftover in the folder is put
    /// back under its own name (rolled back); the move that follows then renames it normally. A media file's own leftover is
    /// not touched here: the organising job's ledger finishes that one. A name that is already taken is never overwritten, and
    /// the folder's policy decides whether the restore may happen at all. Every restore, refusal or failure is added to
    /// <paramref name="results"/>, so History and the job log show it.
    /// </summary>
    private void RestoreInterruptedRenames(string folder, Func<string, string, bool> mayMove, List<SidecarMoveResult> results)
    {
        string[] leftovers;
        try
        {
            leftovers = Directory.Exists(folder)
                ? Directory.GetFiles(folder, $".*{CaseSafeRename.TemporarySuffix}")
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed has nothing to restore; the move itself reports what it cannot do.
            _ = ex;
            return;
        }

        foreach (var leftover in leftovers)
        {
            var temporaryName = Path.GetFileName(leftover);
            var original = temporaryName[1..^CaseSafeRename.TemporarySuffix.Length];
            if (original.Length == 0 || MediaExtensions.Contains(Path.GetExtension(original)))
            {
                continue;
            }

            var originalPath = Path.Combine(folder, original);
            if (File.Exists(originalPath))
            {
                _logger.LogWarning("Left {Leftover} alone: {Original} already exists, so it was not restored", leftover, originalPath);
                continue;
            }

            if (!mayMove(leftover, originalPath))
            {
                results.Add(new SidecarMoveResult(leftover, leftover, FolderOrganiseOutcomes.NotMoved, "This folder's settings do not allow putting it back."));
                continue;
            }

            try
            {
                _mover.Move(leftover, originalPath);
                _logger.LogInformation("Restored {Original} from an interrupted letter-case rename", originalPath);
                results.Add(new SidecarMoveResult(leftover, originalPath, FolderOrganiseOutcomes.SidecarMoved, "Put back under its own name after an interrupted letter-case rename."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not restore {Original} from {Leftover} yet", originalPath, leftover);
                results.Add(new SidecarMoveResult(leftover, leftover, FolderOrganiseOutcomes.Failed, ex.Message));
            }
        }
    }

    /// <summary>
    /// Copies every sidecar of <paramref name="oldMediaPath"/> so it sits beside <paramref name="newMediaPath"/>, for a merge.
    /// Each copy is checked byte for byte before it is put in place. When <paramref name="move"/> is true the original is
    /// removed afterwards, but only when <paramref name="mayRemove"/> allows it. Nothing is ever overwritten, and an identical
    /// copy that is already there is simply left (nothing is reported for it).
    /// </summary>
    /// <param name="mayWrite">Asks the target folder's policy whether a file may be created at that path.</param>
    public IReadOnlyList<SidecarMoveResult> Transfer(
        string oldMediaPath,
        string newMediaPath,
        bool move,
        Func<string, bool> mayWrite,
        Func<string, bool>? mayRemove = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldMediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newMediaPath);
        ArgumentNullException.ThrowIfNull(mayWrite);

        var results = new List<SidecarMoveResult>();
        var candidates = SidecarFiles.Find(oldMediaPath, MediaExtensions).ToList();
        // A copy can share a folder's cover with every file in it. A move takes it only with the last media file to leave.
        var oldFolder = Path.GetDirectoryName(oldMediaPath)!;
        candidates.AddRange(SidecarFiles.FindArtwork(oldMediaPath, MediaExtensions, includeFolderLevel: !move || !HasMediaLeft(oldFolder)));

        foreach (var sidecar in candidates)
        {
            var target = SidecarFiles.DestinationFor(sidecar, newMediaPath);
            if (!mayWrite(target))
            {
                results.Add(new SidecarMoveResult(sidecar.Path, sidecar.Path, FolderOrganiseOutcomes.NotMoved, "The target folder's settings do not allow writing it."));
                continue;
            }

            if (File.Exists(target))
            {
                if (_mover.IsSameFile(sidecar.Path, target))
                {
                    // One file under two names is not a copy; leave it.
                    continue;
                }

                if (SameBytes(sidecar.Path, target))
                {
                    // Already there (a shared cover picked up by an earlier file, or an earlier run). A move can now drop its original.
                    if (move && mayRemove is not null && mayRemove(sidecar.Path) && TryDelete(sidecar.Path))
                    {
                        results.Add(new SidecarMoveResult(sidecar.Path, target, FolderOrganiseOutcomes.DuplicateRemoved, "An identical copy was already there."));
                    }

                    continue;
                }

                results.Add(new SidecarMoveResult(sidecar.Path, sidecar.Path, FolderOrganiseOutcomes.NotMoved, "A different file already has that name, so this one was not copied."));
                continue;
            }

            var copied = CopyVerified(sidecar.Path, target);
            if (copied is not null)
            {
                results.Add(new SidecarMoveResult(sidecar.Path, sidecar.Path, FolderOrganiseOutcomes.Failed, copied));
                continue;
            }

            string? detail = null;
            if (sidecar.IsPlaylist)
            {
                detail = RewritePlaylistEntries(target, Path.GetFileName(oldMediaPath), Path.GetFileName(newMediaPath));
            }

            if (move)
            {
                if (mayRemove is not null && mayRemove(sidecar.Path) && TryDelete(sidecar.Path))
                {
                    results.Add(new SidecarMoveResult(sidecar.Path, target, FolderOrganiseOutcomes.SidecarMoved, detail));
                    continue;
                }

                detail = "Copied, but the original could not be removed.";
            }

            results.Add(new SidecarMoveResult(sidecar.Path, target, FolderOrganiseOutcomes.SidecarCopied, detail));
        }

        return results;
    }

    /// <summary>Copies through a temporary file and renames it into place once its bytes match. Returns a reason on failure, null on success.</summary>
    private string? CopyVerified(string source, string target)
    {
        var temporary = Path.Combine(
            Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}{MergeFileTransfer.PartSuffix}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, temporary, overwrite: false);
            if (!SameBytes(source, temporary))
            {
                return "The copy did not match the original, so it was discarded.";
            }

            File.Move(temporary, target, overwrite: false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not copy the sidecar {Sidecar} to {Target}", source, target);
            return ex.Message;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove {Path}", path);
            return false;
        }
    }

    private bool HasMediaLeft(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Any(file => MediaExtensions.Contains(Path.GetExtension(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If the folder cannot be read, assume files are still there so nothing shared is moved.
            _ = ex;
            return true;
        }
    }

    private SidecarMoveResult MeetExisting(SidecarFiles.Sidecar sidecar, string target, Func<string, bool>? mayDelete)
    {
        if (mayDelete is not null && mayDelete(sidecar.Path) && SameBytes(sidecar.Path, target))
        {
            try
            {
                File.Delete(sidecar.Path);
                return new SidecarMoveResult(sidecar.Path, target, FolderOrganiseOutcomes.DuplicateRemoved, "An identical copy was already there.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove the duplicate sidecar {Sidecar}", sidecar.Path);
                return new SidecarMoveResult(sidecar.Path, sidecar.Path, FolderOrganiseOutcomes.Failed, ex.Message);
            }
        }

        return new SidecarMoveResult(sidecar.Path, sidecar.Path, FolderOrganiseOutcomes.NotMoved, "A different file already has that name, so this one stays where it is.");
    }

    private static bool SameBytes(string left, string right)
    {
        try
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bufferA = new byte[81920];
            var bufferB = new byte[81920];
            int read;
            while ((read = a.Read(bufferA, 0, bufferA.Length)) > 0)
            {
                var offset = 0;
                while (offset < read)
                {
                    var got = b.Read(bufferB, offset, read - offset);
                    if (got == 0)
                    {
                        return false;
                    }

                    offset += got;
                }

                if (!bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If it cannot be compared it is not treated as identical, so nothing is removed.
            _ = ex;
            return false;
        }
    }

    /// <summary>Points the playlist lines that named the old file at the new one. Returns a note, or null when nothing changed.</summary>
    private string? RewritePlaylistEntries(string playlistPath, string oldMediaFileName, string newMediaFileName)
    {
        try
        {
            // Only a playlist that is valid UTF-8 is rewritten; anything else is left exactly as it was.
            string text;
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(File.ReadAllBytes(playlistPath));
            }
            catch (DecoderFallbackException)
            {
                return "Playlist moved; its entries use a text format Library does not rewrite.";
            }

            var rewritten = SidecarFiles.RewritePlaylist(text, oldMediaFileName, newMediaFileName);
            if (rewritten is null)
            {
                return null;
            }

            var temporary = playlistPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, rewritten, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporary, playlistPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return "Playlist entries now name the new file.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not update the playlist {Playlist}", playlistPath);
            return "Playlist moved, but its entries could not be updated.";
        }
    }
}
