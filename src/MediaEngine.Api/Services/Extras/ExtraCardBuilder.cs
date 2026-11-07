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
                Title = document.DisplayTitle,
                Subtitle = DocumentSubtitle(document),
                ExtraKind = KindOf(document),
                Place = PlaceOf(document),
                Badge = BadgeOf(document),
                IsMissing = !fileExists(document.FilePath),
                Description = document.Description,
                Role = document.EffectiveRole,
                RoleLabel = ExtraRoles.Label(document.EffectiveRole),
                Group = document.GroupName,
                ScopeKey = document.EffectiveScope.Key,
                ScopeSource = document.ScopeSource,
                AlongsideKey = document.EffectiveAlongside?.Key,
                Tags = document.TagList,
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
                ExtraKind = KindOf(pictures[0]),
                Place = PlaceOf(pictures[0]),
                Badge = BadgeOf(pictures[0]),
                ItemCount = pictures.Count,
                PreviewIds = [.. pictures.Take(MosaicSize).Select(picture => picture.Id.ToString("D"))],
                IsMissing = pictures.All(picture => !fileExists(picture.FilePath)),
                Description = pictures[0].Description,
                Role = pictures[0].EffectiveRole,
                RoleLabel = ExtraRoles.Label(pictures[0].EffectiveRole),
                Group = pictures[0].GroupName,
                ScopeKey = pictures[0].EffectiveScope.Key,
                ScopeSource = pictures[0].ScopeSource,
                AlongsideKey = pictures[0].EffectiveAlongside?.Key,
                Tags = pictures[0].TagList,
            });
        }

        foreach (var clip in extras.Where(extra => extra.Kind == ItemExtraKinds.Clip))
        {
            cards.Add(new DetailExtraCardViewModel
            {
                Id = clip.Id.ToString("D"),
                Kind = DetailExtraCardKinds.Clip,
                Title = clip.DisplayTitle,
                Subtitle = ClipSubtitle(clip),
                ExtraKind = KindOf(clip),
                Place = PlaceOf(clip),
                Badge = BadgeOf(clip),
                IsMissing = !fileExists(clip.FilePath),
                Description = clip.Description,
                Role = clip.EffectiveRole,
                RoleLabel = ExtraRoles.Label(clip.EffectiveRole),
                Group = clip.GroupName,
                ScopeKey = clip.EffectiveScope.Key,
                ScopeSource = clip.ScopeSource,
                AlongsideKey = clip.EffectiveAlongside?.Key,
                Tags = clip.TagList,
            });
        }

        return cards;
    }

    /// <summary>One supporting file as the editor and the browser read it: what a person set and what the folder says, side by side.</summary>
    public static SupportingFileDto ToSupportingFile(ItemExtra extra, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(extra);
        ArgumentNullException.ThrowIfNull(fileExists);
        return new SupportingFileDto
        {
            Id = extra.Id.ToString("D"),
            Kind = extra.Kind switch
            {
                ItemExtraKinds.Picture => DetailExtraCardKinds.Pictures,
                ItemExtraKinds.Clip => DetailExtraCardKinds.Clip,
                _ => DetailExtraCardKinds.Document,
            },
            FileName = Path.GetFileName(extra.FilePath),
            Title = extra.DisplayTitle,
            OwnTitle = string.IsNullOrWhiteSpace(extra.OwnTitle) ? null : extra.OwnTitle,
            Description = extra.Description,
            GroupId = extra.GroupId?.ToString("D"),
            Group = extra.GroupName,
            GroupSource = extra.GroupName is null ? null : extra.GroupSource,
            Role = extra.EffectiveRole,
            RoleLabel = ExtraRoles.Label(extra.EffectiveRole),
            RoleIsSetByYou = extra.RoleIsSetByYou,
            ScopeKey = extra.EffectiveScope.Key,
            InferredScopeKey = extra.InferredScope.Key,
            ScopeSource = extra.ScopeSource,
            AlongsideKey = extra.EffectiveAlongside?.Key,
            InferredAlongsideKey = extra.InferredAlongside?.Key,
            Tags = extra.TagList,
            SizeBytes = extra.SizeBytes,
            PageCount = extra.PageCount,
            DurationSeconds = extra.DurationSeconds,
            IsMissing = extra.IsMissingFromDisk || !fileExists(extra.FilePath),
        };
    }

    /// <summary>What the Extra is: the kind a person gave it, else the one its role amounts to (a rule-found Extra has no kind stored).</summary>
    private static string KindOf(ItemExtra extra) =>
        ExtraKinds.IsKnown(extra.ExtraKind) ? extra.ExtraKind! : ExtraRoles.ToExtraKind(extra.EffectiveRole, extra.Kind);

    /// <summary>Where in its title the Extra belongs ("Season 1", "Section 1"); null for the whole title.</summary>
    private static string? PlaceOf(ItemExtra extra)
    {
        if (ExtraPlaces.Label(extra.PlaceSeason, extra.PlaceEpisode, extra.PlaceTrack) is { Length: > 0 } place)
        {
            return place;
        }

        return extra.EffectiveScope.Label() is { Length: > 0 } scope ? scope : null;
    }

    private static string BadgeOf(ItemExtra extra) =>
        PlaceOf(extra) is { } place ? $"{ExtraKinds.Label(KindOf(extra))} · {place}" : ExtraKinds.Label(KindOf(extra));

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
