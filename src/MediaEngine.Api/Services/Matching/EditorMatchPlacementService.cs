using System.Globalization;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// Works out, before anything is saved, where applying a match would put the item and its file (universal editor v5, D8): the album,
/// season or series the chosen record names, found among the places the Library already has, and whether the file sits in a Managed folder
/// that moves by itself. A match never creates a place. A Read-only or As-is folder never gets a file move; the match still applies there.
/// Reads only: the places and the previews are the Library move's own, so what the person is shown is what Save changes will do.
/// </summary>
public sealed class EditorMatchPlacementService(
    ILibraryMoveService moves,
    ILibraryMoveRepository moveItems,
    IItemCanonicalRepository items,
    IAutoOrganizeService organizer)
{
    /// <summary>Null when no such item is in the Library.</summary>
    public async Task<EditorMatchPlacementDto?> PlanAsync(
        Guid entityId, ItemCanonicalRetailCandidateDto candidate, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var context = await items.ResolveWorkAssetContextAsync(entityId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var proposed = ProposedParentOf(candidate, context.MediaType);
        LibraryMoveDestination? current = null;
        LibraryMoveDestination? target = null;
        string? blocked = null;
        if (proposed is not null)
        {
            var places = await moves.GetDestinationsAsync(entityId, PlaceQuery(proposed), canEdit, ct).ConfigureAwait(false);
            if (!places.IsSuccess)
            {
                blocked = places.Refusal!.Message;
            }
            else
            {
                current = places.Value!.CurrentDestination;
                target = places.Value.Destinations.FirstOrDefault(place => IsSamePlace(proposed, place.Label, place.ParentLabel));
                if (target is null && (current is null || !IsSamePlace(proposed, current.Label, current.ParentLabel)))
                {
                    blocked = $"There is no {proposed.Label} in your library yet, so it stays where it is.";
                }
            }
        }

        var file = await PlanFileAsync(entityId, target, actor, canEdit, ct).ConfigureAwait(false);
        return new EditorMatchPlacementDto
        {
            DestinationId = target?.Id,
            ParentLabel = target is null ? null : Describe(target),
            CurrentParentLabel = current is null ? null : Describe(current),
            Managed = file.Managed,
            FromPath = file.From,
            ToPath = file.To,
            BlockedReason = blocked ?? file.Blocked,
        };
    }

    private sealed record FilePlan(bool Managed, string? From, string? To, string? Blocked);

    // Whether the item's file would follow: with a new place, the Library move's own preview says; otherwise the organiser's plan for the
    // file as it sits now. Only a Managed folder moves; any other folder keeps its file and says why.
    private async Task<FilePlan> PlanFileAsync(Guid entityId, LibraryMoveDestination? target, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        if (target is not null)
        {
            var preview = await moves.PreviewAsync(
                new LibraryMovePreviewRequest { ItemIds = [entityId], DestinationId = target.Id }, actor, canEdit, ct).ConfigureAwait(false);
            var row = preview.IsSuccess ? preview.Value!.Rows.FirstOrDefault() : null;
            if (row is null)
            {
                return new FilePlan(false, null, null, preview.IsSuccess ? null : preview.Refusal!.Message);
            }

            return FilesCannotMove(row)
                ? new FilePlan(false, null, null, row.BlockedReason ?? row.FileNote)
                : new FilePlan(true, row.FileChange?.From, row.FileChange?.To, null);
        }

        var item = (await moveItems.GetItemsAsync([entityId], ct).ConfigureAwait(false)).FirstOrDefault();
        var assetId = item?.AssetIds.FirstOrDefault();
        if (assetId is not { } id || id == Guid.Empty)
        {
            return new FilePlan(false, null, null, null);
        }

        var plan = await organizer.PlanRefileAsync(id, null, ct).ConfigureAwait(false);
        return plan.Disposition switch
        {
            AssetRefileDisposition.Moves => new FilePlan(true, plan.CurrentPath, plan.TargetPath, null),
            // The file already has the right place; the match may still rename it once the new details are saved.
            AssetRefileDisposition.AlreadyFits => new FilePlan(true, null, null, null),
            _ => new FilePlan(false, null, null, plan.Reason),
        };
    }

    /// <summary>Whether a move preview says the file cannot move (a blocked reason, or a note with no file change).</summary>
    internal static bool FilesCannotMove(LibraryMovePreviewRow row) =>
        !string.IsNullOrWhiteSpace(row.BlockedReason) || (row.FileChange is null && !string.IsNullOrWhiteSpace(row.FileNote));

    private static string Describe(LibraryMoveDestination place) =>
        string.IsNullOrWhiteSpace(place.ParentLabel) ? place.Label : $"{place.Label} ({place.ParentLabel})";

    internal sealed record ProposedParent(string Label, string? ParentLabel);

    // The place a result names, in the words the Library's own places use: the album a song is on, "Season 2" of the show an episode is in,
    // the series a title is in. Null when the result names none.
    internal static ProposedParent? ProposedParentOf(ItemCanonicalRetailCandidateDto candidate, string mediaType)
    {
        if (string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase))
        {
            var album = Field(candidate, MetadataFieldConstants.Album);
            return album is null ? null : new ProposedParent(album, Field(candidate, MetadataFieldConstants.Artist));
        }

        if (string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase))
        {
            var show = Field(candidate, MetadataFieldConstants.ShowName);
            if (show is null)
            {
                return null;
            }

            var season = Field(candidate, MetadataFieldConstants.SeasonNumber);
            return season is not null && int.TryParse(season, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? new ProposedParent(number == 0 ? "Specials" : $"Season {number.ToString(CultureInfo.InvariantCulture)}", show)
                : new ProposedParent(show, null);
        }

        var series = Field(candidate, MetadataFieldConstants.Series);
        return series is null ? null : new ProposedParent(series, null);
    }

    private static string PlaceQuery(ProposedParent proposed) =>
        proposed.Label.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) || string.Equals(proposed.Label, "Specials", StringComparison.OrdinalIgnoreCase)
            ? proposed.ParentLabel ?? proposed.Label
            : proposed.Label;

    internal static bool IsSamePlace(ProposedParent proposed, string label, string? parentLabel)
    {
        var sameLabel = string.Equals(proposed.Label.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(proposed.Label, "Specials", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(label.Trim(), "Season 0", StringComparison.OrdinalIgnoreCase));
        return sameLabel
               && (proposed.ParentLabel is null
                   || parentLabel is null
                   || string.Equals(proposed.ParentLabel.Trim(), parentLabel.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string? Field(ItemCanonicalRetailCandidateDto candidate, string key)
    {
        if (candidate.RequiredFields.TryGetValue(key, out var required) && !string.IsNullOrWhiteSpace(required))
        {
            return required.Trim();
        }

        return candidate.SuggestedFields.TryGetValue(key, out var suggested) && !string.IsNullOrWhiteSpace(suggested) ? suggested.Trim() : null;
    }
}
