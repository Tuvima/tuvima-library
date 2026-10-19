using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Activity;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Activity;

public enum PersonalActivityFailure
{
    None,
    BadRequest,
    Forbidden,
    NotFound,
}

/// <summary>A successful answer, or the reason it was refused and the sentence to show.</summary>
public sealed record PersonalActivityResult<T>(T? Value, PersonalActivityFailure Failure, string? Message)
{
    public bool IsSuccess => Failure == PersonalActivityFailure.None;

    public static PersonalActivityResult<T> Ok(T value) => new(value, PersonalActivityFailure.None, null);

    public static PersonalActivityResult<T> Fail(PersonalActivityFailure failure, string message) => new(default, failure, message);
}

/// <summary>What the Activity page asked for. Everything is checked before any data is read.</summary>
public sealed record PersonalActivityQuery
{
    /// <summary><c>me</c> (the default), <c>profile:{id}</c>, <c>household:{id}</c> or <c>everyone</c>.</summary>
    public string? Scope { get; init; }

    public string? Q { get; init; }
    public IReadOnlyList<string>? Kinds { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    /// <summary><c>finished</c> or <c>not_finished</c>.</summary>
    public string? Outcome { get; init; }

    public string? Device { get; init; }

    /// <summary><c>title</c> groups by title; anything else is one item per entry.</summary>
    public string? Group { get; init; }

    /// <summary><c>songs</c> includes the songs of each music session.</summary>
    public string? Expand { get; init; }

    public string? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}

/// <summary>A CSV download: the file name and the writer that streams it.</summary>
public sealed record PersonalActivityExport(string FileName, Func<Stream, CancellationToken, Task> WriteAsync);

/// <summary>
/// Reads, exports and (for a server administrator) deletes personal activity. The Engine decides whose activity may be
/// read through <see cref="IPersonalScopeAuthorizer"/>; nothing here trusts the page. Opening a scope that includes
/// other people is recorded by the authorizer; exporting and deleting are recorded here.
/// </summary>
public sealed partial class PersonalActivityReadService(
    IPersonalActivityRepository repository,
    IPersonalScopeAuthorizer scopes,
    IAuthorizationAuditWriter audit,
    TimeProvider clock)
{
    public const string ExportedOthers = "activity.exported_others";
    public const string HistoryDeleted = "activity.history_deleted";

    internal const int MaxCoversPerSession = 9;
    private const int ExportPageSize = 500;
    private const string ArtworkRoute = "/api/v1/activity/artwork/";

    public async Task<PersonalActivityResult<PersonalActivityPageDto>> ListAsync(
        RequestAuthority authority, PersonalActivityQuery query, CancellationToken ct)
    {
        var resolved = await ResolveAsync(authority, query, ct).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return PersonalActivityResult<PersonalActivityPageDto>.Fail(resolved.Failure, resolved.Message!);
        }

        var request = resolved.Value!;
        PersonalActivityItemPage page;
        try
        {
            page = await repository.GetItemsAsync(request.Filter, request.Grouping, query.Cursor, query.Limit, ct).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return PersonalActivityResult<PersonalActivityPageDto>.Fail(PersonalActivityFailure.BadRequest, exception.Message);
        }

        var items = page.Items.Select(item => ToDto(item, request)).ToList();
        return PersonalActivityResult<PersonalActivityPageDto>.Ok(
            new PersonalActivityPageDto(items, page.NextCursor, request.IncludesOthers));
    }

    public async Task<PersonalActivityResult<PersonalActivityPeriodsDto>> PeriodsAsync(
        RequestAuthority authority, PersonalActivityQuery query, CancellationToken ct)
    {
        var resolved = await ResolveAsync(authority, query, ct).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return PersonalActivityResult<PersonalActivityPeriodsDto>.Fail(resolved.Failure, resolved.Message!);
        }

        var request = resolved.Value!;
        var counts = await repository.GetPeriodsAsync(request.Filter, request.Grouping, ct).ConfigureAwait(false);
        var years = counts
            .GroupBy(count => count.Year)
            .OrderByDescending(group => group.Key)
            .Select(group => new PersonalActivityYearCountDto(
                group.Key,
                group.Sum(count => count.Count),
                group.OrderByDescending(count => count.Month)
                    .Select(count => new PersonalActivityMonthCountDto(count.Month, count.Count))
                    .ToList()))
            .ToList();
        return PersonalActivityResult<PersonalActivityPeriodsDto>.Ok(new PersonalActivityPeriodsDto(years));
    }

    /// <summary>The caller's own latest activity for the Overview strip, music collapsed into sessions.</summary>
    public async Task<PersonalActivityResult<PersonalActivityRecentDto>> RecentAsync(
        RequestAuthority authority, int limit, CancellationToken ct)
    {
        var decision = await scopes.AuthorizeAsync(authority, PersonalScopeKind.Activity, PersonalScope.Self, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            return Failure<PersonalActivityRecentDto>(decision);
        }

        var page = await repository.GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = decision.ProfileIds }, PersonalActivityGrouping.None, null, limit, ct).ConfigureAwait(false);
        var tiles = page.Items.Select(item => ToTile(item, "me")).ToList();
        return PersonalActivityResult<PersonalActivityRecentDto>.Ok(new PersonalActivityRecentDto(tiles));
    }

    public async Task<PersonalActivityResult<PersonalActivityExport>> ExportAsync(
        RequestAuthority authority, PersonalActivityQuery query, CancellationToken ct)
    {
        var parsed = Parse(query);
        if (!parsed.IsSuccess)
        {
            return PersonalActivityResult<PersonalActivityExport>.Fail(parsed.Failure, parsed.Message!);
        }

        // Only a server administrator exports anyone else's activity. A refusal here leaves no "viewed" entry behind.
        if (!IsOwn(parsed.Value!.Scope, authority) &&
            !await scopes.CanDeletePersonalHistoryAsync(authority, ct).ConfigureAwait(false))
        {
            return PersonalActivityResult<PersonalActivityExport>.Fail(
                PersonalActivityFailure.Forbidden, "Only a server administrator can export other people's activity.");
        }

        var resolved = await ResolveAsync(authority, query, ct).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return PersonalActivityResult<PersonalActivityExport>.Fail(resolved.Failure, resolved.Message!);
        }

        var request = resolved.Value!;
        if (request.IncludesOthers)
        {
            await WriteAuditAsync(authority, ExportedOthers, request.Scope, new Dictionary<string, string?>
            {
                ["scope"] = request.ScopeText,
                ["from"] = request.Filter.From?.ToString("O", CultureInfo.InvariantCulture),
                ["to"] = request.Filter.To?.ToString("O", CultureInfo.InvariantCulture),
                ["search"] = request.Filter.Query,
            }, ct).ConfigureAwait(false);
        }

        var stamp = clock.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return PersonalActivityResult<PersonalActivityExport>.Ok(new PersonalActivityExport(
            $"tuvima-activity-{stamp}.csv",
            (stream, token) => WriteCsvAsync(stream, request, token)));
    }

    public async Task<PersonalActivityResult<PersonalActivityDeletePreviewDto>> DeletePreviewAsync(
        RequestAuthority authority, string? scope, DateTimeOffset before, CancellationToken ct)
    {
        var target = await ResolveDeletionAsync(authority, scope, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return PersonalActivityResult<PersonalActivityDeletePreviewDto>.Fail(target.Failure, target.Message!);
        }

        var count = await repository.CountBeforeAsync(before, target.Value!.ProfileIds, ct).ConfigureAwait(false);
        return PersonalActivityResult<PersonalActivityDeletePreviewDto>.Ok(new PersonalActivityDeletePreviewDto(count));
    }

    public async Task<PersonalActivityResult<PersonalActivityDeleteResultDto>> DeleteAsync(
        RequestAuthority authority, string? scope, DateTimeOffset before, CancellationToken ct)
    {
        var target = await ResolveDeletionAsync(authority, scope, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return PersonalActivityResult<PersonalActivityDeleteResultDto>.Fail(target.Failure, target.Message!);
        }

        var deletion = target.Value!;
        var deleted = await repository.DeleteBeforeAsync(before, deletion.ProfileIds, ct).ConfigureAwait(false);
        await WriteAuditAsync(authority, HistoryDeleted, deletion.Scope, new Dictionary<string, string?>
        {
            ["scope"] = deletion.ScopeText,
            ["before"] = before.ToString("O", CultureInfo.InvariantCulture),
            ["count"] = deleted.ToString(CultureInfo.InvariantCulture),
        }, ct).ConfigureAwait(false);
        return PersonalActivityResult<PersonalActivityDeleteResultDto>.Ok(new PersonalActivityDeleteResultDto(deleted));
    }

    /// <summary>The small picture, only when the caller may see at least one entry that uses it.</summary>
    public async Task<PersonalActivityResult<PersonalActivityArtwork>> ArtworkAsync(
        RequestAuthority authority, string key, string? scope, CancellationToken ct)
    {
        if (!ArtworkKeyPattern().IsMatch(key))
        {
            return PersonalActivityResult<PersonalActivityArtwork>.Fail(PersonalActivityFailure.NotFound, "That picture was not found.");
        }

        var parsed = ParseScope(scope);
        if (!parsed.IsSuccess)
        {
            return PersonalActivityResult<PersonalActivityArtwork>.Fail(parsed.Failure, parsed.Message!);
        }

        var decision = await scopes.AuthorizeAsync(authority, PersonalScopeKind.Activity, parsed.Value!.Scope, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            return Failure<PersonalActivityArtwork>(decision);
        }

        var people = decision.AllProfiles ? null : decision.ProfileIds;
        var visible = await repository.IsArtworkUsedAsync(key, people, ct).ConfigureAwait(false);
        var artwork = visible ? await repository.GetArtworkAsync(key, ct).ConfigureAwait(false) : null;
        return artwork is null
            ? PersonalActivityResult<PersonalActivityArtwork>.Fail(PersonalActivityFailure.NotFound, "That picture was not found.")
            : PersonalActivityResult<PersonalActivityArtwork>.Ok(artwork);
    }

    private async Task<PersonalActivityResult<Deletion>> ResolveDeletionAsync(
        RequestAuthority authority, string? scope, CancellationToken ct)
    {
        var parsed = ParseScope(scope);
        if (!parsed.IsSuccess)
        {
            return PersonalActivityResult<Deletion>.Fail(parsed.Failure, parsed.Message!);
        }

        if (!await scopes.CanDeletePersonalHistoryAsync(authority, ct).ConfigureAwait(false))
        {
            return PersonalActivityResult<Deletion>.Fail(
                PersonalActivityFailure.Forbidden, "Only a server administrator can delete activity history.");
        }

        var requested = parsed.Value!;
        var decision = await scopes.AuthorizeAsync(authority, PersonalScopeKind.Activity, requested.Scope, ct).ConfigureAwait(false);
        return decision.Allowed
            ? PersonalActivityResult<Deletion>.Ok(new Deletion(
                requested.Scope, requested.Text, decision.AllProfiles ? null : decision.ProfileIds))
            : Failure<Deletion>(decision);
    }

    private async Task<PersonalActivityResult<ResolvedRequest>> ResolveAsync(
        RequestAuthority authority, PersonalActivityQuery query, CancellationToken ct)
    {
        var parsed = Parse(query);
        if (!parsed.IsSuccess)
        {
            return parsed;
        }

        var request = parsed.Value!;
        var decision = await scopes.AuthorizeAsync(authority, PersonalScopeKind.Activity, request.Scope, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            return Failure<ResolvedRequest>(decision);
        }

        return PersonalActivityResult<ResolvedRequest>.Ok(request with
        {
            IncludesOthers = decision.IncludesOthers,
            Filter = request.Filter with
            {
                ProfileIds = decision.AllProfiles ? null : decision.ProfileIds,
                SearchPeople = decision.IncludesOthers,
            },
        });
    }

    private static PersonalActivityResult<ResolvedRequest> Parse(PersonalActivityQuery query)
    {
        var scope = ParseScope(query.Scope);
        if (!scope.IsSuccess)
        {
            return PersonalActivityResult<ResolvedRequest>.Fail(scope.Failure, scope.Message!);
        }

        var kinds = new List<string>();
        foreach (var raw in query.Kinds ?? [])
        {
            foreach (var kind in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = kind.ToLowerInvariant();
                if (!PersonalActivityKinds.IsKnown(normalized))
                {
                    return Bad("A kind is watch, listen, read or my_list.");
                }

                kinds.Add(normalized);
            }
        }

        bool? finished;
        switch (query.Outcome?.Trim().ToLowerInvariant())
        {
            case null or "":
                finished = null;
                break;
            case "finished":
                finished = true;
                break;
            case "not_finished":
                finished = false;
                break;
            default:
                return Bad("The outcome is finished or not_finished.");
        }

        PersonalActivityGrouping grouping;
        switch (query.Group?.Trim().ToLowerInvariant())
        {
            case null or "" or "none":
                grouping = PersonalActivityGrouping.None;
                break;
            case "title":
                grouping = PersonalActivityGrouping.Title;
                break;
            default:
                return Bad("The grouping is title or none.");
        }

        if (!string.IsNullOrWhiteSpace(query.Expand) &&
            !string.Equals(query.Expand.Trim(), "songs", StringComparison.OrdinalIgnoreCase))
        {
            return Bad("Expand can only be songs.");
        }

        if (query.From is { } from && query.To is { } to && from >= to)
        {
            return Bad("The start of the date range must be before its end.");
        }

        var filter = new PersonalActivityFilter
        {
            Query = string.IsNullOrWhiteSpace(query.Q) ? null : query.Q.Trim(),
            Kinds = kinds.Count == 0 ? null : kinds.Distinct().ToList(),
            From = query.From,
            To = query.To,
            Finished = finished,
            Device = string.IsNullOrWhiteSpace(query.Device) ? null : query.Device.Trim(),
        };
        var songs = string.Equals(query.Expand?.Trim(), "songs", StringComparison.OrdinalIgnoreCase);
        return PersonalActivityResult<ResolvedRequest>.Ok(new ResolvedRequest(
            scope.Value!.Scope, scope.Value.Text, filter, grouping, songs, false));

        static PersonalActivityResult<ResolvedRequest> Bad(string message) =>
            PersonalActivityResult<ResolvedRequest>.Fail(PersonalActivityFailure.BadRequest, message);
    }

    private static PersonalActivityResult<ParsedScope> ParseScope(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            return PersonalActivityResult<ParsedScope>.Ok(new ParsedScope(PersonalScope.Self, "me"));
        }

        if (value.Equals("everyone", StringComparison.OrdinalIgnoreCase))
        {
            return PersonalActivityResult<ParsedScope>.Ok(new ParsedScope(PersonalScope.Everyone, "everyone"));
        }

        var split = value.IndexOf(':', StringComparison.Ordinal);
        if (split > 0 && Guid.TryParse(value[(split + 1)..], out var id) && id != Guid.Empty)
        {
            var head = value[..split].ToLowerInvariant();
            if (head == "profile")
            {
                return PersonalActivityResult<ParsedScope>.Ok(new ParsedScope(PersonalScope.Profile(id), $"profile:{id:D}"));
            }

            if (head == "household")
            {
                return PersonalActivityResult<ParsedScope>.Ok(new ParsedScope(PersonalScope.Household(id), $"household:{id:D}"));
            }
        }

        return PersonalActivityResult<ParsedScope>.Fail(
            PersonalActivityFailure.BadRequest, "The scope is me, profile:{id}, household:{id} or everyone.");
    }

    private static bool IsOwn(PersonalScope scope, RequestAuthority authority) =>
        scope.Target == PersonalScopeTarget.Self ||
        (scope.Target == PersonalScopeTarget.Profile && scope.Id is { } id && id == authority.ActiveProfileId);

    private static PersonalActivityResult<T> Failure<T>(PersonalScopeDecision decision) => decision.Refusal switch
    {
        PersonalScopeRefusal.UnknownTarget => PersonalActivityResult<T>.Fail(
            PersonalActivityFailure.NotFound, "That person or household was not found."),
        PersonalScopeRefusal.UnlockRequired => PersonalActivityResult<T>.Fail(
            PersonalActivityFailure.Forbidden, "Unlock administrator access to see other people's activity."),
        PersonalScopeRefusal.NotSignedIn => PersonalActivityResult<T>.Fail(
            PersonalActivityFailure.Forbidden, "Sign in as a person to see activity."),
        _ => PersonalActivityResult<T>.Fail(PersonalActivityFailure.Forbidden, "You can't see that activity."),
    };

    private async Task WriteAuditAsync(
        RequestAuthority authority,
        string eventType,
        PersonalScope scope,
        IReadOnlyDictionary<string, string?> changes,
        CancellationToken ct)
    {
        var subjectType = scope.Target.ToString().ToLowerInvariant();
        var subjectId = scope.Id?.ToString("D")
            ?? (scope.Target == PersonalScopeTarget.Self ? authority.ActiveProfileId?.ToString("D") ?? "self" : "everyone");
        await audit.WriteAsync(new AuthorizationAuditEvent(
            eventType,
            clock.GetUtcNow(),
            authority.AccountId,
            authority.ActiveProfileId,
            authority.ApplicationId,
            subjectType,
            subjectId,
            changes), ct).ConfigureAwait(false);
    }

    // ----- Mapping -----

    private static PersonalActivityItemDto ToDto(PersonalActivityItem item, ResolvedRequest request)
    {
        if (item.IsMusicSession)
        {
            return SessionDto(item, request);
        }

        if (item.IsTitleGroup)
        {
            return GroupDto(item, request);
        }

        return EntryDto(item.Entries[0], request);
    }

    private static PersonalActivityItemDto EntryDto(PersonalActivityEntry entry, ResolvedRequest request) => new()
    {
        Id = entry.Id.ToString("D"),
        Kind = entry.Kind,
        StartedAt = Iso(entry.StartedAt),
        EndedAt = entry.EndedAt is { } ended ? Iso(ended) : null,
        PlayedSeconds = entry.PlayedSeconds,
        ProgressPct = entry.ProgressPct,
        Finished = entry.Finished,
        DeviceName = entry.DeviceName,
        DeviceKind = entry.DeviceKind,
        MediaType = entry.MediaType,
        Title = entry.Title,
        Subtitle = entry.Subtitle,
        ParentTitle = entry.ParentTitle,
        Year = entry.Year,
        ArtworkUrl = ArtworkUrl(entry.ArtworkKey, request.ScopeText),
        WorkId = entry.WorkId?.ToString("D"),
        Person = Person(entry, request),
    };

    private static PersonalActivityItemDto GroupDto(PersonalActivityItem item, ResolvedRequest request)
    {
        var latest = item.Entries[^1];
        var dto = EntryDto(latest, request);
        dto.Id = "title:" + item.Key;
        dto.StartedAt = Iso(latest.StartedAt);
        var ends = item.Entries.Where(entry => entry.EndedAt.HasValue).Select(entry => entry.EndedAt!.Value).ToList();
        dto.EndedAt = ends.Count == 0 ? null : Iso(ends.Max());
        dto.PlayedSeconds = item.Entries.Sum(entry => entry.PlayedSeconds);
        var reached = item.Entries.Where(entry => entry.ProgressPct.HasValue).Select(entry => entry.ProgressPct!.Value).ToList();
        dto.ProgressPct = reached.Count == 0 ? null : reached.Max();
        dto.Finished = item.Entries.Any(entry => entry.Finished);
        dto.ArtworkUrl = ArtworkUrl(item.Entries.LastOrDefault(entry => entry.ArtworkKey is not null)?.ArtworkKey, request.ScopeText);
        dto.AttemptCount = item.Entries.Count;
        dto.Attempts = item.Entries.Select(entry => EntryDto(entry, request)).ToList();
        return dto;
    }

    private static PersonalActivityItemDto SessionDto(PersonalActivityItem item, ResolvedRequest request)
    {
        var first = item.Entries[0];
        var last = item.Entries[^1];
        var albums = item.Entries
            .Select(entry => entry.ParentTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var dto = new PersonalActivityItemDto
        {
            Id = "session:" + (first.MusicSessionId?.ToString("D") ?? item.Key),
            Kind = PersonalActivityKindValues.MusicSession,
            StartedAt = Iso(first.StartedAt),
            EndedAt = Iso(last.EndedAt ?? last.StartedAt),
            PlayedSeconds = item.Entries.Sum(entry => entry.PlayedSeconds),
            DeviceName = last.DeviceName,
            DeviceKind = last.DeviceKind,
            MediaType = first.MediaType,
            Title = "Music",
            SongCount = item.Entries.Count,
            AlbumCount = albums,
            CoverUrls = CoverUrls(item.Entries, request.ScopeText),
            Person = Person(first, request),
        };
        if (request.ExpandSongs)
        {
            dto.Songs = item.Entries.Select(entry => EntryDto(entry, request)).ToList();
        }

        return dto;
    }

    private static PersonalActivityRecentTileDto ToTile(PersonalActivityItem item, string scopeText)
    {
        if (item.IsMusicSession)
        {
            var first = item.Entries[0];
            var last = item.Entries[^1];
            var albums = item.Entries
                .Select(entry => entry.ParentTitle)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            return new PersonalActivityRecentTileDto(
                PersonalActivityKindValues.MusicSession, "Music", null, null, Iso(first.StartedAt), Iso(last.EndedAt ?? last.StartedAt),
                item.Entries.Sum(entry => entry.PlayedSeconds), null, false, PersonalActivityShapeValues.Square,
                null, CoverUrls(item.Entries, scopeText), item.Entries.Count, albums, null);
        }

        var entry = item.Entries[0];
        return new PersonalActivityRecentTileDto(
            entry.Kind, entry.Title, entry.Subtitle, entry.ParentTitle, Iso(entry.StartedAt),
            entry.EndedAt is { } ended ? Iso(ended) : null, entry.PlayedSeconds, entry.ProgressPct, entry.Finished,
            ShapeFor(entry.MediaType), ArtworkUrl(entry.ArtworkKey, scopeText), null, null, null, entry.WorkId?.ToString("D"));
    }

    private static string ShapeFor(string mediaType) => DisplayMediaRules.NormalizeDisplayKind(mediaType) switch
    {
        "Movie" or "TV" => PersonalActivityShapeValues.Landscape,
        "Music" or "Audiobook" => PersonalActivityShapeValues.Square,
        _ => PersonalActivityShapeValues.Portrait,
    };

    /// <summary>Up to nine distinct album covers from the session, a different selection on each request.</summary>
    private static List<string> CoverUrls(IReadOnlyList<PersonalActivityEntry> songs, string scopeText) =>
        songs.Select(song => song.ArtworkKey)
            .Where(key => key is not null)
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(_ => Random.Shared.Next())
            .Take(MaxCoversPerSession)
            .Select(key => ArtworkUrl(key, scopeText)!)
            .ToList();

    private static PersonalActivityPersonDto? Person(PersonalActivityEntry entry, ResolvedRequest request) =>
        request.IncludesOthers
            ? new PersonalActivityPersonDto(entry.ProfileId.ToString("D"), entry.PersonName ?? string.Empty)
            : null;

    private static string? ArtworkUrl(string? key, string scopeText) => key is null
        ? null
        : scopeText == "me"
            ? ArtworkRoute + key
            : ArtworkRoute + key + "?scope=" + Uri.EscapeDataString(scopeText);

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // ----- CSV -----

    private async Task WriteCsvAsync(Stream stream, ResolvedRequest request, CancellationToken ct)
    {
        // CSV lines end in CR LF on every platform.
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true) { NewLine = "\r\n" };
        var columns = new List<string> { "Date (UTC)", "Time (UTC)" };
        if (request.IncludesOthers)
        {
            columns.Add("Person");
        }

        columns.AddRange(["Kind", "Title", "Subtitle", "Played", "Progress", "Outcome", "Device"]);
        await writer.WriteLineAsync(string.Join(',', columns)).ConfigureAwait(false);

        string? cursor = null;
        do
        {
            var (entries, next) = await repository.GetEntriesAsync(request.Filter, cursor, ExportPageSize, ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                var started = entry.StartedAt.ToUniversalTime();
                var cells = new List<string>
                {
                    started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    started.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                };
                if (request.IncludesOthers)
                {
                    cells.Add(Text(entry.PersonName));
                }

                cells.Add(entry.Kind);
                cells.Add(Text(entry.Title));
                cells.Add(Text(entry.Subtitle));
                cells.Add(Duration(entry.PlayedSeconds));
                cells.Add(entry.ProgressPct is { } pct ? Math.Round(pct).ToString("0", CultureInfo.InvariantCulture) : string.Empty);
                cells.Add(entry.Kind == PersonalActivityKinds.MyList ? "added" : entry.Finished ? "finished" : "stopped");
                cells.Add(Text(entry.DeviceName));
                await writer.WriteLineAsync(string.Join(',', cells)).ConfigureAwait(false);
            }

            cursor = next;
        }
        while (cursor is not null);

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{(long)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}");
    }

    /// <summary>
    /// A CSV cell for text from a title or a name. Quoted when it holds a comma, quote or line break; a value a
    /// spreadsheet would read as a formula gets a leading apostrophe.
    /// </summary>
    internal static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtworkKeyPattern();

    private sealed record ParsedScope(PersonalScope Scope, string Text);

    private sealed record Deletion(PersonalScope Scope, string ScopeText, IReadOnlySet<Guid>? ProfileIds);

    private sealed record ResolvedRequest(
        PersonalScope Scope,
        string ScopeText,
        PersonalActivityFilter Filter,
        PersonalActivityGrouping Grouping,
        bool ExpandSongs,
        bool IncludesOthers);
}
