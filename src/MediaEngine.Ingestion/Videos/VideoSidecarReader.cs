using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace MediaEngine.Ingestion.Videos;

/// <summary>What a sidecar file next to a video says about it. Every field is optional.</summary>
public sealed record VideoSidecarInfo(
    string? Title,
    string? Description,
    string? Channel,
    string? YoutubeId,
    DateOnly? ReleaseDate,
    string Source)
{
    public bool HasAnything => Title is not null || Description is not null || Channel is not null || YoutubeId is not null || ReleaseDate is not null;
}

/// <summary>
/// Reads the files that sit beside a video and describe it: a yt-dlp <c>.info.json</c> and a Kodi <c>.nfo</c>. All
/// local; nothing is sent anywhere. Files are size-capped and read defensively, so a broken or hostile file only
/// means "no sidecar".
/// </summary>
public static class VideoSidecarReader
{
    private const long MaxSidecarBytes = 4 * 1024 * 1024;

    public static string InfoJsonPath(string videoPath) =>
        Path.Combine(Path.GetDirectoryName(videoPath) ?? string.Empty, Path.GetFileNameWithoutExtension(videoPath) + ".info.json");

    public static string NfoPath(string videoPath) =>
        Path.Combine(Path.GetDirectoryName(videoPath) ?? string.Empty, Path.GetFileNameWithoutExtension(videoPath) + ".nfo");

    public static VideoSidecarInfo? ReadInfoJson(string videoPath)
    {
        var path = InfoJsonPath(videoPath);
        if (!TryOpen(path, out var stream))
        {
            return null;
        }

        using (stream)
        {
            try
            {
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var id = Text(root, "id");
                var info = new VideoSidecarInfo(
                    Text(root, "title"),
                    Text(root, "description"),
                    Text(root, "channel") ?? Text(root, "uploader"),
                    id is not null && VideoTitleCleaner.LooksLikeYoutubeId(id) ? id : null,
                    ParseUploadDate(Text(root, "upload_date")),
                    Domain.Configuration.VideoIdentificationMethods.InfoJson);
                return info.HasAnything ? info : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public static VideoSidecarInfo? ReadNfo(string videoPath)
    {
        var path = NfoPath(videoPath);
        if (!TryOpen(path, out var stream))
        {
            return null;
        }

        using (stream)
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxSidecarBytes };
                using var reader = XmlReader.Create(stream, settings);
                var doc = new XmlDocument { XmlResolver = null };
                doc.Load(reader);
                var root = doc.DocumentElement;
                if (root is null)
                {
                    return null;
                }

                string? Node(params string[] names)
                {
                    foreach (var name in names)
                    {
                        var text = root.SelectSingleNode(name)?.InnerText?.Trim();
                        if (!string.IsNullOrEmpty(text))
                        {
                            return text;
                        }
                    }

                    return null;
                }

                var date = Node("premiered", "aired", "releasedate", "dateadded");
                var info = new VideoSidecarInfo(
                    Node("title"),
                    Node("plot", "outline"),
                    Node("studio", "director"),
                    null,
                    date is not null && DateOnly.TryParse(date[..Math.Min(10, date.Length)], CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
                    Domain.Configuration.VideoIdentificationMethods.Nfo);
                return info.HasAnything ? info : null;
            }
            catch (XmlException)
            {
                return null;
            }
        }
    }

    private static bool TryOpen(string path, out FileStream stream)
    {
        stream = null!;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > MaxSidecarBytes)
            {
                return false;
            }

            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static DateOnly? ParseUploadDate(string? raw) =>
        raw is { Length: 8 } && DateOnly.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
