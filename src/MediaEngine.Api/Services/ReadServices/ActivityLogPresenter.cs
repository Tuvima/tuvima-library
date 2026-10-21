using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Activity;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Turns one stored activity row into the plain words the Activity log shows. The Dashboard shows these words as they are,
/// so every kind of event is worded here and nowhere else. It only ever uses text the writer already put in the row's
/// name, detail or reason (writers keep those plain); the error text some writers keep in the change data is never shown.
/// </summary>
public static partial class ActivityLogPresenter
{
    private const string UntitledItem = "Untitled item";
    private const string NeedsReviewRoute = "/settings/review";
    private const string ToolsRoute = "/settings/tools";

    /// <summary>Words one row, or returns null when its kind is not shown in the Activity log.</summary>
    public static ActivityLogEntryDto? Present(SystemActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var category = ActivityLogCategories.CategoryOf(entry.ActionType);
        if (category is null)
        {
            return null;
        }

        var changes = Changes.Parse(entry.ChangesJson);
        var dto = new ActivityLogEntryDto
        {
            Id = entry.Id,
            OccurredAt = entry.OccurredAt,
            Category = category,
            Kind = entry.ActionType,
        };

        switch (entry.ActionType)
        {
            case SystemActionType.MediaAdded:
                var mediaType = MediaTypeLabel(changes.Text("media_type"));
                dto.Title = FirstText(entry.CollectionName) ?? UntitledItem;
                dto.Detail = mediaType is null ? "Added" : $"Added to {mediaType}";
                dto.MediaType = mediaType;
                dto.Route = WorkRoute(entry.EntityId, changes.Text("media_type"));
                dto.CoverUrl = WorkCoverUrl(entry.EntityId);
                break;

            case SystemActionType.MediaRemoved:
                // One entry per deletion: the writer put the title (or "12 songs") in the name and who, files and space in the detail.
                dto.Title = $"Deleted {FirstText(entry.CollectionName) ?? "an item"}";
                dto.Detail = FirstText(entry.Detail);
                break;

            case SystemActionType.PersonalTrashEmptied:
                // The writer keeps only counts and size in the detail ("128 items, 4.2 GB freed, 2 skipped"); no file names or titles exist here.
                dto.Title = "Emptied Trash";
                dto.Detail = FirstText(entry.Detail);
                break;

            case SystemActionType.PersonalMediaAdded:
                dto.Title = FirstText(entry.CollectionName) ?? "Personal media";
                dto.Detail = FirstText(entry.Detail);
                dto.MediaType = changes.Number("photos") is 0 && changes.Number("videos") > 0 ? "Videos" : "Photos";
                dto.Count = changes.Number("count");
                break;

            case SystemActionType.ReviewItemCreated:
                dto.Title = FirstText(entry.CollectionName) ?? UntitledItem;
                dto.Detail = "Needs Review";
                dto.Route = NeedsReviewRoute;
                break;

            case SystemActionType.ReviewItemResolved:
                dto.Title = FirstText(entry.CollectionName) ?? UntitledItem;
                dto.Detail = "Review resolved";
                dto.Route = NeedsReviewRoute;
                break;

            case SystemActionType.MediaFailed:
            case SystemActionType.FileQuarantined:
            case SystemActionType.ArtworkWritebackFailed:
                dto.Title = FirstText(entry.CollectionName) ?? changes.Text("source_file") ?? "A file";
                dto.Detail = changes.Text("reason") ?? WithoutFailurePrefix(entry.Detail);
                dto.MediaType = MediaTypeLabel(changes.Text("media_type"));
                break;

            case SystemActionType.ToolMissing:
                var tool = ToolName(entry, changes);
                dto.Title = string.Equals(changes.Text("state"), "problem", StringComparison.OrdinalIgnoreCase)
                    ? $"{tool} isn't working"
                    : $"Couldn't find {tool}";
                dto.Detail = FirstText(entry.Detail);
                dto.Route = ToolsRoute;
                break;

            case SystemActionType.ToolFound:
                dto.Title = $"Found {ToolName(entry, changes)}";
                dto.Detail = FirstText(entry.Detail);
                dto.Route = ToolsRoute;
                break;

            case SystemActionType.ToolDownloadChecksumRefused:
                dto.Title = $"Refused a download of {ToolName(entry, changes)}";
                dto.Detail = "The download didn't match its recorded checksum, so it was thrown away.";
                break;

            case SystemActionType.ToolDownloadFailed:
                dto.Title = $"Couldn't download {ToolName(entry, changes)}";
                dto.Detail = changes.Text("reason") ?? FirstText(entry.Detail);
                break;

            case SystemActionType.ToolDownloadStarted:
                dto.Title = $"Downloading {ToolName(entry, changes)}";
                break;

            case SystemActionType.ToolDownloadFinished:
                dto.Title = $"Downloaded {ToolName(entry, changes)}";
                break;

            case SystemActionType.ToolDownloadCancelled:
                dto.Title = $"Cancelled the download of {ToolName(entry, changes)}";
                break;

            case SystemActionType.ProviderDown:
                var down = ProviderName(entry, changes);
                dto.Title = $"{down} isn't responding";
                dto.Detail = $"Matching and details from {down} wait until it's back. The Library keeps checking.";
                break;

            case SystemActionType.ProviderRecovered:
                dto.Title = $"{ProviderName(entry, changes)} is back";
                dto.Detail = "Items that were waiting for it carry on.";
                break;

            case SystemActionType.ServerStarted:
                dto.Title = "The Library started";
                break;

            case SystemActionType.ServerStopped:
                dto.Title = "The Library stopped";
                break;

            case SystemActionType.ActivityPruned:
                dto.Title = "Cleared old log entries";
                dto.Detail = FirstText(entry.Detail);
                break;

            case SystemActionType.ReconciliationCompleted:
                dto.Title = "Checked the library folders";
                dto.Detail = FirstText(entry.Detail);
                break;

            default:
                dto.Title = FirstText(entry.CollectionName) ?? FirstText(entry.Detail) ?? SplitIntoWords(entry.ActionType);
                break;
        }

        return dto;
    }

    private static string ToolName(SystemActivityEntry entry, Changes changes) =>
        changes.Text("tool") ?? FirstText(entry.CollectionName) ?? "a tool";

    private static string ProviderName(SystemActivityEntry entry, Changes changes) =>
        FirstText(entry.CollectionName) ?? changes.Text("provider_id") ?? "A provider";

    private static string? FirstText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // The older writer put "Failed — {file}: {reason}" in the detail; the reason alone reads better under the file name.
    private static string? WithoutFailurePrefix(string? detail)
    {
        var text = FirstText(detail);
        if (text is null)
        {
            return null;
        }

        var match = FailurePrefix().Match(text);
        return match.Success ? FirstText(text[match.Length..]) : text;
    }

    private static string? MediaTypeLabel(string? mediaType) => mediaType?.Trim() switch
    {
        null or "" => null,
        "Movies" => "Movies",
        "Books" => "Books",
        "Audiobooks" => "Audiobooks",
        "Comics" => "Comics",
        "TV" => "TV",
        "Music" => "Music",
        "Unknown" => null,
        var other => other,
    };

    // Only a title that has its own page gets a link. A TV episode or music track is a Work inside a show or album, so it
    // is left without one rather than sending the person to the wrong page.
    private static string? WorkRoute(Guid? workId, string? mediaType)
    {
        if (workId is not { } id || id == Guid.Empty)
        {
            return null;
        }

        return MediaTypeLabel(mediaType) is "Movies" or "Books" or "Audiobooks" or "Comics"
            ? IngestionPresentationReadService.DetailRoute(MediaTypeLabel(mediaType)!, id)
            : null;
    }

    // The same small cover address the other Engine lists use; a work without art answers with the Engine's placeholder.
    private static string? WorkCoverUrl(Guid? workId) =>
        workId is { } id && id != Guid.Empty ? $"/stream/entity/work/{id:D}/cover?size=s" : null;

    private static string SplitIntoWords(string kind)
    {
        var words = WordBoundary().Replace(kind, " ").Trim();
        return words.Length == 0
            ? kind
            : string.Concat(words[..1].ToUpper(CultureInfo.InvariantCulture), words[1..].ToLower(CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^Failed\s*[—–-]\s*[^:]*:\s*")]
    private static partial Regex FailurePrefix();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordBoundary();

    // The few text and number fields the wording reads from a row's change text. Anything unreadable is simply absent.
    private readonly struct Changes
    {
        private readonly JsonElement? _root;

        private Changes(JsonElement? root) => _root = root;

        public static Changes Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Changes(null);
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                return new Changes(document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null);
            }
            catch (JsonException)
            {
                // Change text that is not JSON has nothing to word; the stored name and detail still show.
                return new Changes(null);
            }
        }

        public string? Text(string name) =>
            _root is { } root && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? FirstText(value.GetString())
                : null;

        public int? Number(string name) =>
            _root is { } root && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number
                : null;
    }
}
