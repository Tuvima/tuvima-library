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
            (".mp3" or ".m4a" or ".m4b" or ".flac" or ".ogg" or ".wav" or ".aac" or ".wma")) return result;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(result.FilePath))!);
        while (directory.Parent is not null && Regex.IsMatch(directory.Name, @"^(disc|disk|cd)\s*[-_. ]*\d+$", RegexOptions.IgnoreCase))
            directory = directory.Parent;
        if (!directory.FullName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return result;
        var claims = result.Claims.ToList();
        string? Get(string key) => claims.FirstOrDefault(c => c.Key == key && !string.IsNullOrWhiteSpace(c.Value))?.Value;
        void Set(string key, string value, double confidence = 0.98)
        {
            claims.RemoveAll(c => c.Key == key);
            claims.Add(new() { Key = key, Value = value, Confidence = confidence });
        }
        var title = Get("book_title") ?? Get("album") ?? directory.Name;
        var trackTitle = Get("track_title") ?? Get("title") ?? Path.GetFileNameWithoutExtension(result.FilePath);
        var files = Directory.EnumerateFiles(directory.FullName, "*", new EnumerationOptions {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".mp3" or ".m4a" or ".m4b" or ".flac" or ".ogg" or ".wav" or ".aac" or ".wma")
            .OrderBy(p => Regex.Replace(Path.GetRelativePath(directory.FullName, p), @"\d+", m => m.Value.PadLeft(16, '0')), StringComparer.OrdinalIgnoreCase).ToArray();
        var part = Array.FindIndex(files, p => string.Equals(p, Path.GetFullPath(result.FilePath), StringComparison.OrdinalIgnoreCase)) + 1;
        Set("track_title", trackTitle);
        Set("book_title", title);
        Set("title", title);
        Set("audiobook_recording_key", "source-folder:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.FullName.ToUpperInvariant()))));
        Set("audiobook_part_number", Math.Max(1, part).ToString());
        Set("audiobook_part_count", files.Length.ToString());
        if (Get("author") is null && directory.Parent is { } parent && !string.Equals(parent.FullName, root, StringComparison.OrdinalIgnoreCase))
            Set("author", parent.Name, 0.65);
        return new() { FilePath = result.FilePath, DetectedType = MediaType.Audiobooks, Claims = claims,
            CoverImage = result.CoverImage, CoverImageMimeType = result.CoverImageMimeType,
            MediaTypeCandidates = [new() { Type = MediaType.Audiobooks, Confidence = 0.99, Reason = "Configured audiobook source and recording folder" }] };
    }
}
