using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Items;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Extras;

/// <summary>
/// The History tab of a supporting file (universal editor v5 D16, spec 3a): what was changed in the editor and when. A saved edit adds one
/// row for each field it changed and for each file move, and a delete adds one row, all on the file's own timeline (keyed on the file's id,
/// in the same store every other editor history uses). The editor reads the rows back as the same history rows a library title shows, so
/// one History tool draws them all.
/// </summary>
public static class SupportingFileHistory
{
    public const string EntityType = "SupportingFile";
    public const string FieldEvent = "supporting_file_edit";
    public const string MoveEvent = "supporting_file_moved";
    public const string DeleteEvent = "supporting_file_removed";
    public const string Trigger = "editor";

    /// <summary>The words for each changed field of one file, in the order the editor shows the fields; empty when nothing would change.</summary>
    public static IReadOnlyList<string> Describe(ItemExtra before, ExtraOwnedUpdate update, ExtrasConfiguration? config)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(update);
        var rows = new List<string>();

        if (update.Title.IsSet && !string.Equals(before.OwnTitle, update.Title.Value, StringComparison.Ordinal))
        {
            rows.Add($"Title changed to \"{update.Title.Value}\".");
        }
        else if (update.Title is { State: ExtraValueState.Cleared } && !string.IsNullOrWhiteSpace(before.OwnTitle))
        {
            rows.Add("Title cleared. The title read from the file name shows again.");
        }

        if (update.Description.IsSet && !string.Equals(before.OwnDescription, update.Description.Value, StringComparison.Ordinal))
        {
            rows.Add("Description changed.");
        }
        else if (update.Description is { State: ExtraValueState.Cleared } && !string.IsNullOrWhiteSpace(before.OwnDescription))
        {
            rows.Add("Description cleared.");
        }

        if (update.GroupName.IsSet)
        {
            var name = update.GroupName.Value!.Trim();
            if (!string.Equals(before.GroupName, name, StringComparison.OrdinalIgnoreCase) || before.GroupSource != ExtraGroupSources.You)
            {
                rows.Add($"Group set to \"{name}\".");
            }
        }
        else if (update.NoGroup)
        {
            if (before.GroupName is not null || before.GroupSource != ExtraGroupSources.You)
            {
                rows.Add("Group removed. The file is in no group.");
            }
        }
        else if (update.GroupName is { State: ExtraValueState.Cleared } && before.GroupSource == ExtraGroupSources.You)
        {
            rows.Add("Group cleared. The group read from its folder applies again.");
        }

        if (update.Role.IsSet && !string.Equals(before.Role, update.Role.Value, StringComparison.Ordinal))
        {
            var label = config?.LabelFor(before.Kind, update.Role.Value) ?? ExtraRoles.Label(update.Role.Value);
            rows.Add($"Type set to \"{label}\".");
        }
        else if (update.Role is { State: ExtraValueState.Cleared } && (before.Role is not null || before.ExtraKind is not null))
        {
            rows.Add("Type cleared. The type guessed from the names applies again.");
        }

        if (update.Scope.IsSet && !string.Equals(before.Scope?.Key, update.Scope.Value.Key, StringComparison.Ordinal))
        {
            rows.Add($"Belongs to {PlaceWords(update.Scope.Value)}.");
        }
        else if (update.Scope is { State: ExtraValueState.Cleared }
            && (before.Scope is not null || before.PlaceSeason is not null || before.PlaceEpisode is not null || before.PlaceTrack is not null))
        {
            rows.Add($"Belongs to its folder's place again ({PlaceWords(before.InferredScope)}).");
        }

        if (update.Alongside.IsSet && !string.Equals(before.Alongside?.Key, update.Alongside.Value.Key, StringComparison.Ordinal))
        {
            rows.Add(update.Alongside.Value.Level == ExtraScopeLevel.None
                ? "Shown alongside no episode or lesson."
                : $"Shown alongside {PlaceWords(update.Alongside.Value)}.");
        }
        else if (update.Alongside is { State: ExtraValueState.Cleared } && before.Alongside is not null)
        {
            rows.Add("Shown alongside the episode or lesson guessed from the file name again.");
        }

        var tags = before.TagList;
        var added = update.AddTags.Where(tag => !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
        if (added.Count > 0)
        {
            rows.Add($"Tags added: {string.Join(", ", added)}.");
        }

        var removed = update.RemoveTags.Where(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
        if (removed.Count > 0)
        {
            rows.Add($"Tags removed: {string.Join(", ", removed)}.");
        }

        return rows;
    }

    /// <summary>The words for a file move: from where, to where, below the title's folder.</summary>
    public static string MoveWords(ItemExtra file, string from, string to) =>
        $"Moved from \"{Relative(file.ItemFolder, from)}\" to \"{Relative(file.ItemFolder, to)}\".";

    /// <summary>The words for a delete: what happened to the file on disk.</summary>
    public static string DeleteWords(string diskOutcome, string? reason) => diskOutcome switch
    {
        SupportingFileDiskOutcomes.Recycled => "Removed from the Library. The file was moved to the Recycle bin.",
        SupportingFileDiskOutcomes.Deleted => "Removed from the Library. The file was deleted from the disk.",
        SupportingFileDiskOutcomes.Kept => string.IsNullOrWhiteSpace(reason)
            ? "Removed from the Library. The file was left on the disk."
            : $"Removed from the Library. The file was left on the disk ({reason.TrimEnd('.')}).",
        _ => "Removed from the Library. The file was already gone from the disk.",
    };

    public static EntityEvent Row(Guid fileId, string eventType, string detail) => new()
    {
        EntityId = fileId,
        EntityType = EntityType,
        EventType = eventType,
        Trigger = Trigger,
        Detail = detail,
    };

    public static async Task<IReadOnlyList<LibraryItemHistoryDto>> Read(IEntityTimelineRepository timeline, Guid fileId, CancellationToken ct)
    {
        var events = await timeline.GetEventsByEntityAsync(fileId, ct).ConfigureAwait(false);
        return events
            .Where(evt => evt.EventType is FieldEvent or MoveEvent or DeleteEvent)
            .Select(evt => new LibraryItemHistoryDto
            {
                Id = evt.Id.ToString("D"),
                EntityId = fileId,
                OccurredAt = evt.OccurredAt,
                EventType = evt.EventType,
                Label = evt.EventType switch
                {
                    MoveEvent => "File moved",
                    DeleteEvent => "Removed from the Library",
                    _ => "Details changed",
                },
                Detail = evt.Detail,
                Category = evt.EventType == FieldEvent ? "manual" : "file",
                ActorLabel = "You",
            })
            .OrderByDescending(item => item.OccurredAt)
            .ToList();
    }

    private static string PlaceWords(ExtraScope scope) =>
        scope.Level == ExtraScopeLevel.Root ? "the whole title" : scope.Label();

    private static string Relative(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
