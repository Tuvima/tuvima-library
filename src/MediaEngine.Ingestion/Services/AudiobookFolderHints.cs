using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Enums;
using MediaEngine.Processors.Models;

namespace MediaEngine.Ingestion.Services;

/// <summary>Recording identity scoped to an explicitly configured audiobook source.</summary>
public static class AudiobookFolderHints
{
    public static ProcessorResult Apply(ProcessorResult result, string sourceRoot)
    {
        if (result.IsCorrupt || Path.GetExtension(result.FilePath).ToLowerInvariant() is not
            (".mp3" or ".m4a" or ".m4b" or ".flac" or ".ogg" or ".wav" or ".aac" or ".wma"))
        {
            return result;
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(result.FilePath))!);
        while (directory.Parent is not null && Regex.IsMatch(directory.Name, @"^(disc|disk|cd)\s*[-_. ]*\d+$", RegexOptions.IgnoreCase))
        {
            directory = directory.Parent;
        }
        if (!directory.FullName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }
        var singleFileBook = IsSingleFileBookAmongSiblings(result.FilePath);
        var claims = result.Claims.ToList();
        string? Get(string key) => claims.FirstOrDefault(c => c.Key == key && !string.IsNullOrWhiteSpace(c.Value))?.Value;
        void Set(string key, string value, double confidence = 0.98)
        {
            claims.RemoveAll(c => c.Key == key);
            claims.Add(new() { Key = key, Value = value, Confidence = confidence });
        }
        var title = Get("book_title") ?? Get("album") ?? (singleFileBook ? Path.GetFileNameWithoutExtension(result.FilePath) : directory.Name);
        var trackTitle = Get("track_title") ?? Get("title") ?? Path.GetFileNameWithoutExtension(result.FilePath);
        var files = (singleFileBook ? [Path.GetFullPath(result.FilePath)] : Directory.EnumerateFiles(directory.FullName, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        }))
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".mp3" or ".m4a" or ".m4b" or ".flac" or ".ogg" or ".wav" or ".aac" or ".wma")
            .OrderBy(p => Regex.Replace(Path.GetRelativePath(directory.FullName, p), @"\d+", m => m.Value.PadLeft(16, '0')), StringComparer.OrdinalIgnoreCase).ToArray();
        var part = Array.FindIndex(files, p => string.Equals(p, Path.GetFullPath(result.FilePath), StringComparison.OrdinalIgnoreCase)) + 1;
        Set("track_title", trackTitle);
        Set("book_title", title);
        Set("title", title);
        // A whole-book file sharing its folder with other whole-book files (two m4b in one author folder) is its
        // own recording: the folder holds several books, so it cannot name one of them.
        var recordingScope = singleFileBook ? Path.GetFullPath(result.FilePath) : directory.FullName;
        Set("audiobook_recording_key", (singleFileBook ? "source-file:" : "source-folder:") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordingScope.ToUpperInvariant()))));
        Set("audiobook_part_number", Math.Max(1, part).ToString());
        Set("audiobook_part_count", files.Length.ToString());
        if (Get("author") is null && singleFileBook && !string.Equals(directory.FullName, root, StringComparison.OrdinalIgnoreCase))
        {
            Set("author", directory.Name, 0.65);
        }
        else if (Get("author") is null && directory.Parent is { } parent && !string.Equals(parent.FullName, root, StringComparison.OrdinalIgnoreCase))
        {
            Set("author", parent.Name, 0.65);
        }
        return BuildResult(result, claims);
    }

    private static ProcessorResult BuildResult(ProcessorResult result, List<ExtractedClaim> claims)
    {
        return new()
        {
            FilePath = result.FilePath,
            DetectedType = MediaType.Audiobooks,
            Claims = claims,
            CoverImage = result.CoverImage,
            CoverImageMimeType = result.CoverImageMimeType,
            MediaTypeCandidates = [new() { Type = MediaType.Audiobooks, Confidence = 0.99, Reason = "Configured audiobook source and recording folder" }]
        };
    }

    private static readonly Regex PartLikeName = new(
        @"(^|[\s._\-(\[])(part|pt|disc|disk|cd|chapter|ch|track)\s*[-_. ]*\d+|^\d+[\s._-]|[\s._]-\s*\d{1,3}\s*$|\b\d+\s*(of|von)\s*\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True for an <c>.m4b</c> or <c>.m4a</c> file (a whole book in one file) that shares its folder with other
    /// such files and is not named like a part of a larger book.
    /// </summary>
    internal static bool IsSingleFileBookAmongSiblings(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is not (".m4b" or ".m4a"))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(filePath);
        if (PartLikeName.IsMatch(name))
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            return Directory.EnumerateFiles(directory)
                .Count(p => Path.GetExtension(p).ToLowerInvariant() is ".m4b" or ".m4a"
                            && !PartLikeName.IsMatch(Path.GetFileNameWithoutExtension(p))) > 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Folder cannot be listed: fall back to the folder-as-one-book rule rather than guess.
            return false;
        }
    }
}
