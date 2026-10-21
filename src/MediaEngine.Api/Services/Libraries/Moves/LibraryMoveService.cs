using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Services.Libraries.Moves;

/// <summary>
/// Preview, then confirm with a re-check (Details overlay D17, Delete D6). The preview stores a token bound to the person and the
/// request, with a fingerprint of everything the answer depended on: the destination's list and who holds each entry, each item's
/// place and identity revision, and whether anything is running for it. The commit works the preview out again; any difference
/// returns the fresh preview and changes nothing. The writes then happen in one transaction that checks the same things once more.
/// </summary>
public sealed class LibraryMoveService(
    ILibraryMoveRepository moves,
    MoveListRegistry registry,
    MoveOwnedFileCleaner ownedFiles,
    MoveFilePlanner files,
    IMemoryCache cache) : ILibraryMoveService
{
    public const int MaxItems = 200;
    public const string ChangedMessage = "Something changed since you opened this. Here's the updated list.";
    private const string TokenPrefix = "library-move:";
    private const string ProcessingReason = LibraryMoveBlockedReasons.StillProcessing;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

    // Two moves never overlap: the second one plans against what the first left, so it gets a fresh preview.
    private readonly SemaphoreSlim _commitLock = new(1, 1);

    private sealed record Review(
        LibraryMoveActor Actor,
        Guid DestinationId,
        Guid? SourceCollectionId,
        IReadOnlyList<Guid> ItemIds,
        IReadOnlyList<LibraryMoveCommitRow>? Choices,
        string Fingerprint,
        DateTimeOffset ExpiresAt);

    private sealed record State(
        LibraryMoveDestinationInfo Destination,
        IMoveListProvider Provider,
        IReadOnlyList<MoveListEntry> Entries,
        IReadOnlyList<LibraryMoveItem> Items,
        IReadOnlyDictionary<Guid, string> Blocked,
        Guid? SourceCollectionId,
        IReadOnlyList<LibraryMoveCommitRow>? Choices,
        LibraryMovePreview Preview,
        string Fingerprint);

    public async Task<LibraryMoveResult<LibraryMoveDestinationsResponse>> GetDestinationsAsync(
        Guid itemId, string? query, LibraryMoveAccess canEdit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(canEdit);
        if (itemId == Guid.Empty)
        {
            return LibraryMoveResult<LibraryMoveDestinationsResponse>.Refused(LibraryMoveRefusalKind.NotFound, "This item is no longer in your library.");
        }

        var item = (await moves.GetItemsAsync([itemId], ct).ConfigureAwait(false)).FirstOrDefault();
        if (item is null)
        {
            return LibraryMoveResult<LibraryMoveDestinationsResponse>.Refused(LibraryMoveRefusalKind.NotFound, "This item is no longer in your library.", [itemId]);
        }

        if (!await canEdit(itemId, LibraryMoveRight.Write, ct).ConfigureAwait(false))
        {
            return LibraryMoveResult<LibraryMoveDestinationsResponse>.Refused(LibraryMoveRefusalKind.Unavailable, "This item is unavailable for editing.", [itemId]);
        }

        var rows = await moves.FindDestinationsAsync(item, query, 25, ct).ConfigureAwait(false);
        var destinations = new List<LibraryMoveDestination>(rows.Count);
        foreach (var row in rows)
        {
            // A place the caller may not edit is simply not offered.
            if (!await canEdit(row.Id, LibraryMoveRight.Write, ct).ConfigureAwait(false))
            {
                continue;
            }

            var listKind = registry.Resolve(new LibraryMoveDestinationInfo(row.Id, row.Kind, row.MediaType, row.Label, row.ParentLabel, null, null, null, null)).ListKind;
            destinations.Add(ToDestination(row.Id, row.Kind, row.Label, row.ParentLabel, listKind));
        }

        var current = await CurrentDestinationAsync(item, canEdit, ct).ConfigureAwait(false);
        return LibraryMoveResult<LibraryMoveDestinationsResponse>.Ok(
            new LibraryMoveDestinationsResponse { Destinations = destinations, CurrentDestination = current });
    }

    /// <summary>
    /// The place the item already sits in, when it has its own list and the caller may edit it. The picker never offers it (an item is
    /// moved elsewhere), but matching an item added anyway, or changing a number, happens in this very place.
    /// </summary>
    private async Task<LibraryMoveDestination?> CurrentDestinationAsync(LibraryMoveItem item, LibraryMoveAccess canEdit, CancellationToken ct)
    {
        var containerId = item.IsEdition ? item.WorkId : item.ParentWorkId;
        if (containerId is not { } id || id == Guid.Empty)
        {
            return null;
        }

        var info = await moves.GetDestinationAsync(id, ct).ConfigureAwait(false);
        if (info is null || info.Kind == LibraryMoveKinds.Collection || WrongKindReason(item, info) is not null)
        {
            return null;
        }

        var listKind = registry.Resolve(info).ListKind;
        if (listKind == LibraryMoveListKinds.None || !await canEdit(info.Id, LibraryMoveRight.Write, ct).ConfigureAwait(false))
        {
            return null;
        }

        return ToDestination(info.Id, info.Kind, info.Label, info.ParentLabel, listKind);
    }

    public async Task<LibraryMoveResult<LibraryMovePreview>> PreviewAsync(
        LibraryMovePreviewRequest request, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(canEdit);

        var ids = (request.ItemIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0 || ids.Count > MaxItems || request.DestinationId == Guid.Empty)
        {
            return LibraryMoveResult<LibraryMovePreview>.Refused(
                LibraryMoveRefusalKind.Invalid, $"Choose between 1 and {MaxItems.ToString(CultureInfo.InvariantCulture)} items and a place to move them to.");
        }

        // Previewing needs only the right to write. The source collection is checked like every item and the destination.
        var touched = ids.Append(request.DestinationId);
        if (request.SourceCollectionId is { } previewSource)
        {
            touched = touched.Append(previewSource);
        }

        foreach (var id in touched)
        {
            if (!await canEdit(id, LibraryMoveRight.Write, ct).ConfigureAwait(false))
            {
                return LibraryMoveResult<LibraryMovePreview>.Refused(LibraryMoveRefusalKind.Unavailable, "One of these is unavailable for editing.", [id]);
            }
        }

        var built = await BuildAsync(ids, request.DestinationId, request.SourceCollectionId, request.Choices, canEdit, lenient: false, ct).ConfigureAwait(false);
        if (built.Refusal is not null)
        {
            return LibraryMoveResult<LibraryMovePreview>.Refused(built.Refusal);
        }

        return LibraryMoveResult<LibraryMovePreview>.Ok(Issue(built.State!, actor, ids));
    }

    public async Task<LibraryMoveResult<LibraryMoveCommitResult>> CommitAsync(
        LibraryMoveCommitRequest request, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(canEdit);

        var token = request.PreviewToken ?? string.Empty;
        if (token.Length != 48 || !token.All(Uri.IsHexDigit)
            || !cache.TryGetValue<Review>(TokenPrefix + token, out var review) || review is null
            || review.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return LibraryMoveResult<LibraryMoveCommitResult>.Refused(LibraryMoveRefusalKind.Expired, "This review expired. Review the move again.");
        }

        if (review.Actor.Authority != actor.Authority || review.Actor.CredentialId != actor.CredentialId)
        {
            return LibraryMoveResult<LibraryMoveCommitResult>.Refused(LibraryMoveRefusalKind.Expired, "The editing session changed. Review the move again.");
        }

        var reviewed = review.ItemIds.Append(review.DestinationId);
        if (review.SourceCollectionId is { } reviewedSource)
        {
            reviewed = reviewed.Append(reviewedSource);
        }

        foreach (var id in reviewed)
        {
            if (!await canEdit(id, LibraryMoveRight.Write, ct).ConfigureAwait(false))
            {
                return LibraryMoveResult<LibraryMoveCommitResult>.Refused(LibraryMoveRefusalKind.Unavailable, "One of these is no longer editable.", [id]);
            }
        }

        await _commitLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Worked out again for the choices the person confirmed in the preview, so the file lines are compared like for like.
            var built = await BuildAsync(review.ItemIds, review.DestinationId, review.SourceCollectionId, review.Choices, canEdit, lenient: true, ct).ConfigureAwait(false);
            if (built.Refusal is not null)
            {
                return LibraryMoveResult<LibraryMoveCommitResult>.Refused(built.Refusal);
            }

            var state = built.State!;
            if (!string.Equals(state.Fingerprint, review.Fingerprint, StringComparison.Ordinal))
            {
                return Changed(state, actor, ChangedMessage);
            }

            // A preview made for the person's choices described the files for exactly those. Committing something else would make a
            // file the dialog never named, so it gets a fresh preview for what is being committed instead.
            if (review.Choices is not null && ChoicesDiffer(review.Choices, request.Rows ?? []))
            {
                var changedChoices = await BuildAsync(review.ItemIds, review.DestinationId, review.SourceCollectionId, request.Rows, canEdit, lenient: true, ct).ConfigureAwait(false);
                return changedChoices.Refusal is not null
                    ? LibraryMoveResult<LibraryMoveCommitResult>.Refused(changedChoices.Refusal)
                    : Changed(changedChoices.State!, actor, ChangedMessage);
            }

            var plan = BuildPlan(request, state, actor, out var invalid);
            if (plan is null)
            {
                return LibraryMoveResult<LibraryMoveCommitResult>.Refused(LibraryMoveRefusalKind.Invalid, invalid!);
            }

            // Matching an entry and adding another version change what an item is, so they also need the right to match, checked
            // for each item they apply to. Adding anyway, and moves into a place with no list, need only the right to write.
            foreach (var row in plan.Rows.Where(row => row.Mode != LibraryMoveMode.AddAnyway && state.Provider.ListKind != LibraryMoveListKinds.None))
            {
                if (!await canEdit(row.ItemId, LibraryMoveRight.Match, ct).ConfigureAwait(false))
                {
                    return LibraryMoveResult<LibraryMoveCommitResult>.Refused(
                        LibraryMoveRefusalKind.Unavailable, "You can't match this item to an entry.", [row.ItemId]);
                }
            }

            var outcome = await moves.CommitAsync(plan, state.SourceCollectionId, ct).ConfigureAwait(false);
            if (!outcome.Committed && outcome.IsRefusal)
            {
                // The request is one the Engine never allows (not a change since the preview), so there is nothing fresh to show: the
                // reason goes back as it is, and the person picks something that works.
                return LibraryMoveResult<LibraryMoveCommitResult>.Refused(
                    LibraryMoveRefusalKind.Invalid, outcome.ConflictReason ?? "That can't be done.");
            }

            if (!outcome.Committed)
            {
                // The transaction found something different from the preview; it changed nothing.
                var fresh = await BuildAsync(review.ItemIds, review.DestinationId, review.SourceCollectionId, review.Choices, canEdit, lenient: true, ct).ConfigureAwait(false);
                return fresh.Refusal is not null
                    ? LibraryMoveResult<LibraryMoveCommitResult>.Refused(fresh.Refusal)
                    : Changed(fresh.State!, actor, outcome.ConflictReason ?? ChangedMessage);
            }

            cache.Remove(TokenPrefix + token);

            // Pictures and text tracks Tuvima wrote for a title that was merged away, now that nothing points at them.
            ownedFiles.Remove(outcome.OwnedFilesToRemove);
            return LibraryMoveResult<LibraryMoveCommitResult>.Ok(new LibraryMoveCommitResult
            {
                MovedCount = outcome.Rows.Count,
                Rows = [.. outcome.Rows.Select(row => new LibraryMoveRowResult { ItemId = row.ItemId, Outcome = row.Outcome, ResultEntityId = row.ResultEntityId })],
                RemovedContainers = [.. outcome.RemovedContainers],
            });
        }
        finally
        {
            _commitLock.Release();
        }
    }

    // ----- planning ----------------------------------------------------------------------------------------------------------

    private LibraryMoveResult<LibraryMoveCommitResult> Changed(State fresh, LibraryMoveActor actor, string message) =>
        LibraryMoveResult<LibraryMoveCommitResult>.Refused(new LibraryMoveRefusal(
            LibraryMoveRefusalKind.Changed,
            message,
            [],
            Issue(fresh, actor, [.. fresh.Items.Select(item => item.ItemId)])));

    private sealed record Built(State? State, LibraryMoveRefusal? Refusal);

    private async Task<Built> BuildAsync(
        IReadOnlyList<Guid> ids,
        Guid destinationId,
        Guid? sourceCollectionId,
        IReadOnlyList<LibraryMoveCommitRow>? choices,
        LibraryMoveAccess canEdit,
        bool lenient,
        CancellationToken ct)
    {
        var found = await moves.GetItemsAsync(ids, ct).ConfigureAwait(false);
        var byId = found.ToDictionary(item => item.ItemId);
        var missing = ids.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0 && !lenient)
        {
            return Refuse(LibraryMoveRefusalKind.NotFound, "An item is no longer in your library.", missing);
        }

        // On the confirm step an item that has gone is a change, not an error: the fingerprint no longer matches.
        var items = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (items.Count == 0)
        {
            return Refuse(LibraryMoveRefusalKind.NotFound, "These items are no longer in your library.", missing);
        }

        var destination = await moves.GetDestinationAsync(destinationId, ct).ConfigureAwait(false);
        if (destination is null)
        {
            return Refuse(LibraryMoveRefusalKind.NotFound, "That place isn't available any more.", [destinationId]);
        }

        // Never into itself or anything inside it: that would make a loop.
        if (await moves.IsSameOrBeneathAsync(destination.Id, [.. items.Where(item => !item.IsEdition).Select(item => item.WorkId)], ct).ConfigureAwait(false))
        {
            return Refuse(LibraryMoveRefusalKind.Invalid, "Items can't be moved into themselves or into something inside them.", [destination.Id]);
        }

        foreach (var item in items)
        {
            var wrongKind = WrongKindReason(item, destination);
            if (wrongKind is not null)
            {
                return Refuse(LibraryMoveRefusalKind.Invalid, wrongKind, [item.ItemId]);
            }
        }

        LibraryMoveSourceFacts? sourceCollection = null;
        if (sourceCollectionId is { } collectionId)
        {
            sourceCollection = await moves.GetSourceCollectionAsync(collectionId, items, ct).ConfigureAwait(false);
            if (sourceCollection is null)
            {
                return Refuse(LibraryMoveRefusalKind.Invalid, "These items aren't in that collection.", [collectionId]);
            }
        }

        var provider = registry.Resolve(destination);
        var entries = await provider.GetEntriesAsync(destination, ct).ConfigureAwait(false);
        var places = await moves.GetSourcePlacesAsync(items, destination.Id, sourceCollectionId, ct).ConfigureAwait(false);
        var suggestions = Suggest(items, entries);

        // Why an item cannot move right now. Apply stays off while any row is blocked.
        var emptied = places.Where(place => place.BecomesEmpty).Select(place => place.ContainerId).ToHashSet();
        var blocked = new Dictionary<Guid, string>();
        foreach (var item in items)
        {
            if (item.ProcessingRunning)
            {
                blocked[item.ItemId] = ProcessingReason;
            }
            else if (item.IsEdition && item.HasUserManagedDetails && emptied.Contains(item.WorkId))
            {
                // The audiobook would be emptied and removed, and with it the details the person set on it.
                blocked[item.ItemId] = "You changed details on this audiobook, so its last file can't be moved out of it.";
            }
        }

        // What the caller may do to each item. Matching an entry needs the match right as well as the write right (Details overlay
        // spec decision); the preview says so up front so the Match step offers only what will work. A place with no list needs no match.
        var hasList = provider.ListKind != LibraryMoveListKinds.None;

        // What happens to each file (D19): renamed and moved in a Managed folder, never touched in an As-is or protected one. It is named
        // for the entry the person chose; for an item they have not chosen for, the entry the Engine suggests.
        var (picked, addedAnyway) = FileEntries(items, entries, suggestions, choices, hasList);
        var fileLines = await files.PlanAsync(items, destination, hasList, picked, addedAnyway, blocked.Keys.ToHashSet(), ct).ConfigureAwait(false);

        var rows = new List<LibraryMovePreviewRow>(items.Count);
        foreach (var item in items)
        {
            suggestions.TryGetValue(item.ItemId, out var suggestion);
            fileLines.TryGetValue(item.ItemId, out var fileLine);
            var canMatch = !hasList || await canEdit(item.ItemId, LibraryMoveRight.Match, ct).ConfigureAwait(false);
            rows.Add(new LibraryMovePreviewRow
            {
                ItemId = item.ItemId,
                Title = item.Title,
                CurrentPlacementLabel = PlacementLabel(item),
                SuggestedEntryKey = suggestion.Entry?.Key,
                SuggestionReason = suggestion.Reason ?? string.Empty,
                FileChange = fileLine?.Change,
                FileNote = fileLine?.Note,
                BlockedReason = blocked.GetValueOrDefault(item.ItemId),
                CanMatch = canMatch,
                MergeBlockedReason = hasList && CannotMergeIntoExistingTitle(item) ? LibraryMoveReasons.UserManagedMerge : null,
            });
        }

        var warnings = new List<string>();
        warnings.AddRange(items.Select(item => fileLines.GetValueOrDefault(item.ItemId)?.Warning).OfType<string>());
        var leaving = places.Where(place => place.ContainerId != destination.Id).ToList();
        warnings.AddRange(leaving.Where(place => !place.BecomesEmpty).Select(place => $"Leaves {place.Label}"));
        if (sourceCollection is { BecomesEmpty: false })
        {
            warnings.Add($"Leaves {sourceCollection.Label}");
        }

        warnings.AddRange(leaving.Where(place => place.BecomesEmpty).Select(place => $"{place.Label} will be empty and removed"));
        if (sourceCollection is { BecomesEmpty: true })
        {
            warnings.Add($"{sourceCollection.Label} will be empty and removed");
        }

        var preview = new LibraryMovePreview
        {
            Destination = ToDestination(destination.Id, destination.Kind, destination.Label, destination.ParentLabel, provider.ListKind),
            ListKind = provider.ListKind,
            Entries = [.. entries.Select(entry => new LibraryMoveEntry
            {
                EntryKey = entry.Key,
                Number = entry.Number,
                Title = entry.Title,
                LengthSeconds = entry.LengthSeconds,
                TakenByItemId = entry.TakenByItemId,
                TakenByTitle = entry.TakenByTitle,
                CanAddVersion = entry.CanAddVersion,
                JoinsExistingTitle = entry.Target.ExistingWorkId is not null,
            })],
            Rows = rows,
            Warnings = [.. warnings.Distinct(StringComparer.Ordinal)],
            AddAnywayOnly = provider.AddAnywayOnly,
            Note = provider.Note,
        };

        var fileFingerprint = string.Concat(items.Select(item => fileLines.GetValueOrDefault(item.ItemId)?.FingerprintText + "\n"));
        var state = new State(destination, provider, entries, items, blocked, sourceCollectionId, choices, preview, Fingerprint(destination, provider, entries, items, places, sourceCollection, missing.Count, fileFingerprint));
        return new Built(state, null);
    }

    private static Built Refuse(LibraryMoveRefusalKind kind, string message, IReadOnlyList<Guid> ids) =>
        new(null, new LibraryMoveRefusal(kind, message, ids, null));

    /// <summary>Stores the token for a preview and returns it with the token set.</summary>
    private LibraryMovePreview Issue(State state, LibraryMoveActor actor, IReadOnlyList<Guid> itemIds)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.Add(TokenLifetime);
        cache.Set(
            TokenPrefix + token,
            new Review(actor, state.Destination.Id, state.SourceCollectionId, itemIds, state.Choices, state.Fingerprint, expires),
            expires);
        return new LibraryMovePreview
        {
            Destination = state.Preview.Destination,
            ListKind = state.Preview.ListKind,
            Entries = state.Preview.Entries,
            Rows = state.Preview.Rows,
            Warnings = state.Preview.Warnings,
            AddAnywayOnly = state.Preview.AddAnywayOnly,
            Note = state.Preview.Note,
            PreviewToken = token,
        };
    }

    /// <summary>
    /// The entry each item's file is named for, and the items the person chose to add anyway (named without a number). A choice only
    /// describes the move: one that names no entry in the list is not refused here (the commit refuses it) and leaves the item unnamed.
    /// </summary>
    private static (Dictionary<Guid, MoveListEntry> Picked, HashSet<Guid> AddedAnyway) FileEntries(
        IReadOnlyList<LibraryMoveItem> items,
        IReadOnlyList<MoveListEntry> entries,
        Dictionary<Guid, (MoveListEntry? Entry, string? Reason)> suggestions,
        IReadOnlyList<LibraryMoveCommitRow>? choices,
        bool hasList)
    {
        var picked = new Dictionary<Guid, MoveListEntry>();
        var addedAnyway = new HashSet<Guid>();
        var chosen = new Dictionary<Guid, LibraryMoveCommitRow>();
        foreach (var choice in choices ?? [])
        {
            chosen.TryAdd(choice.ItemId, choice);
        }

        var byKey = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (chosen.TryGetValue(item.ItemId, out var choice))
            {
                if (!hasList)
                {
                    continue;
                }

                if (choice.Mode == LibraryMoveModes.AddAnyway)
                {
                    addedAnyway.Add(item.ItemId);
                }
                else if (!string.IsNullOrEmpty(choice.EntryKey) && byKey.TryGetValue(choice.EntryKey, out var entry))
                {
                    picked[item.ItemId] = entry;
                }

                continue;
            }

            if (suggestions.TryGetValue(item.ItemId, out var suggestion) && suggestion.Entry is not null)
            {
                picked[item.ItemId] = suggestion.Entry;
            }
        }

        return (picked, addedAnyway);
    }

    /// <summary>True when a row of the reviewed choices is not what the commit asks for (the same item, a different mode or entry).</summary>
    private static bool ChoicesDiffer(IReadOnlyList<LibraryMoveCommitRow> reviewed, IReadOnlyList<LibraryMoveCommitRow> committed)
    {
        var now = new Dictionary<Guid, LibraryMoveCommitRow>();
        foreach (var row in committed)
        {
            now.TryAdd(row.ItemId, row);
        }

        return reviewed.Any(choice => !now.TryGetValue(choice.ItemId, out var row)
            || !string.Equals(row.Mode, choice.Mode, StringComparison.Ordinal)
            || !string.Equals(row.EntryKey ?? string.Empty, choice.EntryKey ?? string.Empty, StringComparison.Ordinal));
    }

    /// <summary>D15: wrong kind is blocked. Returns why this item can't go into this place, or null when it can.</summary>
    private static string? WrongKindReason(LibraryMoveItem item, LibraryMoveDestinationInfo destination)
    {
        // A whole show, season, album, series or audiobook is never moved, only what is in it.
        if (item.IsContainer)
        {
            return $"\"{item.Title}\" holds other items, so it can't be moved. Move what is in it instead.";
        }

        var accepts = destination.Kind switch
        {
            LibraryMoveKinds.Album => !item.IsEdition && Is(item, "Music"),
            LibraryMoveKinds.Season => !item.IsEdition && Is(item, "TV"),
            LibraryMoveKinds.Series => !item.IsEdition && string.Equals(item.MediaType, destination.MediaType, StringComparison.OrdinalIgnoreCase),
            LibraryMoveKinds.Audiobook => item.IsEdition && Is(item, "Audiobooks"),
            LibraryMoveKinds.Collection => !item.IsEdition,
            _ => false,
        };
        if (!accepts)
        {
            return $"\"{item.Title}\" can't go into {destination.Label}.";
        }

        // An item may be matched within the place it already sits in: that is how an item added anyway is matched later, and how a
        // numbered one is renumbered. Only matching or adding a version makes sense there (checked when the choices are made).
        return null;

        static bool Is(LibraryMoveItem item, string mediaType) =>
            string.Equals(item.MediaType, mediaType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The best guess for each item, one entry per item and each entry once: by title first ("Same title"), then by length ("Closest
    /// length", within five seconds). A free entry beats a taken one that fits equally well.
    /// </summary>
    private static Dictionary<Guid, (MoveListEntry? Entry, string? Reason)> Suggest(
        IReadOnlyList<LibraryMoveItem> items, IReadOnlyList<MoveListEntry> entries)
    {
        var picks = new Dictionary<Guid, (MoveListEntry? Entry, string? Reason)>();
        if (entries.Count == 0)
        {
            return picks;
        }

        var candidates = new List<(LibraryMoveItem Item, MoveListEntry Entry, int Tier, double Score, string Reason)>();
        foreach (var item in items)
        {
            // Never guess an entry the item cannot be merged into (it would be refused), nor the entry it already is.
            foreach (var entry in entries.Where(entry => !IsOwnEntry(item, entry)
                         && !(CannotMergeIntoExistingTitle(item) && entry.Target.ExistingWorkId is not null)))
            {
                var similarity = MoveTitleMatcher.Similarity(item.Title, entry.Title);
                if (similarity >= 0.6)
                {
                    candidates.Add((item, entry, 0, similarity + (entry.IsTaken ? 0 : 0.01), similarity >= 0.85 ? "Same title" : "Similar title"));
                }
                else if (item.DurationSeconds is { } own && entry.LengthSeconds is { } known && Math.Abs(own - known) <= 5)
                {
                    candidates.Add((item, entry, 1, 1 - Math.Abs(own - known) / 10 + (entry.IsTaken ? 0 : 0.01), "Closest length"));
                }
            }
        }

        var usedEntries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates.OrderBy(c => c.Tier).ThenByDescending(c => c.Score).ThenBy(c => c.Entry.Key, StringComparer.Ordinal))
        {
            if (picks.ContainsKey(candidate.Item.ItemId) || !usedEntries.Add(candidate.Entry.Key))
            {
                continue;
            }

            picks[candidate.Item.ItemId] = (candidate.Entry, candidate.Reason);
        }

        return picks;
    }

    /// <summary>The entry the item itself already is (its own track, episode, position or part).</summary>
    private static bool IsOwnEntry(LibraryMoveItem item, MoveListEntry entry) =>
        (!item.IsEdition && entry.Target.ExistingWorkId == item.WorkId)
        || (item.IsEdition && entry.Target.TakenPartEditionId == item.EditionId);

    /// <summary>Checks the person's choices against the fresh preview and turns them into the instruction for the transaction.</summary>
    private static LibraryMovePlan? BuildPlan(LibraryMoveCommitRequest request, State state, LibraryMoveActor actor, out string? invalid)
    {
        invalid = null;
        var rows = request.Rows ?? [];
        var items = state.Items.ToDictionary(item => item.ItemId);
        if (rows.Count != items.Count || rows.Select(row => row.ItemId).Distinct().Count() != items.Count || rows.Any(row => !items.ContainsKey(row.ItemId)))
        {
            invalid = "Choose how to place every item you are moving.";
            return null;
        }

        var hasList = state.Provider.ListKind != LibraryMoveListKinds.None;
        var entries = state.Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var planned = new List<LibraryMovePlanRow>(rows.Count);
        foreach (var row in rows)
        {
            var item = items[row.ItemId];
            if (state.Blocked.TryGetValue(item.ItemId, out var blockedReason))
            {
                invalid = $"\"{item.Title}\" can't be moved right now: {blockedReason.ToLowerInvariant()}.";
                return null;
            }

            var mode = row.Mode switch
            {
                LibraryMoveModes.Match => (LibraryMoveMode?)LibraryMoveMode.Match,
                LibraryMoveModes.AddAnyway => LibraryMoveMode.AddAnyway,
                LibraryMoveModes.AddAsVersion => LibraryMoveMode.AddAsVersion,
                _ => null,
            };
            if (mode is null)
            {
                invalid = "Choose Match, Add anyway or Add as another version for each item.";
                return null;
            }

            var alreadyHere = item.IsEdition ? item.WorkId == state.Destination.Id : item.ParentWorkId == state.Destination.Id;
            if (alreadyHere && mode == LibraryMoveMode.AddAnyway)
            {
                invalid = $"\"{item.Title}\" is already in {state.Destination.Label}, so adding it anyway would change nothing. Match it to an entry instead.";
                return null;
            }

            var target = new LibraryMoveTarget();
            if (state.Provider.AddAnywayOnly)
            {
                // D20: a Series is built from each title's match. The only way in by hand is forcing a title there without a number.
                if (mode != LibraryMoveMode.AddAnyway || !string.IsNullOrEmpty(row.EntryKey))
                {
                    invalid = state.Provider.Note ?? "That can't be done here.";
                    return null;
                }
            }
            else if (!hasList)
            {
                // D14: no list, no gate. Items simply join at the end.
                if (mode != LibraryMoveMode.Match || !string.IsNullOrEmpty(row.EntryKey))
                {
                    invalid = $"{state.Destination.Label} has no list to match against, so items simply join it.";
                    return null;
                }
            }
            else if (mode != LibraryMoveMode.AddAnyway)
            {
                if (string.IsNullOrEmpty(row.EntryKey) || !entries.TryGetValue(row.EntryKey, out var entry))
                {
                    invalid = $"Choose which entry \"{item.Title}\" is in {state.Destination.Label}, or add it anyway.";
                    return null;
                }

                if (IsOwnEntry(item, entry))
                {
                    invalid = $"\"{item.Title}\" already is {DescribeEntry(state, entry)}. Pick a different entry.";
                    return null;
                }

                // Merging into an entry's own title removes this item's title, and with it the details the person set on it.
                if (CannotMergeIntoExistingTitle(item) && entry.Target.ExistingWorkId is not null)
                {
                    invalid = LibraryMoveReasons.UserManagedMerge;
                    return null;
                }

                if (mode == LibraryMoveMode.Match && entry.IsTaken)
                {
                    invalid = $"{DescribeEntry(state, entry)} already has a file. Add \"{item.Title}\" as another version, or pick another entry.";
                    return null;
                }

                if (mode == LibraryMoveMode.AddAsVersion && !entry.IsTaken)
                {
                    invalid = $"{DescribeEntry(state, entry)} has no file yet, so match \"{item.Title}\" to it instead.";
                    return null;
                }

                if (mode == LibraryMoveMode.AddAsVersion && !entry.CanAddVersion)
                {
                    invalid = $"{DescribeEntry(state, entry)} can't take another version here. Pick another entry, or add \"{item.Title}\" anyway.";
                    return null;
                }

                if (!usedKeys.Add(entry.Key))
                {
                    invalid = $"Two items can't take {DescribeEntry(state, entry)}.";
                    return null;
                }

                target = entry.Target;
            }
            else if (!string.IsNullOrEmpty(row.EntryKey))
            {
                invalid = "An item added anyway takes no entry.";
                return null;
            }

            planned.Add(new LibraryMovePlanRow(item.ItemId, mode.Value, target, item.ParentWorkId, item.IdentityRevision));
        }

        return new LibraryMovePlan(state.Destination.Id, state.Destination.Kind, planned, actor.DisplayName);
    }

    /// <summary>A title whose details the person set cannot be merged into an entry's own title (the transaction refuses it too).</summary>
    private static bool CannotMergeIntoExistingTitle(LibraryMoveItem item) => !item.IsEdition && item.HasUserManagedDetails;

    private static string DescribeEntry(State state, MoveListEntry entry) =>
        state.Provider.ListKind switch
        {
            LibraryMoveListKinds.Tracks => $"Track {entry.Number}",
            LibraryMoveListKinds.Parts => $"Part {entry.Number}",
            _ => entry.Number,
        };

    private static string PlacementLabel(LibraryMoveItem item)
    {
        if (item.IsEdition)
        {
            var part = item.PartNumber is { } number ? $"part {number.ToString(CultureInfo.InvariantCulture)}" : "no part number";
            return $"{item.WorkTitle}, {part}";
        }

        if (string.IsNullOrWhiteSpace(item.ParentTitle) && item.ParentWorkId is null)
        {
            return "On its own";
        }

        var position = item.IsUnmatched || (item.OrdinalSort ?? item.Ordinal) is null
            ? "not numbered"
            : null;
        if (string.Equals(item.MediaType, "TV", StringComparison.OrdinalIgnoreCase))
        {
            var season = item.ParentOrdinal is null or 0 ? "Specials" : $"season {item.ParentOrdinal.Value.ToString(CultureInfo.InvariantCulture)}";
            var episode = position ?? $"episode {Format(item.OrdinalSort ?? item.Ordinal)}";
            return $"{item.GrandparentTitle ?? item.ParentTitle}, {season}, {episode}";
        }

        var numberText = position ?? (string.Equals(item.MediaType, "Music", StringComparison.OrdinalIgnoreCase)
            ? $"track {Format(item.Ordinal)}"
            : $"#{Format(item.OrdinalSort ?? item.Ordinal)}");
        return $"{item.ParentTitle}, {numberText}";
    }

    private static string Format(double? value) => value is null ? "?" : value.Value.ToString("0.####", CultureInfo.InvariantCulture);

    private static LibraryMoveDestination ToDestination(Guid id, string kind, string label, string? parentLabel, string listKind)
    {
        var hasList = listKind != LibraryMoveListKinds.None;
        return new LibraryMoveDestination { Id = id, Kind = kind, Label = label, ParentLabel = parentLabel, HasList = hasList };
    }

    private static string Fingerprint(
        LibraryMoveDestinationInfo destination,
        IMoveListProvider provider,
        IReadOnlyList<MoveListEntry> entries,
        IReadOnlyList<LibraryMoveItem> items,
        IReadOnlyList<LibraryMoveSourceFacts> places,
        LibraryMoveSourceFacts? sourceCollection,
        int missingItems,
        string fileLines)
    {
        var text = new StringBuilder();
        text.Append(fileLines);
        text.Append("dest|").Append(destination.Id.ToString("N")).Append('|').Append(destination.Kind).Append('|').Append(provider.ListKind).AppendLine();
        text.Append("missing|").Append(missingItems.ToString(CultureInfo.InvariantCulture)).AppendLine();
        foreach (var entry in entries)
        {
            text.Append("entry|").Append(entry.Key).Append('|').Append(entry.Number).Append('|').Append(entry.Title).Append('|')
                .Append(entry.IsTaken ? "1" : "0").Append('|').Append(entry.CanAddVersion ? "1" : "0").Append('|')
                .Append(entry.TakenByItemId?.ToString("N")).AppendLine();
        }

        foreach (var item in items)
        {
            text.Append("item|").Append(item.ItemId.ToString("N")).Append('|').Append(item.WorkId.ToString("N")).Append('|')
                .Append(item.ParentWorkId?.ToString("N")).Append('|').Append(item.IdentityRevision).Append('|')
                .Append(item.ProcessingRunning ? "1" : "0").Append('|').Append(item.IsUnmatched ? "1" : "0").Append('|')
                .Append(Format(item.OrdinalSort ?? item.Ordinal)).Append('|')
                .Append(string.Join(',', item.AssetIds.Select(asset => asset.ToString("N")))).AppendLine();
        }

        foreach (var place in places.OrderBy(place => place.ContainerId))
        {
            text.Append("place|").Append(place.ContainerId.ToString("N")).Append('|').Append(place.BecomesEmpty ? "1" : "0").AppendLine();
        }

        if (sourceCollection is not null)
        {
            text.Append("source|").Append(sourceCollection.ContainerId.ToString("N")).Append('|').Append(sourceCollection.BecomesEmpty ? "1" : "0").AppendLine();
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
