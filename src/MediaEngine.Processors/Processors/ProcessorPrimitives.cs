using MediaEngine.Domain.Enums;
using MediaEngine.Processors.Models;

namespace MediaEngine.Processors.Processors;

internal static class ProcessorHeaderReader
{
    public static bool TryRead(
        string filePath,
        Span<byte> destination,
        out int bytesRead,
        FileShare fileShare = FileShare.Read,
        long offset = 0)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                fileShare,
                bufferSize: destination.Length,
                FileOptions.None);

            if (offset > 0)
            {
                if (stream.Length < offset + destination.Length)
                {
                    bytesRead = 0;
                    return false;
                }
                stream.Position = offset;
            }
            bytesRead = stream.Read(destination);
            return true;
        }
        catch (IOException)
        {
            bytesRead = 0;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            bytesRead = 0;
            return false;
        }
    }
}

/// <summary>
/// Turns a file-name stem into a readable title. A stem that already contains spaces keeps its
/// full stops ("Dr. Horrible's Sing-Along Blog (2008)", "Mr. Smith Goes to Washington", "Vol. 2");
/// only scene-style stems without spaces ("The.Matrix.1999.1080p") use full stops as word
/// separators. Underscores are always separators and repeated whitespace is collapsed.
/// </summary>
public static class FileStemTitleCleaner
{
    public static string Clean(string stem)
    {
        if (string.IsNullOrEmpty(stem))
        {
            return string.Empty;
        }

        var cleaned = stem.Contains(' ') ? stem : stem.Replace('.', ' ');
        cleaned = cleaned.Replace('_', ' ');
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
    }
}

internal static class ProcessorClaimFactory
{
    public static ExtractedClaim Create(
        string key,
        string value,
        double confidence,
        bool trimValue = false) => new()
        {
            Key = key,
            Value = trimValue ? value.Trim() : value,
            Confidence = confidence,
        };
}

internal static class ProcessorResultFactory
{
    public static ProcessorResult Corrupt(
        string filePath,
        MediaType detectedType,
        string reason) => new()
        {
            FilePath = filePath,
            DetectedType = detectedType,
            IsCorrupt = true,
            CorruptReason = reason,
        };
}
