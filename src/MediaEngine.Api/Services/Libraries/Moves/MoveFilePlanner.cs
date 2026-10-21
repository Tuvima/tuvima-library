using System.Globalization;
using System.Text;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Libraries.Moves;

/// <summary>What a move does to one item's files: a before and after name, or one plain line saying why the file stays put.</summary>
/// <param name="Change">The new name and place when exactly one file is renamed and moved.</param>
/// <param name="Note">One plain line about the files (several files moving, or why they are not touched).</param>
/// <param name="Warning">A problem the person should read before confirming (a name that is already taken). Null when there is none.</param>
/// <param name="FingerprintText">Everything the answer depended on, so the confirm step notices when it changed.</param>
public sealed record MoveFilePreview(LibraryMoveFileChange? Change, string? Note, string? Warning, string FingerprintText);

/// <summary>
/// Says exactly what a move does to each item's file before anything changes (Details overlay D19). In a Managed folder the file is
/// always renamed and moved to fit its new place; As-is folders, read-only folders and protected files are never touched, and nothing
/// is ever overwritten. It asks the organiser's own re-file what it would do for the item's new place, so the preview and the
/// durable job that carries the move out give the same answer. A file name depends on the number the person gives the item, so the
/// preview names the file for the entry the person chose, or for the entry the Match step pre-selects when they have not chosen yet.
/// </summary>
public sealed class MoveFilePlanner(IAutoOrganizeService organizer)
{
    public const string AsIsNote = "As-is folder: the file stays where it is.";
    public const string AsIsNoteMany = "As-is folder: the files stay where they are.";
    public const string ProtectedNote = "Protected original: the file is not touched.";
    public const string ProtectedNoteMany = "Protected originals: the files are not touched.";
    public const string ReadOnlyNote = "Read-only folder: the file stays where it is.";
    public const string ReadOnlyNoteMany = "Read-only folder: the files stay where they are.";
    public const string TakenNote = "A file with that name is already there, so this file stays where it is.";
    public const string TakenNoteMany = "Files with those names are already there, so these files stay where they are.";
    public const string UnavailableNote = "The file can't be renamed right now, so it stays where it is.";
    public const string UnavailableNoteMany = "The files can't be renamed right now, so they stay where they are.";
    public const string RenameToFitNote = "The file will be renamed and moved to fit its new place.";
    public const string RenameToFitNoteMany = "The files will be renamed and moved to fit their new place.";

    /// <summary>
    /// Plans every item that can move. <paramref name="entries"/> holds the entry each item would take (the person's choice, else the
    /// one the Match step pre-selects); an item with none is not named yet, and the answer is only about whether its file may be touched
    /// at all. An item in <paramref name="addedAnyway"/> has no entry on purpose: it is named from where it sits unnumbered. A
    /// hand-picked collection never touches a file, so it gives nothing.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, MoveFilePreview>> PlanAsync(
        IReadOnlyList<LibraryMoveItem> items,
        LibraryMoveDestinationInfo destination,
        bool hasList,
        IReadOnlyDictionary<Guid, MoveListEntry> entries,
        IReadOnlySet<Guid> addedAnyway,
        IReadOnlySet<Guid> skip,
        CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, MoveFilePreview>();
        if (destination.Kind == LibraryMoveKinds.Collection)
        {
            return result;
        }

        // Two files of one move can never take the same name: the second one stays where it is.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(item => !skip.Contains(item.ItemId) && item.AssetIds.Count > 0))
        {
            entries.TryGetValue(item.ItemId, out var entry);
            var placement = PlacementFor(item, destination, hasList ? entry : null);
            var plans = new List<AssetRefilePlan>(item.AssetIds.Count);
            foreach (var assetId in item.AssetIds)
            {
                var plan = await organizer.PlanRefileAsync(assetId, placement, ct).ConfigureAwait(false);
                if (plan.Disposition == AssetRefileDisposition.Moves && !claimed.Add(plan.TargetPath!))
                {
                    plan = AssetRefilePlan.Of(
                        AssetRefileDisposition.Collision, plan.CurrentPath, "Another file in this move takes that name.", plan.TargetPath);
                }

                plans.Add(plan);
            }

            result[item.ItemId] = Describe(item, plans, named: !hasList || entry is not null || addedAnyway.Contains(item.ItemId));
        }

        return result;
    }

    /// <summary>
    /// Where the item would sit after the move, as the commit will leave it: the entry's Work when it merges into one, the place above,
    /// and the numbers the move writes (an item with no entry gets none, like one added anyway).
    /// </summary>
    private static AssetRefilePlacement PlacementFor(LibraryMoveItem item, LibraryMoveDestinationInfo destination, MoveListEntry? entry)
    {
        var parentScope = destination.ParentWorkId ?? destination.Id;
        var numbers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (item.IsEdition)
        {
            // An audiobook part: the file joins the audiobook and takes the part number of the entry.
            numbers[MovePlacementNumbers.AudiobookPartNumberKey] =
                entry?.Target.PartNumber?.ToString(CultureInfo.InvariantCulture);
            return new AssetRefilePlacement(destination.Id, destination.ParentWorkId, parentScope, numbers);
        }

        foreach (var key in MovePlacementNumbers.WorkKeys)
        {
            numbers[key] = null;
        }

        var target = entry?.Target;
        foreach (var (key, value) in MovePlacementNumbers.For(
                     destination.MediaType, destination.SeasonNumber, target?.Ordinal, target?.DiscNumber))
        {
            numbers[key] = value;
        }

        return new AssetRefilePlacement(target?.ExistingWorkId ?? item.WorkId, destination.Id, parentScope, numbers);
    }

    private static MoveFilePreview Describe(LibraryMoveItem item, IReadOnlyList<AssetRefilePlan> plans, bool named)
    {
        var fingerprint = new StringBuilder();
        fingerprint.Append("file|").Append(item.ItemId.ToString("N"));
        foreach (var plan in plans)
        {
            fingerprint.Append('|').Append(plan.Disposition).Append(':').Append(plan.CurrentPath).Append("=>").Append(plan.TargetPath);
        }

        var moves = plans.Where(plan => plan.Disposition == AssetRefileDisposition.Moves).ToList();
        var taken = plans.Where(plan => plan.Disposition == AssetRefileDisposition.Collision).ToList();
        var notes = new List<string>();

        if (!named)
        {
            // The file's name depends on the number the person gives it, so only whether it may be touched is known yet.
            var touchable = plans.Any(plan => plan.Disposition is AssetRefileDisposition.Moves or AssetRefileDisposition.Collision);
            if (touchable)
            {
                notes.Add(plans.Count > 1 ? RenameToFitNoteMany : RenameToFitNote);
            }

            notes.AddRange(NotesForKept(plans));
            return new MoveFilePreview(null, notes.Count == 0 ? null : string.Join(' ', notes), null, fingerprint.ToString());
        }

        LibraryMoveFileChange? change = null;
        if (moves.Count == 1 && plans.Count == 1)
        {
            change = new LibraryMoveFileChange { From = moves[0].CurrentPath, To = moves[0].TargetPath! };
        }
        else if (moves.Count > 0)
        {
            var folder = FolderName(moves[0].TargetPath!);
            notes.Add(moves.Count == plans.Count
                ? $"{moves.Count.ToString(CultureInfo.InvariantCulture)} files will be renamed and moved to {folder}."
                : $"{moves.Count.ToString(CultureInfo.InvariantCulture)} of {plans.Count.ToString(CultureInfo.InvariantCulture)} files will be renamed and moved to {folder}.");
        }

        notes.AddRange(NotesForKept(plans));

        string? warning = null;
        if (taken.Count > 0)
        {
            var names = string.Join(", ", taken.Select(plan => Path.GetFileName(plan.TargetPath!)).Distinct(StringComparer.OrdinalIgnoreCase));
            warning = $"{names} is already there, so {(taken.Count == 1 ? "that file stays" : "those files stay")} where {(taken.Count == 1 ? "it is" : "they are")}";
        }

        return new MoveFilePreview(change, notes.Count == 0 ? null : string.Join(' ', notes), warning, fingerprint.ToString());
    }

    private static IEnumerable<string> NotesForKept(IReadOnlyList<AssetRefilePlan> plans)
    {
        foreach (var group in plans
                     .Where(plan => plan.Disposition is not (AssetRefileDisposition.Moves or AssetRefileDisposition.AlreadyFits))
                     .GroupBy(plan => plan.Disposition)
                     .OrderBy(group => group.Key))
        {
            var many = group.Count() > 1;
            yield return group.Key switch
            {
                AssetRefileDisposition.AsIs => many ? AsIsNoteMany : AsIsNote,
                AssetRefileDisposition.ReadOnly => many ? ReadOnlyNoteMany : ReadOnlyNote,
                AssetRefileDisposition.Protected => many ? ProtectedNoteMany : ProtectedNote,
                AssetRefileDisposition.Collision => many ? TakenNoteMany : TakenNote,
                _ => many ? UnavailableNoteMany : UnavailableNote,
            };
        }
    }

    /// <summary>The folder a file lands in, as a person reads it ("OK Computer").</summary>
    private static string FolderName(string path)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(folder) ? "its new folder" : folder;
    }
}
