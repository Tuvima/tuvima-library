using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// The one Save of the photos, videos and documents editor (spec photos-documents-editor, WP1, D8). Everything is checked before anything
/// is written; if any item is refused nothing changes. The writes themselves go through the same repositories as the single-field
/// actions, so who may change what is decided in exactly one place. Each item's earlier values are read first, and if a step fails
/// part-way the items already changed are put back, so a failed Save never leaves a group half-edited.
/// </summary>
public sealed class ViewItemsEditService(
    ILocalAssetRepository assets,
    IViewCatalogueRepository catalogue,
    IViewGalleryRepository galleries,
    ILogger<ViewItemsEditService> logger)
{
    public const string GroupIdentifyingReason = "Title, archive number, notes and description are for one item at a time.";

    /// <summary>The person and place the Save is made for, resolved by the endpoint.</summary>
    public sealed record Context(
        ViewCatalogueViewer Viewer,
        Guid ProfileId,
        Guid? PersonalSpaceId,
        IReadOnlyCollection<string> BlockedKinds,
        Func<IReadOnlyCollection<string>, Task<string?>> FirstUnknownPerson,
        Func<Guid, Task<bool>> CanContributeToGallery);

    public async Task<ViewItemsEditResultDto> RunAsync(Context context, ViewItemsEditSetRequest set, IReadOnlyCollection<ViewItemsEditRefusalDto> accessRefusals, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Edits is null || set.Edits.Count == 0)
        {
            throw new ArgumentException("There is nothing to save.");
        }

        var refusals = new List<ViewItemsEditRefusalDto>(accessRefusals);
        var planned = new List<(ViewItemsEditRequest Request, Dictionary<Guid, LocalAssetDto> Snapshots)>();
        var dayMoves = 0;
        var seen = new HashSet<Guid>();
        foreach (var request in set.Edits)
        {
            ArgumentNullException.ThrowIfNull(request);
            var ids = ValidateShape(request);
            var snapshots = new Dictionary<Guid, LocalAssetDto>();
            foreach (var id in ids)
            {
                if (!seen.Add(id))
                {
                    throw new ArgumentException("An item can only be in one edit of a Save.");
                }

                if (assets.Find(id, ct) is { } found)
                {
                    snapshots[id] = found;
                }
                else
                {
                    refusals.Add(new ViewItemsEditRefusalDto(id, "This item is not available."));
                }
            }

            var group = ids.Count > 1;
            foreach (var (id, item) in snapshots)
            {
                var isDocument = string.Equals(item.MediaKind, LocalAssetMediaKinds.Document, StringComparison.Ordinal);
                if (Reason(item, isDocument, group, request) is { } reason)
                {
                    refusals.Add(new ViewItemsEditRefusalDto(id, reason));
                    continue;
                }

                if (NewCapturedAt(item, request) is { } moved && item.CapturedAt is { } was && DayOf(was) != DayOf(moved))
                {
                    dayMoves++;
                }
            }

            planned.Add((request, snapshots));
        }

        if (refusals.Count == 0)
        {
            foreach (var request in set.Edits)
            {
                if (request.PeopleAdd is { Count: > 0 } added && await context.FirstUnknownPerson(added) is { } unknown)
                {
                    throw new ArgumentException($"'{unknown}' is not an identified person in this View library.");
                }

                foreach (var galleryId in (request.GalleryAddIds ?? []).Concat(request.GalleryRemoveIds ?? []).Where(id => id != Guid.Empty).Distinct())
                {
                    if (!await context.CanContributeToGallery(galleryId)
                        || await galleries.GetAsync(galleryId, true, ct, context.BlockedKinds).ConfigureAwait(false) is not { Kind: ViewGalleryKind.Manual })
                    {
                        throw new ArgumentException("One of the galleries is not available.");
                    }
                }

                if (!string.IsNullOrWhiteSpace(request.CreateGalleryName) && context.PersonalSpaceId is null)
                {
                    throw new ArgumentException("A new gallery can't be made here.");
                }
            }
        }

        if (refusals.Count > 0)
        {
            return new ViewItemsEditResultDto(false, false, 0, refusals, "Nothing was saved: some items can't be changed this way.");
        }

        var total = planned.Sum(entry => entry.Snapshots.Count);
        if (set.PreviewOnly)
        {
            return new ViewItemsEditResultDto(false, true, total, [], DayMoves: dayMoves);
        }

        var applied = new List<(LocalAssetDto Item, ViewItemsEditRequest Request)>();
        var createdGalleries = new List<Guid>();
        var membership = new List<(Guid GalleryId, List<Guid> Added, List<Guid> Removed)>();
        Guid? firstCreated = null;
        try
        {
            foreach (var (request, snapshots) in planned)
            {
                foreach (var (_, item) in snapshots)
                {
                    ct.ThrowIfCancellationRequested();
                    applied.Add((item, request));
                    await ApplyAsync(context, item, request, ct).ConfigureAwait(false);
                }

                var touched = snapshots.Keys.ToList();
                if (!string.IsNullOrWhiteSpace(request.CreateGalleryName))
                {
                    var gallery = await galleries.CreateAsync(new CreateViewGalleryCommand(
                        context.ProfileId, context.PersonalSpaceId!.Value, request.CreateGalleryName.Trim(), ViewGalleryKind.Manual), ct).ConfigureAwait(false);
                    createdGalleries.Add(gallery.Id);
                    firstCreated ??= gallery.Id;
                    await galleries.AddItemsAsync(gallery.Id, touched, context.BlockedKinds, ct).ConfigureAwait(false);
                }

                foreach (var galleryId in (request.GalleryAddIds ?? []).Where(id => id != Guid.Empty).Distinct())
                {
                    var before = await MembersAsync(galleryId, touched, ct).ConfigureAwait(false);
                    membership.Add((galleryId, touched.Where(id => !before.Contains(id)).ToList(), new List<Guid>()));
                    await galleries.AddItemsAsync(galleryId, touched, context.BlockedKinds, ct).ConfigureAwait(false);
                }

                foreach (var galleryId in (request.GalleryRemoveIds ?? []).Where(id => id != Guid.Empty).Distinct())
                {
                    var before = await MembersAsync(galleryId, touched, ct).ConfigureAwait(false);
                    membership.Add((galleryId, new List<Guid>(), touched.Where(before.Contains).ToList()));
                    await galleries.RemoveItemsAsync(galleryId, touched, true, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "A View save failed part-way; putting {Count} item(s) back.", applied.Count);
            // Putting things back must finish even when the person closed the page, so it never uses the request's token.
            var notPutBack = await PutBackAsync(context, applied, createdGalleries, membership).ConfigureAwait(false);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            if (notPutBack > 0)
            {
                logger.LogError("A View save could not put {Count} step(s) back; the items need a look.", notPutBack);
                return new ViewItemsEditResultDto(false, false, 0, [], "Nothing was saved, but some changes could not be put back. Check the items you were editing.");
            }

            return exception is ArgumentException or InvalidOperationException or ViewCatalogueAccessException or ViewCatalogueConflictException
                ? new ViewItemsEditResultDto(false, false, 0, [], $"Nothing was saved. {exception.Message}")
                : new ViewItemsEditResultDto(false, false, 0, [], "Nothing was saved: something went wrong, and everything was put back as it was.");
        }

        return new ViewItemsEditResultDto(true, true, total, [], CreatedGalleryId: firstCreated, DayMoves: dayMoves);
    }

    private static List<Guid> ValidateShape(ViewItemsEditRequest request)
    {
        if (request.ItemIds is null || request.ItemIds.Count == 0)
        {
            throw new ArgumentException("Choose at least one item.");
        }

        if (request.ItemIds.Count > ViewItemsEditLimits.MaximumItems)
        {
            throw new ArgumentException($"One Save can change at most {ViewItemsEditLimits.MaximumItems} items.");
        }

        if (request.ItemIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("Every item needs an id.");
        }

        const int MaximumNames = 50;
        if ((request.TagsAdd?.Count ?? 0) > MaximumNames || (request.TagsRemove?.Count ?? 0) > MaximumNames
            || (request.PeopleAdd?.Count ?? 0) > MaximumNames || (request.PeopleRemove?.Count ?? 0) > MaximumNames
            || (request.Document?.FieldValues?.Count ?? 0) > MaximumNames
            || (request.GalleryAddIds?.Count ?? 0) > MaximumNames || (request.GalleryRemoveIds?.Count ?? 0) > MaximumNames)
        {
            throw new ArgumentException($"One edit can name at most {MaximumNames} tags, people, fields or galleries.");
        }

        if (request.CapturedAt is not null && request.CapturedShiftSeconds is not null)
        {
            throw new ArgumentException("Set one date or shift the dates, not both.");
        }

        if (request.Location is { ResetToEmbedded: false } place && place.Latitude.HasValue != place.Longitude.HasValue)
        {
            throw new ArgumentException("Latitude and longitude must be given together.");
        }

        return request.ItemIds.Distinct().ToList();
    }

    // Why one item cannot take this Save, or null. Photos and videos take dates, places and people; documents take the document fields.
    private static string? Reason(LocalAssetDto item, bool isDocument, bool group, ViewItemsEditRequest request)
    {
        var photoFields = request.CapturedAt is not null || request.CapturedShiftSeconds is not null || request.CapturedReset
            || request.Location is not null || request.PeopleAdd is { Count: > 0 } || request.PeopleRemove is { Count: > 0 };
        if (isDocument && photoFields)
        {
            return "Documents don't have a date taken, a place or people.";
        }

        if (!isDocument && request.Document is not null)
        {
            return "Only documents have these fields.";
        }

        if (group && (request.SetDescription || request.Document is { SetTitle: true } or { SetArchiveNumber: true } or { SetNotes: true }))
        {
            return GroupIdentifyingReason;
        }

        if (request.CapturedShiftSeconds is not null && item.CapturedAt is null)
        {
            return "This item has no date taken to shift.";
        }

        return null;
    }

    private static DateTimeOffset? NewCapturedAt(LocalAssetDto item, ViewItemsEditRequest request) =>
        request.CapturedShiftSeconds is { } shift && item.CapturedAt is { } at ? at.AddSeconds(shift) : request.CapturedAt;

    private static DateOnly DayOf(DateTimeOffset value) => DateOnly.FromDateTime(value.DateTime);

    private async Task ApplyAsync(Context context, LocalAssetDto item, ViewItemsEditRequest request, CancellationToken ct)
    {
        var id = item.Id;
        if (request.SetDescription && !await assets.UpdateDescriptionAsync(id, request.Description, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("An item is no longer available.");
        }

        if (request.CapturedReset)
        {
            await assets.UpdateCapturedAtAsync(id, null, true, ct).ConfigureAwait(false);
        }
        else if (NewCapturedAt(item, request) is { } captured)
        {
            await assets.UpdateCapturedAtAsync(id, captured, false, ct).ConfigureAwait(false);
        }

        if (request.Location is { } place)
        {
            await assets.UpdateLocationAsync(id, new LocalAssetLocationUpdate(place.Latitude, place.Longitude, place.Name, place.City,
                place.Region, place.Country, place.CountryCode, place.ResetToEmbedded), ct).ConfigureAwait(false);
        }

        if (request.PeopleAdd is { Count: > 0 } || request.PeopleRemove is { Count: > 0 })
        {
            await assets.ReplacePeopleAsync(id, Merge(item.People, request.PeopleAdd, request.PeopleRemove), ct).ConfigureAwait(false);
        }

        if (request.Document is { } document && ChangesDocument(document))
        {
            await catalogue.UpdateDocumentDetailsAsync(context.Viewer, id, DocumentWrite(item, document), ct).ConfigureAwait(false);
        }

        if (request.TagsAdd is { Count: > 0 } || request.TagsRemove is { Count: > 0 })
        {
            await catalogue.ReplaceItemTagsAsync(context.Viewer, id, Merge(item.Tags, request.TagsAdd, request.TagsRemove), [], ct).ConfigureAwait(false);
        }
    }

    private static bool ChangesDocument(ViewItemsDocumentEdit edit) =>
        edit.SetTitle || edit.SetCorrespondent || edit.SetDocumentType || edit.SetDocumentDate || edit.SetArchiveNumber || edit.SetNotes
        || edit.FieldValues is { Count: > 0 };

    private static List<string> Merge(IReadOnlyList<string>? current, IReadOnlyList<string>? add, IReadOnlyList<string>? remove)
    {
        var result = (current ?? []).ToList();
        var present = result.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in (add ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()))
        {
            if (present.Add(name))
            {
                result.Add(name);
            }
        }

        var gone = (remove ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.RemoveAll(gone.Contains);
        return result;
    }

    // A document's details are written whole ("anything left empty is cleared"), so the write starts from what the document says now.
    private static ViewDocumentDetailsWrite DocumentWrite(LocalAssetDto item, ViewItemsDocumentEdit edit)
    {
        var info = item.DocumentInfo;
        var fields = info?.FieldValues?.Select(value => new ViewDocumentFieldWrite(
            value.FieldId, value.TextValue, value.NumberValue, value.DateValue, value.BoolValue)).ToList();
        if (edit.FieldValues is { Count: > 0 } changed)
        {
            fields ??= [];
            foreach (var value in changed)
            {
                fields.RemoveAll(existing => existing.FieldId == value.FieldId);
                fields.Add(new ViewDocumentFieldWrite(value.FieldId, value.TextValue, value.NumberValue, value.DateValue, value.BoolValue));
            }
        }

        return new ViewDocumentDetailsWrite(
            edit.SetTitle ? edit.Title : item.Title,
            edit.SetDocumentDate ? edit.DocumentDate : info?.DocumentDate,
            edit.SetCorrespondent ? edit.Correspondent : info?.Correspondent?.Name,
            edit.SetDocumentType ? edit.DocumentType : info?.DocumentType?.Name,
            edit.SetArchiveNumber ? edit.ArchiveNumber : info?.ArchiveNumber,
            edit.SetNotes ? edit.Notes : info?.Notes,
            info?.FilingState,
            (info?.Tags ?? []).Select(tag => tag.Name).ToList(),
            [],
            fields);
    }

    // Puts every touched item back to what it said before, in reverse; a gallery made by this Save is removed and its additions undone.
    private async Task<int> PutBackAsync(Context context, IReadOnlyList<(LocalAssetDto Item, ViewItemsEditRequest Request)> touchedItems, IReadOnlyList<Guid> createdGalleries,
        IReadOnlyList<(Guid GalleryId, List<Guid> Added, List<Guid> Removed)> membership)
    {
        var ct = CancellationToken.None;
        var failed = 0;
        foreach (var (galleryId, added, removed) in membership.Reverse())
        {
            var gallery = galleryId;
            if (added.Count > 0 && !await Try(() => galleries.RemoveItemsAsync(gallery, added, true, ct)).ConfigureAwait(false))
            {
                failed++;
            }

            if (removed.Count > 0 && !await Try(() => galleries.AddItemsAsync(gallery, removed, context.BlockedKinds, ct)).ConfigureAwait(false))
            {
                failed++;
            }
        }

        foreach (var made in createdGalleries)
        {
            var gallery = made;
            if (!await Try(() => galleries.DeleteAsync(gallery, ct)).ConfigureAwait(false))
            {
                failed++;
            }
        }

        // Only what the Save itself changed is put back, so a failed Save never touches anything else about an item.
        foreach (var (item, request) in touchedItems.Reverse())
        {
            var id = item.Id;
            if (request.SetDescription && !await Try(() => assets.UpdateDescriptionAsync(id, item.Description, ct)).ConfigureAwait(false))
            {
                failed++;
            }

            if (request.CapturedAt is not null || request.CapturedShiftSeconds is not null || request.CapturedReset)
            {
                var restored = item.CapturedAtUserOverride && item.CapturedAt is { } was
                    ? await Try(() => assets.UpdateCapturedAtAsync(id, was, false, ct)).ConfigureAwait(false)
                    : await Try(() => assets.UpdateCapturedAtAsync(id, null, true, ct)).ConfigureAwait(false);
                failed += restored ? 0 : 1;
            }

            if (request.Location is not null)
            {
                var restored = item.LocationUserOverride
                    ? await Try(() => assets.UpdateLocationAsync(id, new LocalAssetLocationUpdate(item.Latitude, item.Longitude, item.LocationName,
                        item.LocationCity, item.LocationRegion, item.LocationCountry, item.LocationCountryCode, false), ct)).ConfigureAwait(false)
                    : await Try(() => assets.UpdateLocationAsync(id, new LocalAssetLocationUpdate(null, null, null, null, null, null, null, true), ct)).ConfigureAwait(false);
                failed += restored ? 0 : 1;
            }

            if ((request.PeopleAdd is { Count: > 0 } || request.PeopleRemove is { Count: > 0 })
                && !await Try(() => assets.ReplacePeopleAsync(id, item.People ?? [], ct)).ConfigureAwait(false))
            {
                failed++;
            }

            if ((request.TagsAdd is { Count: > 0 } || request.TagsRemove is { Count: > 0 })
                && !await Try(() => catalogue.ReplaceItemTagsAsync(context.Viewer, id, item.Tags, [], ct)).ConfigureAwait(false))
            {
                failed++;
            }

            if (request.Document is { } document && ChangesDocument(document) && item.DocumentInfo is not null
                && !await Try(() => catalogue.UpdateDocumentDetailsAsync(context.Viewer, id, DocumentWrite(item, new ViewItemsDocumentEdit()), ct)).ConfigureAwait(false))
            {
                failed++;
            }
        }

        return failed;
    }

    // Which of the given items a gallery holds now (so a failed Save can put exactly that back).
    private async Task<HashSet<Guid>> MembersAsync(Guid galleryId, IReadOnlyCollection<Guid> candidates, CancellationToken ct)
    {
        var wanted = candidates.ToHashSet();
        var found = new HashSet<Guid>();
        int? afterPosition = null;
        Guid? afterItem = null;
        while (true)
        {
            var page = await galleries.GetItemsAsync(galleryId, afterPosition, afterItem, 500, true, ct).ConfigureAwait(false);
            foreach (var item in page.Items.Where(item => wanted.Contains(item.ItemId)))
            {
                found.Add(item.ItemId);
            }

            if (!page.HasMore || page.Items.Count == 0)
            {
                return found;
            }

            afterPosition = page.NextPosition;
            afterItem = page.NextItemId;
        }
    }

    private async Task<bool> Try(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Putting an item back after a failed View save did not finish.");
            return false;
        }
    }
}
