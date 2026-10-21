using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Items;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>Why a move request was turned away.</summary>
public enum ItemMoveRefusalKind
{
    /// <summary>The title is not one that can be moved (not a song, episode or part with a file).</summary>
    NotMovable,

    /// <summary>The destination is gone or does not accept this kind (spec D15).</summary>
    DestinationUnavailable,

    /// <summary>The person confirmed a plan that is out of date. Nothing was changed; the refusal carries the fresh preview.</summary>
    Changed,
}

public sealed record ItemMoveRefusal(ItemMoveRefusalKind Kind, string Message, ItemMovePreview? FreshPreview = null);

public sealed class ItemMoveResult<T> where T : class
{
    private ItemMoveResult(T? value, ItemMoveRefusal? refusal)
    {
        Value = value;
        Refusal = refusal;
    }

    public T? Value { get; }

    public ItemMoveRefusal? Refusal { get; }

    public bool IsSuccess => Value is not null;

    public static ItemMoveResult<T> Ok(T value) => new(value, null);

    public static ItemMoveResult<T> Refused(ItemMoveRefusal refusal) => new(null, refusal);
}

/// <summary>
/// Match first, then move (details overlay spec D12 to D17). Works out what a move would do, in the person's words, and acts only on
/// a plan the person confirmed by its fingerprint: a changed plan is refused with a fresh preview and nothing is touched. The same
/// rules serve songs, episodes and audiobook parts; the Dashboard has one Match screen for all of them.
/// </summary>
public sealed class ItemMoveService(ItemMoveRepository repository)
{
    // One move at a time: two people moving into the same list must not both take the same empty entry.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ItemMoveResult<ItemMoveDestinationsResponse>> FindDestinationsAsync(
        Guid workId, string? query, CancellationToken ct = default)
    {
        var source = await repository.GetSourceAsync(workId, ct);
        if (source is null)
        {
            return ItemMoveResult<ItemMoveDestinationsResponse>.Refused(new(ItemMoveRefusalKind.NotMovable, NotMovableMessage));
        }

        var found = await repository.FindDestinationsAsync(source, query, 25, ct);
        return ItemMoveResult<ItemMoveDestinationsResponse>.Ok(new ItemMoveDestinationsResponse
        {
            Destinations = [.. found.Select(d => new ItemMoveDestinationDto { Id = d.Id, Title = d.Title, EntryCount = d.EntryCount })],
        });
    }

    public async Task<ItemMoveResult<ItemMovePreview>> PreviewAsync(ItemMovePreviewRequest request, CancellationToken ct = default)
    {
        var source = await repository.GetSourceAsync(request.WorkId, ct);
        if (source is null)
        {
            return ItemMoveResult<ItemMovePreview>.Refused(new(ItemMoveRefusalKind.NotMovable, NotMovableMessage));
        }

        var destination = await repository.GetDestinationAsync(request.DestinationId, ct);
        if (destination is null || destination.Id == source.ParentId
            || !(await repository.FindDestinationsAsync(source, null, int.MaxValue, ct)).Any(d => d.Id == destination.Id))
        {
            return ItemMoveResult<ItemMovePreview>.Refused(new(ItemMoveRefusalKind.DestinationUnavailable, "That place does not take this kind of title."));
        }

        var entries = await repository.GetEntriesAsync(destination.Id, ct);
        return ItemMoveResult<ItemMovePreview>.Ok(Build(source, destination, entries, request.EntryId, request.Mode));
    }

    public async Task<ItemMoveResult<ItemMoveResultDto>> MoveAsync(ItemMoveRequest request, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var preview = await PreviewAsync(new ItemMovePreviewRequest
            {
                WorkId = request.WorkId,
                DestinationId = request.DestinationId,
                EntryId = request.EntryId,
                Mode = request.Mode,
            }, ct);
            if (!preview.IsSuccess)
            {
                return ItemMoveResult<ItemMoveResultDto>.Refused(preview.Refusal!);
            }

            var plan = preview.Value!;
            if (!string.Equals(plan.Fingerprint, request.Fingerprint, StringComparison.Ordinal) || plan.BlockedReason is not null)
            {
                return ItemMoveResult<ItemMoveResultDto>.Refused(new(ItemMoveRefusalKind.Changed,
                    plan.BlockedReason ?? "Something changed since you looked. Here is what would happen now.", plan));
            }

            var mode = ParseMode(plan.Mode);
            var source = (await repository.GetSourceAsync(request.WorkId, ct))!;
            var outcome = await repository.CommitAsync(new ItemMoveCommand(
                request.WorkId, request.DestinationId, plan.SelectedEntryId, mode, source.EditionIds.Count, plan.SelectedEntryTaken), ct);
            if (outcome != ItemMoveOutcome.Moved)
            {
                var fresh = await PreviewAsync(new ItemMovePreviewRequest
                {
                    WorkId = request.WorkId,
                    DestinationId = request.DestinationId,
                    EntryId = request.EntryId,
                    Mode = request.Mode,
                }, ct);
                return ItemMoveResult<ItemMoveResultDto>.Refused(new(ItemMoveRefusalKind.Changed,
                    "Something changed while moving. Nothing was moved.", fresh.Value));
            }

            return ItemMoveResult<ItemMoveResultDto>.Ok(new ItemMoveResultDto
            {
                WorkId = request.WorkId,
                LandedWorkId = mode == ItemMoveMode.AddAnyway ? request.WorkId : plan.SelectedEntryId ?? request.WorkId,
                DestinationTitle = plan.DestinationTitle,
                Mode = plan.Mode,
            });
        }
        finally
        {
            Gate.Release();
        }
    }

    private const string NotMovableMessage = "Only a song, an episode or an audiobook part with a file can be moved.";

    private static ItemMovePreview Build(
        ItemMoveSource source, ItemMoveDestination destination, IReadOnlyList<ItemMoveEntry> entries, Guid? picked, string? requestedMode)
    {
        var addAnyway = string.Equals(requestedMode, ItemMoveModes.AddAnyway, StringComparison.Ordinal);
        ItemMoveEntry? selected = null;
        string? reason = null;
        if (!addAnyway)
        {
            selected = picked is { } id ? entries.FirstOrDefault(e => e.Id == id) : null;
            if (selected is null && picked is null)
            {
                (selected, reason) = BestGuess(source, entries);
            }
        }

        var mode = addAnyway ? ItemMoveModes.AddAnyway
            : selected is { Taken: true } ? ItemMoveModes.AnotherVersion
            : ItemMoveModes.Match;
        // A taken entry is only a version when the person said so; until then the choice stays open (D16), so the confirm is off.
        var versionChosen = selected is { Taken: true } && string.Equals(requestedMode, ItemMoveModes.AnotherVersion, StringComparison.Ordinal);

        string? blocked = null;
        if (!addAnyway && selected is null)
        {
            blocked = "Pick the entry this is, or choose Add anyway.";
        }
        else if (selected is { Taken: true } && !versionChosen)
        {
            blocked = $"{EntryName(selected)} already has a file. Add as another version, or pick another entry.";
        }

        var leavesEmpty = source.OtherOwnedSiblings == 0;
        var message = addAnyway
            ? $"This won't get a number or details from {destination.Title}. You can match it later. It leaves {source.ParentTitle ?? "its old place"}{(leavesEmpty ? ", which will have no files left" : string.Empty)}. The file will be renamed to fit and moved to {destination.Title}."
            : selected is null
                ? string.Empty
                : versionChosen
                    ? $"Add {source.Title} as another version of {EntryName(selected)} in {destination.Title}? It leaves {source.ParentTitle ?? "its old place"}{(leavesEmpty ? ", which will have no files left" : string.Empty)}. The file will be renamed to fit and moved to {destination.Title}."
                    : $"Move {source.Title} to {destination.Title} as {EntryName(selected)}? It leaves {source.ParentTitle ?? "its old place"}{(leavesEmpty ? ", which will have no files left" : string.Empty)}. The file will be renamed to fit and moved to {destination.Title}.";

        return new ItemMovePreview
        {
            WorkId = source.WorkId,
            Title = source.Title,
            FromTitle = source.ParentTitle,
            DestinationId = destination.Id,
            DestinationTitle = destination.Title,
            Entries = [.. entries.Select(e => new ItemMoveEntryDto { Id = e.Id, Title = e.Title, Number = e.Ordinal, Taken = e.Taken })],
            SelectedEntryId = selected?.Id,
            GuessReason = reason,
            Mode = mode,
            SelectedEntryTaken = selected?.Taken ?? false,
            Message = message,
            RenamesFile = true,
            LeavesOldPlaceEmpty = leavesEmpty,
            BlockedReason = blocked,
            Fingerprint = Fingerprint(source, destination, entries, selected?.Id, mode, versionChosen),
        };
    }

    // The Engine's best guess (spec "Match step"): the same title, else the same number when that entry is free.
    private static (ItemMoveEntry? Entry, string? Reason) BestGuess(ItemMoveSource source, IReadOnlyList<ItemMoveEntry> entries)
    {
        var wanted = Normalize(source.Title);
        if (wanted.Length > 0)
        {
            var byTitle = entries.Where(e => Normalize(e.Title) == wanted).OrderBy(e => e.Taken).FirstOrDefault();
            if (byTitle is not null)
            {
                return (byTitle, "Same title");
            }
        }

        if (source.Ordinal is { } number)
        {
            var byNumber = entries.FirstOrDefault(e => e.Ordinal == number && !e.Taken);
            if (byNumber is not null)
            {
                return (byNumber, $"Same number ({number})");
            }
        }

        return (null, null);
    }

    private static string EntryName(ItemMoveEntry entry) =>
        entry.Ordinal is { } n ? $"{n}. {entry.Title}".TrimEnd('.', ' ') : entry.Title;

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static ItemMoveMode ParseMode(string mode) => mode switch
    {
        ItemMoveModes.AddAnyway => ItemMoveMode.AddAnyway,
        ItemMoveModes.AnotherVersion => ItemMoveMode.AnotherVersion,
        _ => ItemMoveMode.MatchEntry,
    };

    private static string Fingerprint(
        ItemMoveSource source, ItemMoveDestination destination, IReadOnlyList<ItemMoveEntry> entries, Guid? entry, string mode, bool versionChosen)
    {
        var lines = new List<string>
        {
            $"work:{source.WorkId:N}", $"parent:{source.ParentId:N}", $"dest:{destination.Id:N}", $"entry:{entry:N}", $"mode:{mode}", $"version:{versionChosen}",
        };
        lines.AddRange(source.EditionIds.Select(id => $"edition:{id:N}"));
        lines.AddRange(entries.Select(e => $"e:{e.Id:N}:{e.Ordinal}:{e.Taken}:{e.EditionCount}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }
}
