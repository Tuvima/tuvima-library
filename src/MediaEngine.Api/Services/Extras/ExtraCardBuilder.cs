using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Extras;

namespace MediaEngine.Api.Services.Extras;

/// <summary>Turns a title's stored Extras into the cards on its page: documents, then picture folders, then clips.</summary>
public static class ExtraCardBuilder
{
    private const int MosaicSize = 4;

    /// <param name="alongsideAsset">
    /// Finds the media asset of the lesson a file is shown alongside (a folder of videos has no season or episode numbers to find it by);
    /// null when the caller cannot, or the file is shown alongside nothing.
    /// </param>
    /// <param name="groupByFamilyAbove">
    /// When the title has more than this many cards and not one file is in a group, every card falls into the group of its kind of file
    /// ("Documents", "Videos", "Pictures", from the role families in <c>config/extras.json</c>) until a person names groups (item 17); 0 turns it off.
    /// </param>
    public static IReadOnlyList<DetailExtraCardViewModel> Build(
        IReadOnlyList<ItemExtra> extras, Func<string, bool> fileExists, ExtrasConfiguration? rules = null, Func<ItemExtra, Guid?>? alongsideAsset = null,
        int groupByFamilyAbove = 0)
    {
        ArgumentNullException.ThrowIfNull(extras);
        ArgumentNullException.ThrowIfNull(fileExists);

        var cards = new List<DetailExtraCardViewModel>();
        var cardCount = extras.Count(extra => extra.Kind != ItemExtraKinds.Picture)
            + extras.Where(extra => extra.Kind == ItemExtraKinds.Picture).Select(extra => (extra.ItemFolder.ToLowerInvariant(), (extra.GroupKey ?? string.Empty).ToLowerInvariant())).Distinct().Count();
        var byFamily = groupByFamilyAbove > 0 && cardCount > groupByFamilyAbove && extras.All(extra => extra.GroupName is null);
        var config = rules ?? ExtrasConfigurationProvider.Active;
        string? GroupOf(ItemExtra extra) =>
            extra.GroupName ?? (byFamily ? config.FamilyFor(extra.Kind).Label : null);

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
                RoleLabel = RoleLabelOf(document, rules),
                Group = GroupOf(document),
                ScopeKey = document.EffectiveScope.Key,
                ScopeSource = document.ScopeSource,
                AlongsideKey = document.EffectiveAlongside?.Key,
                FileBadge = SupportingFileListService.TypeWords(document, isFolder: false, config).Badge,
                SizeBytes = document.SizeBytes,
                PageCount = document.PageCount,
                AlongsideLabel = AlongsideLabelOf(document),
                AlongsideAssetId = alongsideAsset?.Invoke(document)?.ToString("D"),
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
                RoleLabel = RoleLabelOf(pictures[0], rules),
                Group = GroupOf(pictures[0]),
                ScopeKey = pictures[0].EffectiveScope.Key,
                ScopeSource = pictures[0].ScopeSource,
                AlongsideKey = pictures[0].EffectiveAlongside?.Key,
                FileBadge = SupportingFileListService.TypeWords(pictures[0], isFolder: pictures.Count > 1, config).Badge,
                SizeBytes = pictures.Sum(picture => picture.SizeBytes),
                AlongsideLabel = AlongsideLabelOf(pictures[0]),
                AlongsideAssetId = alongsideAsset?.Invoke(pictures[0])?.ToString("D"),
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
                RoleLabel = RoleLabelOf(clip, rules),
                Group = GroupOf(clip),
                ScopeKey = clip.EffectiveScope.Key,
                ScopeSource = clip.ScopeSource,
                AlongsideKey = clip.EffectiveAlongside?.Key,
                FileBadge = SupportingFileListService.TypeWords(clip, isFolder: false, config).Badge,
                SizeBytes = clip.SizeBytes,
                AlongsideLabel = AlongsideLabelOf(clip),
                AlongsideAssetId = alongsideAsset?.Invoke(clip)?.ToString("D"),
                Tags = clip.TagList,
            });
        }

        return cards;
    }

    private static string? AlongsideLabelOf(ItemExtra extra) =>
        extra.EffectiveAlongside is { Level: not ExtraScopeLevel.None } alongside ? SupportingFileScopes.LabelOf(alongside) : null;

    /// <summary>One supporting file as the editor and the browser read it: what a person set and what the folder says, side by side.</summary>
    public static SupportingFileDto ToSupportingFile(
        ItemExtra extra, Func<string, bool> fileExists, ExtrasConfiguration? rules = null, int itemCount = 1, long? sizeBytes = null)
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
            RoleLabel = RoleLabelOf(extra, rules),
            RoleIsSetByYou = extra.RoleIsSetByYou,
            ScopeKey = extra.EffectiveScope.Key,
            InferredScopeKey = extra.InferredScope.Key,
            ScopeSource = extra.ScopeSource,
            AlongsideKey = extra.EffectiveAlongside?.Key,
            InferredAlongsideKey = extra.InferredAlongside?.Key,
            Tags = extra.TagList,
            SizeBytes = sizeBytes ?? extra.SizeBytes,
            PageCount = extra.PageCount,
            DurationSeconds = extra.DurationSeconds,
            ItemCount = itemCount,
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

    /// <summary>The words for the file's role: the configured label for its kind of file, else the built-in words.</summary>
    private static string RoleLabelOf(ItemExtra extra, ExtrasConfiguration? rules) =>
        rules?.LabelFor(extra.Kind, extra.EffectiveRole) ?? ExtraRoles.Label(extra.EffectiveRole);

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
