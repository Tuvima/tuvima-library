using System.Globalization;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// What has happened in one library (Library pages D16): the events the Engine already records, filtered to this library and worded in
/// plain sentences. Nothing new is stored. The caller has already decided the person may see the library (the library visibility rule);
/// a Vault library is never passed in. Words for added, review and failed rows come from <see cref="ActivityLogPresenter"/>, so the same
/// event reads the same here as in System &gt; Logs.
/// </summary>
public sealed class LibraryActivityReadService(ILibraryActivityReadRepository activity)
{
    public const int DefaultTake = 50;
    public const int MaximumTake = 200;
    private const string NeedsReviewRoute = "/settings/review";

    public static int ClampTake(int? take) => Math.Clamp(take ?? DefaultTake, 1, MaximumTake);

    public async Task<LibraryActivityDto> GetAsync(LibraryFolderConfig library, int? take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        var libraryId = Guid.Parse(library.Id);
        var limit = ClampTake(take);
        var sources = library.Sources
            .Where(source => Guid.TryParse(source.Id, out _))
            .Select(source => new LibraryActivitySource(Guid.Parse(source.Id), source.Path))
            .ToList();
        var folderNames = sources.ToDictionary(source => source.SourceId, source => FolderName(source.Path));

        var entries = new List<LibraryActivityEntryDto>();
        foreach (var row in await activity.GetEntriesAsync(libraryId, sources, limit, ct).ConfigureAwait(false))
        {
            if (Word(row) is { } entry)
            {
                entries.Add(entry);
            }
        }

        foreach (var check in await activity.GetFolderChecksAsync(libraryId, limit, ct).ConfigureAwait(false))
        {
            entries.Add(Word(check, folderNames));
        }

        foreach (var run in await activity.GetOrganiseRunsAsync(sources.Select(source => source.SourceId).ToList(), limit, ct).ConfigureAwait(false))
        {
            if (Word(run, folderNames) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return new LibraryActivityDto(
            libraryId,
            entries.OrderByDescending(entry => entry.OccurredAt).ThenBy(entry => entry.Id, StringComparer.Ordinal).Take(limit).ToList());
    }

    private static LibraryActivityEntryDto? Word(SystemActivityEntry row)
    {
        var id = $"activity-{row.Id.ToString(CultureInfo.InvariantCulture)}";
        if (row.ActionType == SystemActionType.FolderKindChanged)
        {
            return string.IsNullOrWhiteSpace(row.Detail)
                ? null
                : new LibraryActivityEntryDto(id, row.OccurredAt, LibraryActivityKinds.Move, Sentence(row.Detail), false, null);
        }

        if (ActivityLogPresenter.Present(row) is not { } shown)
        {
            return null;
        }

        return row.ActionType switch
        {
            SystemActionType.MediaAdded =>
                new LibraryActivityEntryDto(id, row.OccurredAt, LibraryActivityKinds.Added, $"Added {shown.Title}.", false, null),
            SystemActionType.ReviewItemCreated =>
                new LibraryActivityEntryDto(id, row.OccurredAt, LibraryActivityKinds.Review, $"{shown.Title} needs review.", true, NeedsReviewRoute),
            SystemActionType.ReviewItemResolved =>
                new LibraryActivityEntryDto(id, row.OccurredAt, LibraryActivityKinds.Review, $"Review finished for {shown.Title}.", false, null),
            SystemActionType.MediaFailed =>
                new LibraryActivityEntryDto(
                    id, row.OccurredAt, LibraryActivityKinds.Failed,
                    string.IsNullOrWhiteSpace(shown.Detail) ? $"Couldn't take in {shown.Title}." : $"Couldn't take in {shown.Title}: {Sentence(shown.Detail)}",
                    false, null),
            _ => null,
        };
    }

    private static LibraryActivityEntryDto Word(LibraryActivityFolderCheck check, IReadOnlyDictionary<Guid, string> folderNames)
    {
        var folder = Guid.TryParse(check.SourceId, out var sourceId) && folderNames.TryGetValue(sourceId, out var name) ? name : FolderName(check.Path);
        var at = check.EndedAt ?? check.StartedAt;
        var unmatched = check.Unmatched.ToString("N0", CultureInfo.InvariantCulture);
        var text = check.State switch
        {
            "checking" => $"Checking {folder} for the first time.",
            "paused" => $"Paused the first check of {folder}.",
            "kept_going" => $"Kept {folder} after the first check ({Files(check.Tried)} looked at, {unmatched} not matched).",
            _ => $"Checked {folder}: {Files(check.Tried)} looked at, {unmatched} not matched.",
        };
        return new LibraryActivityEntryDto($"check-{check.SourceId}-{check.StartedAt.ToUnixTimeMilliseconds()}", at, LibraryActivityKinds.Scan, text, false, null);
    }

    private static LibraryActivityEntryDto? Word(LibraryActivityOrganiseRun run, IReadOnlyDictionary<Guid, string> folderNames)
    {
        var folder = folderNames.GetValueOrDefault(run.SourceId, "a folder");
        var failed = run.Failed > 0 ? $" {Files(run.Failed)} could not be moved." : string.Empty;
        var id = $"job-{run.OperationId:N}-{run.SourceId:N}";
        if (run.Copied > 0)
        {
            return new LibraryActivityEntryDto(id, run.LastAt, LibraryActivityKinds.Merge, $"Merged {Files(run.Copied)} into {folder}.{failed}", false, null);
        }

        // A change of what a folder is is worded from its own activity row; a run that only did that has nothing more to say.
        return run.Moved > 0 || run.Failed > 0
            ? new LibraryActivityEntryDto(id, run.LastAt, LibraryActivityKinds.Conversion, $"Renamed or moved {Files(run.Moved)} in {folder}.{failed}", false, null)
            : null;
    }

    private static string Files(int count) => $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? "file" : "files")}";

    private static string FolderName(string path)
    {
        var trimmed = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return string.IsNullOrWhiteSpace(name) ? "a folder" : name;
    }

    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.EndsWith('.') ? trimmed : trimmed + ".";
    }
}
