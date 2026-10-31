using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Extras;

/// <summary>Turns a title's stored Extras into the cards on its page: documents, then picture folders, then clips.</summary>
public static class ExtraCardBuilder
{
    private const int MosaicSize = 4;

    public static IReadOnlyList<DetailExtraCardViewModel> Build(IReadOnlyList<ItemExtra> extras, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(extras);
        ArgumentNullException.ThrowIfNull(fileExists);

        var cards = new List<DetailExtraCardViewModel>();

        foreach (var document in extras.Where(extra => extra.Kind == ItemExtraKinds.Document))
        {
            cards.Add(new DetailExtraCardViewModel
            {
                Id = document.Id.ToString("D"),
                Kind = DetailExtraCardKinds.Document,
                Title = document.Title,
                Subtitle = DocumentSubtitle(document),
                IsMissing = !fileExists(document.FilePath),
            });
        }

        foreach (var folder in extras
                     .Where(extra => extra.Kind == ItemExtraKinds.Picture)
                     .GroupBy(extra => (extra.ItemFolder.ToLowerInvariant(), (extra.GroupKey ?? string.Empty).ToLowerInvariant()))
                     .OrderBy(group => group.Key.Item2, StringComparer.Ordinal))
        {
            var pictures = folder.OrderBy(extra => extra.SortOrder).ToList();
            var title = FolderTitle(pictures[0].GroupKey);
            cards.Add(new DetailExtraCardViewModel
            {
                Id = pictures[0].Id.ToString("D"),
                Kind = DetailExtraCardKinds.Pictures,
                Title = title,
                Subtitle = pictures.Count == 1 ? "1 picture" : $"{pictures.Count.ToString(CultureInfo.InvariantCulture)} pictures",
                ItemCount = pictures.Count,
                PreviewIds = [.. pictures.Take(MosaicSize).Select(picture => picture.Id.ToString("D"))],
                IsMissing = pictures.All(picture => !fileExists(picture.FilePath)),
            });
        }

        foreach (var clip in extras.Where(extra => extra.Kind == ItemExtraKinds.Clip))
        {
            cards.Add(new DetailExtraCardViewModel
            {
                Id = clip.Id.ToString("D"),
                Kind = DetailExtraCardKinds.Clip,
                Title = clip.Title,
                Subtitle = ClipSubtitle(clip),
                IsMissing = !fileExists(clip.FilePath),
            });
        }

        return cards;
    }

    public static string MimeTypeOf(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain; charset=utf-8",
            ".md" => "text/plain; charset=utf-8",
            ".epub" => "application/epub+zip",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            ".heic" => "image/heic",
            ".tif" or ".tiff" => "image/tiff",
            ".mp4" or ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".wmv" => "video/x-ms-wmv",
            ".ts" or ".m2ts" => "video/mp2t",
            ".mpg" or ".mpeg" => "video/mpeg",
            ".mp3" => "audio/mpeg",
            ".m4a" or ".m4b" => "audio/mp4",
            ".flac" => "audio/flac",
            ".ogg" or ".opus" => "audio/ogg",
            ".wav" => "audio/wav",
            ".aac" => "audio/aac",
            ".wma" => "audio/x-ms-wma",
            _ => "application/octet-stream",
        };

    private static string DocumentSubtitle(ItemExtra document)
    {
        var extension = Path.GetExtension(document.FilePath).ToLowerInvariant();
        return extension switch
        {
            ".pdf" when document.PageCount is > 0 =>
                document.PageCount == 1 ? "PDF, 1 page" : $"PDF, {document.PageCount.Value.ToString(CultureInfo.InvariantCulture)} pages",
            ".pdf" => "PDF",
            ".epub" => "EPUB",
            ".md" => "Notes",
            _ => "Text",
        };
    }

    private static string ClipSubtitle(ItemExtra clip)
    {
        if (clip.DurationSeconds is { } seconds and > 0)
        {
            var span = TimeSpan.FromSeconds(Math.Round(seconds));
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
        }

        return MimeTypeOf(clip.FilePath).StartsWith("audio/", StringComparison.Ordinal) ? "Audio" : "Video";
    }

    private static string FolderTitle(string? groupKey)
    {
        if (string.IsNullOrEmpty(groupKey))
        {
            return "Pictures";
        }

        var name = groupKey.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(name) ? "Pictures" : name.Replace('_', ' ').Trim();
    }
}
