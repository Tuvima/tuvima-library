using System.Text.Json;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Collections.ListImport;

/// <summary>
/// Imported lists (Collections and lists spec D5, D6, 4b and 4d): reads a list file, matches its entries to the titles Library
/// owns, previews "You own X of Y", makes a collection hold the matched titles, and keeps it in step on refresh. Files are read
/// from the bytes the Dashboard sent; this service never opens a network connection.
/// </summary>
public sealed class ListImportService
{
    private const int PreviewCoverCount = 12;
    private const int MaximumNameLength = 100;

    private readonly ICollectionListRepository _lists;
    private readonly IListImportCandidateRepository _candidates;
    private readonly IFuzzyMatchingService _fuzzy;
    private readonly IConfigurationLoader _configuration;
    private readonly ICollectionMediaLookupReadService _lookup;
    private readonly TimeProvider _time;

    public ListImportService(
        ICollectionListRepository lists,
        IListImportCandidateRepository candidates,
        IFuzzyMatchingService fuzzy,
        IConfigurationLoader configuration,
        ICollectionMediaLookupReadService lookup,
        TimeProvider? time = null)
    {
        _lists = lists;
        _candidates = candidates;
        _fuzzy = fuzzy;
        _configuration = configuration;
        _lookup = lookup;
        _time = time ?? TimeProvider.System;
    }

    // ── Preview ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the list and says how much of it Library owns. Nothing is saved.</summary>
    /// <exception cref="ListFileNotUnderstoodException">The file is not a list.</exception>
    public async Task<ListImportPreviewDto> PreviewAsync(
        string fileName,
        ParsedList parsed,
        string? shelf,
        IReadOnlyList<ListEntryOverrideDto> overrides,
        IReadOnlySet<Guid>? allowedWorkIds,
        CancellationToken ct)
    {
        var candidates = await _candidates.GetCandidatesAsync(allowedWorkIds, ct).ConfigureAwait(false);
        var resolved = Resolve(parsed.Entries, candidates, overrides);

        var shelves = parsed.Shelves
            .Select(item => new ListImportShelfDto(
                item.Name,
                item.Count,
                resolved.Count(entry => entry.IsOwned && entry.Entry.Shelves.Contains(item.Name, StringComparer.OrdinalIgnoreCase))))
            .ToList();

        var chosen = string.IsNullOrWhiteSpace(shelf)
            ? resolved
            : resolved.Where(entry => entry.Entry.Shelves.Contains(shelf, StringComparer.OrdinalIgnoreCase)).ToList();
        var counted = chosen.Where(entry => entry.Status != ListImportChoices.StatusIgnored).ToList();
        var owned = counted.Where(entry => entry.IsOwned).ToList();
        var byWork = candidates.ToDictionary(candidate => candidate.WorkId);

        var ownedWorkIds = owned.Select(entry => entry.WorkId!.Value).Distinct().ToList();
        var covers = await _lookup.ResolveMetadataAsync(ownedWorkIds.Take(PreviewCoverCount).ToList(), ct).ConfigureAwait(false);

        var reviewable = chosen
            .Where(entry => entry.Status != ListImportChoices.StatusMatched)
            .Select(entry => ToEntryDto(null, entry, byWork))
            .ToList();

        return new ListImportPreviewDto
        {
            SourceKind = parsed.SourceKind,
            SourceLabel = parsed.SourceLabel,
            SourceName = SafeFileName(fileName),
            SuggestedName = SuggestName(parsed, shelf),
            EntryCount = counted.Count,
            OwnedCount = owned.Count,
            UnmatchedCount = counted.Count(entry => !entry.IsOwned),
            IgnoredCount = chosen.Count - counted.Count,
            Shelves = shelves,
            Entries = reviewable,
            MatchedWorkIds = ownedWorkIds,
            Owned = covers.Select(item => new ListImportOwnedItemDto(item.EntityId, item.Title, item.MediaType, item.CoverUrl)).ToList(),
            MediaTypes = ownedWorkIds
                .Select(id => byWork.TryGetValue(id, out var candidate) ? candidate.MediaType : null)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Select(type => type!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    // ── Import ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the list as the source of a collection that was just created and makes the collection hold the matched titles.
    /// Titles the collection already holds that the list also names are taken over as the list's; anything else already in
    /// the collection stays as the person's own.
    /// </summary>
    public async Task<ListImportResultDto> ImportAsync(
        Guid collectionId,
        string fileName,
        ParsedList parsed,
        string? shelf,
        string order,
        string refresh,
        string syncMode,
        IReadOnlyList<ListEntryOverrideDto> overrides,
        IReadOnlySet<Guid>? allowedWorkIds,
        CancellationToken ct)
    {
        var candidates = await _candidates.GetCandidatesAsync(allowedWorkIds, ct).ConfigureAwait(false);
        var entries = string.IsNullOrWhiteSpace(shelf)
            ? parsed.Entries
            : parsed.Entries.Where(entry => entry.Shelves.Contains(shelf, StringComparer.OrdinalIgnoreCase)).ToList();
        var resolved = Resolve(entries, candidates, overrides);
        var now = _time.GetUtcNow();
        var orderBy = NormalizeOrder(order);
        var refreshChoice = NormalizeRefresh(refresh);
        var sync = NormalizeSync(syncMode);

        var stored = resolved.Select((entry, index) => ToStoredEntry(collectionId, index + 1, entry, onList: true, existingId: null)).ToList();
        var existingItems = await _lists.GetItemsAsync(collectionId, ct).ConfigureAwait(false);
        var items = BuildItems(existingItems, stored, candidates, orderBy, adoptExisting: true);

        var source = new CollectionListSource
        {
            CollectionId = collectionId,
            SourceKind = parsed.SourceKind,
            SourceLabel = parsed.SourceLabel,
            SourceName = SafeFileName(fileName),
            Shelf = string.IsNullOrWhiteSpace(shelf) ? null : shelf.Trim(),
            OrderBy = orderBy,
            Refresh = refreshChoice,
            SyncMode = sync,
            LastRefreshedAt = now,
            NextRefreshAt = NextRefresh(refreshChoice, now),
            CreatedAt = now,
            UpdatedAt = now,
        };
        ApplyCounts(source, stored);
        await _lists.SaveAsync(source, stored, items, ct).ConfigureAwait(false);
        return new ListImportResultDto(collectionId, source.EntryCount, source.MatchedCount, source.UnmatchedCount);
    }

    // ── Read the source ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<CollectionListSourceDto?> GetSourceAsync(Guid collectionId, CancellationToken ct)
    {
        var source = await _lists.GetSourceAsync(collectionId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var entries = await _lists.GetEntriesAsync(collectionId, ct).ConfigureAwait(false);
        var unmatched = entries
            .Where(entry => entry.OnList && entry.Status is ListImportChoices.StatusUnmatched or ListImportChoices.StatusIgnored)
            .Select(entry => ToEntryDto(entry))
            .ToList();
        return new CollectionListSourceDto
        {
            CollectionId = collectionId,
            SourceKind = source.SourceKind,
            SourceLabel = source.SourceLabel,
            SourceName = source.SourceName,
            Shelf = source.Shelf,
            Order = source.OrderBy,
            Refresh = source.Refresh,
            SyncMode = source.SyncMode,
            LastRefreshedAt = source.LastRefreshedAt,
            EntryCount = source.EntryCount,
            OwnedCount = source.MatchedCount,
            UnmatchedCount = source.UnmatchedCount,
            Unmatched = unmatched,
        };
    }

    // ── Hand-fix ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Picks the right title for an entry, ignores it, or puts it back to automatic matching. Returns false when the
    /// collection or entry is not found, or the picked title is not one Library owns.
    /// </summary>
    public async Task<bool> FixEntryAsync(
        Guid collectionId, Guid entryId, string action, Guid? workId, IReadOnlySet<Guid>? allowedWorkIds, CancellationToken ct)
    {
        var source = await _lists.GetSourceAsync(collectionId, ct).ConfigureAwait(false);
        var entries = (await _lists.GetEntriesAsync(collectionId, ct).ConfigureAwait(false)).ToList();
        var entry = entries.FirstOrDefault(item => item.Id == entryId);
        if (source is null || entry is null)
        {
            return false;
        }

        var candidates = await _candidates.GetCandidatesAsync(allowedWorkIds, ct).ConfigureAwait(false);
        switch (action.Trim().ToLowerInvariant())
        {
            case "pick":
                if (workId is not { } picked || candidates.All(candidate => candidate.WorkId != picked))
                {
                    return false;
                }

                entry.Status = ListImportChoices.StatusHandFixed;
                entry.MatchedWorkId = picked;
                entry.MatchBasis = "hand";
                break;
            case "ignore":
                entry.Status = ListImportChoices.StatusIgnored;
                entry.MatchedWorkId = null;
                entry.MatchBasis = null;
                break;
            case "restore":
                var match = BuildMatcher(candidates).Match(ToInput(entry));
                entry.Status = match is null ? ListImportChoices.StatusUnmatched : ListImportChoices.StatusMatched;
                entry.MatchedWorkId = match?.WorkId;
                entry.MatchBasis = match?.Basis;
                break;
            default:
                return false;
        }

        await SaveAfterChangeAsync(source, entries, candidates, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Changes the order, refresh and sync choices of a list collection.</summary>
    public async Task<bool> UpdateSettingsAsync(
        Guid collectionId, string order, string refresh, string syncMode, IReadOnlySet<Guid>? allowedWorkIds, CancellationToken ct)
    {
        var source = await _lists.GetSourceAsync(collectionId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return false;
        }

        var orderBy = NormalizeOrder(order);
        var refreshChoice = NormalizeRefresh(refresh);
        var now = _time.GetUtcNow();
        var orderChanged = !string.Equals(orderBy, source.OrderBy, StringComparison.Ordinal);
        await _lists.UpdateSettingsAsync(
            collectionId, orderBy, refreshChoice, NormalizeSync(syncMode),
            refreshChoice == source.Refresh ? source.NextRefreshAt : NextRefresh(refreshChoice, now), ct).ConfigureAwait(false);

        if (orderChanged)
        {
            var entries = (await _lists.GetEntriesAsync(collectionId, ct).ConfigureAwait(false)).ToList();
            var candidates = await _candidates.GetCandidatesAsync(allowedWorkIds, ct).ConfigureAwait(false);
            source = (await _lists.GetSourceAsync(collectionId, ct).ConfigureAwait(false))!;
            await SaveAfterChangeAsync(source, entries, candidates, ct).ConfigureAwait(false);
        }

        return true;
    }

    // ── Refresh ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Brings a list collection up to date. With <paramref name="fresh"/> (a newer copy of the list) entries that left the list
    /// leave the collection under "Follow the list" and stay under "Keep everything ever added", and new entries are added;
    /// without it the stored entries are matched again against the library. A title a person added by hand is never touched.
    /// Returns null when the collection has no list source.
    /// </summary>
    public async Task<ListRefreshResultDto?> RefreshAsync(
        Guid collectionId, ParsedList? fresh, IReadOnlySet<Guid>? allowedWorkIds, CancellationToken ct)
    {
        var source = await _lists.GetSourceAsync(collectionId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var existing = (await _lists.GetEntriesAsync(collectionId, ct).ConfigureAwait(false)).ToList();
        var candidates = await _candidates.GetCandidatesAsync(allowedWorkIds, ct).ConfigureAwait(false);
        var matcher = BuildMatcher(candidates);
        var added = 0;
        var removed = 0;
        List<CollectionListEntry> next;

        if (fresh is null)
        {
            next = existing;
            foreach (var entry in next.Where(entry => entry.Status is ListImportChoices.StatusMatched or ListImportChoices.StatusUnmatched))
            {
                Rematch(entry, matcher);
            }
        }
        else
        {
            var incoming = string.IsNullOrWhiteSpace(source.Shelf)
                ? fresh.Entries
                : fresh.Entries.Where(entry => entry.Shelves.Contains(source.Shelf, StringComparer.OrdinalIgnoreCase)).ToList();
            var pool = existing.Where(entry => entry.OnList).GroupBy(entry => entry.EntryKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => new Queue<CollectionListEntry>(group.OrderBy(entry => entry.Position)), StringComparer.Ordinal);
            var used = new HashSet<Guid>();
            var offList = existing.Where(entry => !entry.OnList).ToList();
            next = [];
            var position = 0;
            foreach (var entry in incoming)
            {
                position++;
                var key = EntryKey(entry);
                if (pool.TryGetValue(key, out var queue) && queue.Count > 0)
                {
                    var kept = queue.Dequeue();
                    used.Add(kept.Id);
                    kept.Position = position;
                    kept.Title = entry.Title;
                    kept.Year = entry.Year;
                    kept.Creator = entry.Creator;
                    kept.Shelf = entry.Shelves.FirstOrDefault();
                    kept.IdsJson = JsonSerializer.Serialize(entry.Ids);
                    kept.MediaTypes = string.Join(',', entry.MediaTypes);
                    if (kept.Status is ListImportChoices.StatusMatched or ListImportChoices.StatusUnmatched)
                    {
                        Rematch(kept, matcher);
                    }

                    next.Add(kept);
                    continue;
                }

                var created = ToStoredEntry(collectionId, position, ResolveOne(entry, matcher), onList: true, existingId: null);
                next.Add(created);
                added++;
            }

            foreach (var gone in existing.Where(entry => entry.OnList && !used.Contains(entry.Id)))
            {
                removed++;
                if (string.Equals(source.SyncMode, ListImportChoices.SyncKeep, StringComparison.Ordinal))
                {
                    gone.OnList = false;
                    next.Add(gone);
                }
            }

            // Entries kept from earlier refreshes stay too, after the entries that are on the list now.
            next.AddRange(offList);
            for (var index = 0; index < next.Count; index++)
            {
                if (!next[index].OnList)
                {
                    next[index].Position = index + 1;
                }
            }
        }

        var now = _time.GetUtcNow();
        source.LastRefreshedAt = now;
        source.NextRefreshAt = NextRefresh(source.Refresh, now);
        await SaveAfterChangeAsync(source, next, candidates, ct).ConfigureAwait(false);

        var onList = next.Where(entry => entry.OnList && entry.Status != ListImportChoices.StatusIgnored).ToList();
        return new ListRefreshResultDto(
            added,
            removed,
            onList.Count(entry => entry.MatchedWorkId is not null && entry.Status != ListImportChoices.StatusUnmatched),
            onList.Count(entry => entry.Status == ListImportChoices.StatusUnmatched));
    }

    // ── Shared steps ─────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task SaveAfterChangeAsync(
        CollectionListSource source,
        List<CollectionListEntry> entries,
        IReadOnlyList<ListImportCandidate> candidates,
        CancellationToken ct)
    {
        var existingItems = await _lists.GetItemsAsync(source.CollectionId, ct).ConfigureAwait(false);
        var items = BuildItems(existingItems, entries, candidates, source.OrderBy, adoptExisting: false);
        source.UpdatedAt = _time.GetUtcNow();
        ApplyCounts(source, entries);
        await _lists.SaveAsync(source, entries, items, ct).ConfigureAwait(false);
    }

    private static void ApplyCounts(CollectionListSource source, IReadOnlyList<CollectionListEntry> entries)
    {
        var counted = entries.Where(entry => entry.OnList && entry.Status != ListImportChoices.StatusIgnored).ToList();
        source.EntryCount = counted.Count;
        source.MatchedCount = counted.Count(entry => entry.MatchedWorkId is not null && entry.Status != ListImportChoices.StatusUnmatched);
        source.UnmatchedCount = counted.Count(entry => entry.Status == ListImportChoices.StatusUnmatched);
    }

    /// <summary>
    /// What the collection's items should be: every title a person added by hand (unchanged), then the list's matched titles in
    /// the chosen order. A title both a person and the list added stays the person's. Titles the list added earlier that no
    /// entry holds any more are dropped, so "Follow the list" removes a title that left the list while "Keep everything ever
    /// added" (which keeps the entry) leaves it.
    /// </summary>
    private static List<ListCollectionItemPlacement> BuildItems(
        IReadOnlyList<ListCollectionItem> existing,
        IReadOnlyList<CollectionListEntry> entries,
        IReadOnlyList<ListImportCandidate> candidates,
        string orderBy,
        bool adoptExisting)
    {
        var byWork = candidates.ToDictionary(candidate => candidate.WorkId);
        var handWorks = existing
            .Where(item => adoptExisting ? false : string.Equals(item.AddedBy, "hand", StringComparison.Ordinal))
            .Select(item => item.WorkId)
            .ToHashSet();

        var listed = entries
            .Where(entry => entry.MatchedWorkId is not null
                            && entry.Status is ListImportChoices.StatusMatched or ListImportChoices.StatusHandFixed)
            .Select(entry => (Entry: entry, WorkId: entry.MatchedWorkId!.Value))
            .GroupBy(item => item.WorkId)
            .Select(group => group.OrderBy(item => item.Entry.Position).First())
            .ToList();

        IEnumerable<(CollectionListEntry Entry, Guid WorkId)> ordered = orderBy switch
        {
            ListImportChoices.OrderRelease => listed
                .OrderBy(item => YearOf(item, byWork) ?? int.MaxValue)
                .ThenBy(item => TitleOf(item, byWork), StringComparer.OrdinalIgnoreCase),
            ListImportChoices.OrderTitle => listed
                .OrderBy(item => ListEntryMatcher.NormalizeTitle(TitleOf(item, byWork)), StringComparer.Ordinal),
            _ => listed.OrderBy(item => item.Entry.Position),
        };

        var result = new List<ListCollectionItemPlacement>();
        var placed = new HashSet<Guid>();
        var sort = 1;
        foreach (var item in ordered)
        {
            placed.Add(item.WorkId);
            result.Add(new ListCollectionItemPlacement(item.WorkId, sort++, handWorks.Contains(item.WorkId) ? "hand" : "list"));
        }

        // Hand-added titles that the list does not name come after the list's titles, in the order they already had.
        foreach (var item in existing.Where(item => !placed.Contains(item.WorkId)
                                                    && (adoptExisting || string.Equals(item.AddedBy, "hand", StringComparison.Ordinal)))
                     .OrderBy(item => item.SortOrder))
        {
            result.Add(new ListCollectionItemPlacement(item.WorkId, sort++, "hand"));
        }

        return result;
    }

    private static int? YearOf((CollectionListEntry Entry, Guid WorkId) item, Dictionary<Guid, ListImportCandidate> byWork) =>
        byWork.TryGetValue(item.WorkId, out var candidate) ? candidate.Year ?? item.Entry.Year : item.Entry.Year;

    private static string TitleOf((CollectionListEntry Entry, Guid WorkId) item, Dictionary<Guid, ListImportCandidate> byWork) =>
        byWork.TryGetValue(item.WorkId, out var candidate) ? candidate.Title : item.Entry.Title;

    // ── Matching ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private ListEntryMatcher BuildMatcher(IReadOnlyList<ListImportCandidate> candidates) =>
        new(candidates, _fuzzy, _configuration.LoadScoring().AutoLinkThreshold);

    private List<ResolvedEntry> Resolve(
        IReadOnlyList<ParsedListEntry> entries,
        IReadOnlyList<ListImportCandidate> candidates,
        IReadOnlyList<ListEntryOverrideDto> overrides)
    {
        var matcher = BuildMatcher(candidates);
        var known = candidates.Select(candidate => candidate.WorkId).ToHashSet();
        var byPosition = overrides
            .Where(item => item.Position > 0)
            .GroupBy(item => item.Position)
            .ToDictionary(group => group.Key, group => group.Last());

        var result = new List<ResolvedEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (byPosition.TryGetValue(entry.Position, out var fix))
            {
                if (fix.Ignore)
                {
                    result.Add(new ResolvedEntry(entry, ListImportChoices.StatusIgnored, null, null));
                    continue;
                }

                if (fix.WorkId is { } picked && known.Contains(picked))
                {
                    result.Add(new ResolvedEntry(entry, ListImportChoices.StatusHandFixed, picked, "hand"));
                    continue;
                }
            }

            result.Add(ResolveOne(entry, matcher));
        }

        return result;
    }

    private static ResolvedEntry ResolveOne(ParsedListEntry entry, ListEntryMatcher matcher)
    {
        var match = matcher.Match(new ListMatchInput(entry.Title, entry.Year, entry.Creator, entry.Ids, entry.MediaTypes));
        return match is null
            ? new ResolvedEntry(entry, ListImportChoices.StatusUnmatched, null, null)
            : new ResolvedEntry(entry, ListImportChoices.StatusMatched, match.WorkId, match.Basis);
    }

    private static void Rematch(CollectionListEntry entry, ListEntryMatcher matcher)
    {
        var match = matcher.Match(ToInput(entry));
        entry.Status = match is null ? ListImportChoices.StatusUnmatched : ListImportChoices.StatusMatched;
        entry.MatchedWorkId = match?.WorkId;
        entry.MatchBasis = match?.Basis;
    }

    private static ListMatchInput ToInput(CollectionListEntry entry) =>
        new(entry.Title, entry.Year, entry.Creator, ParseIds(entry.IdsJson), ParseMediaTypes(entry.MediaTypes));

    private static IReadOnlyDictionary<string, string> ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            // The identifiers were written by this service as JSON; text that does not parse adds no identifiers and the entry
            // simply matches by title.
            return new Dictionary<string, string>();
        }
    }

    private static IReadOnlyList<string> ParseMediaTypes(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // ── Stored entries and DTOs ──────────────────────────────────────────────────────────────────────────────────────

    private static CollectionListEntry ToStoredEntry(Guid collectionId, int position, ResolvedEntry resolved, bool onList, Guid? existingId) =>
        new()
        {
            Id = existingId ?? Guid.NewGuid(),
            CollectionId = collectionId,
            Position = position,
            Title = resolved.Entry.Title,
            Year = resolved.Entry.Year,
            Creator = resolved.Entry.Creator,
            Shelf = resolved.Entry.Shelves.FirstOrDefault(),
            IdsJson = JsonSerializer.Serialize(resolved.Entry.Ids),
            MediaTypes = string.Join(',', resolved.Entry.MediaTypes),
            EntryKey = EntryKey(resolved.Entry),
            Status = resolved.Status,
            MatchedWorkId = resolved.WorkId,
            MatchBasis = resolved.Basis,
            OnList = onList,
        };

    /// <summary>An entry's identity across refreshes: its strongest identifier, otherwise its normalised title and year.</summary>
    internal static string EntryKey(ParsedListEntry entry)
    {
        foreach (var key in ListIdentifierKeys.All)
        {
            if (entry.Ids.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return $"{key}:{value.Trim().ToLowerInvariant()}";
            }
        }

        return $"title:{ListEntryMatcher.NormalizeTitle(entry.Title)}|{entry.Year}";
    }

    private static CollectionListEntryDto ToEntryDto(
        CollectionListEntry entry) =>
        new()
        {
            Id = entry.Id,
            Position = entry.Position,
            Title = entry.Title,
            Year = entry.Year,
            Creator = entry.Creator,
            Shelf = entry.Shelf,
            Status = entry.Status,
            MatchedWorkId = entry.MatchedWorkId,
            MatchBasis = entry.MatchBasis,
            OnList = entry.OnList,
        };

    private static CollectionListEntryDto ToEntryDto(
        CollectionListEntry? stored, ResolvedEntry resolved, Dictionary<Guid, ListImportCandidate> byWork) =>
        new()
        {
            Id = stored?.Id,
            Position = resolved.Entry.Position,
            Title = resolved.Entry.Title,
            Year = resolved.Entry.Year,
            Creator = resolved.Entry.Creator,
            Shelf = resolved.Entry.Shelves.FirstOrDefault(),
            Status = resolved.Status,
            MatchedWorkId = resolved.WorkId,
            MatchedTitle = resolved.WorkId is { } id && byWork.TryGetValue(id, out var candidate) ? candidate.Title : null,
            MatchBasis = resolved.Basis,
        };

    private static string SuggestName(ParsedList parsed, string? shelf)
    {
        var baseName = !string.IsNullOrWhiteSpace(parsed.Name) ? parsed.Name : parsed.SourceLabel;
        var name = string.IsNullOrWhiteSpace(shelf) ? baseName : $"{baseName} · {shelf}";
        return name.Length > MaximumNameLength ? name[..MaximumNameLength] : name;
    }

    private static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        return name.Length > 200 ? name[..200] : name;
    }

    // ── Choices ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public static string NormalizeOrder(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ListImportChoices.OrderRelease => ListImportChoices.OrderRelease,
        ListImportChoices.OrderTitle => ListImportChoices.OrderTitle,
        _ => ListImportChoices.OrderList,
    };

    public static string NormalizeRefresh(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        ListImportChoices.RefreshDaily => ListImportChoices.RefreshDaily,
        ListImportChoices.RefreshWeekly => ListImportChoices.RefreshWeekly,
        _ => ListImportChoices.RefreshNever,
    };

    public static string NormalizeSync(string? value) =>
        string.Equals(value?.Trim(), ListImportChoices.SyncKeep, StringComparison.OrdinalIgnoreCase)
            ? ListImportChoices.SyncKeep
            : ListImportChoices.SyncFollow;

    /// <summary>When the next scheduled refresh is due: a day or a week on, or never.</summary>
    public static DateTimeOffset? NextRefresh(string refresh, DateTimeOffset now) => refresh switch
    {
        ListImportChoices.RefreshDaily => now.AddDays(1),
        ListImportChoices.RefreshWeekly => now.AddDays(7),
        _ => null,
    };

    private sealed record ResolvedEntry(ParsedListEntry Entry, string Status, Guid? WorkId, string? Basis)
    {
        public bool IsOwned => WorkId is not null && Status is ListImportChoices.StatusMatched or ListImportChoices.StatusHandFixed;
    }
}
