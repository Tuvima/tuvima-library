using System.Collections.Concurrent;
using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.View;

public sealed class ViewOtherPeopleService(
    IDatabaseConnection database,
    IAuthorizationAuditWriter audit,
    TimeProvider clock) : IViewOtherPeopleAuditor, IViewOtherPeopleService
{
    public const string OpenedEventType = "view.other_household_opened";
    public const string PersonalSubject = "view_personal_space";
    public const string SharedSubject = "view_shared_library";

    /// <summary>A viewer who keeps looking at the same space is recorded once per hour, not once per photo.</summary>
    public static readonly TimeSpan RecordWindow = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<(Guid Actor, string Subject, string Id), DateTimeOffset> _recent = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RecordOpenAsync(RequestAuthority actor, ResolvedViewScope scope, CancellationToken ct = default)
    {
        if (actor.AccountId is not { } accountId || scope.OtherHouseholdId is not { } household)
        {
            throw new InvalidOperationException("Only a signed-in person opens another household's photos.");
        }

        var shared = scope.Kind == ViewScopeKind.Shared;
        var subjectType = shared ? SharedSubject : PersonalSubject;
        var subjectId = (shared ? household : scope.ProfileId ?? throw new InvalidOperationException("Missing person."))
            .ToString("D");
        var key = (accountId, subjectType, subjectId);
        var now = clock.GetUtcNow();
        if (_recent.TryGetValue(key, out var last) && now - last < RecordWindow)
        {
            return;
        }

        // Many thumbnails load at once; only the first of them writes.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_recent.TryGetValue(key, out last) && now - last < RecordWindow)
            {
                return;
            }

            using var connection = database.CreateConnection();
            var latest = connection.ExecuteScalar<string?>(new CommandDefinition("""
                SELECT MAX(occurred_at) FROM authorization_audit_events
                 WHERE event_type = @OpenedEventType AND actor_account_id = @accountId
                   AND subject_type = @subjectType AND subject_id = @subjectId;
                """, new { OpenedEventType, accountId, subjectType, subjectId }, cancellationToken: ct));
            var lastRecorded = latest is null
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(latest, System.Globalization.CultureInfo.InvariantCulture);
            if (lastRecorded is { } recorded && now - recorded < RecordWindow)
            {
                // Remember the real time of the last row, so a look after that hour is still recorded.
                _recent[key] = recorded;
                return;
            }

            await audit.WriteAsync(new AuthorizationAuditEvent(
                OpenedEventType,
                now,
                accountId,
                actor.ActiveProfileId,
                null,
                subjectType,
                subjectId,
                new Dictionary<string, string?>
                {
                    ["household_id"] = household.ToString("D"),
                    ["space"] = shared ? "shared" : "personal",
                }), ct).ConfigureAwait(false);
            _recent[key] = now;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ViewOtherPeopleDto> ListAsync(RequestAuthority caller, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var own = caller.ActiveProfileId is { } active
            ? connection.ExecuteScalar<Guid?>(new CommandDefinition(
                "SELECT household_id FROM profiles WHERE id = @active;", new { active }, cancellationToken: ct))
            : null;
        var rows = connection.Query<PersonRow>(new CommandDefinition("""
            SELECT h.id AS HouseholdId, h.name AS HouseholdName, p.id AS ProfileId, p.display_name AS DisplayName,
                   p.avatar_color AS AvatarColor,
                   EXISTS (SELECT 1 FROM view_personal_spaces s WHERE s.owner_profile_id = p.id) AS HasPersonalSpace
              FROM households h
              JOIN profiles p ON p.household_id = h.id
             WHERE (@own IS NULL OR h.id <> @own)
             ORDER BY h.name COLLATE NOCASE, p.display_name COLLATE NOCASE;
            """, new { own }, cancellationToken: ct)).ToList();
        var households = rows
            .GroupBy(row => (row.HouseholdId, row.HouseholdName))
            .Select(group => new ViewOtherHouseholdDto(group.Key.HouseholdId, group.Key.HouseholdName,
                group.Select(row => new ViewOtherPersonDto(row.ProfileId, row.DisplayName, row.AvatarColor,
                    row.HasPersonalSpace)).ToList()))
            .ToList();
        return Task.FromResult(new ViewOtherPeopleDto(households));
    }

    public Task<ViewPhotoViewsPageDto> ListViewsAsync(RequestAuthority caller, PagedRequest page, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (caller.ActiveProfileId is not { } activeProfile)
        {
            throw new UnauthorizedAccessException();
        }

        using var connection = database.CreateConnection();
        var household = connection.ExecuteScalar<Guid?>(new CommandDefinition(
            "SELECT household_id FROM profiles WHERE id = @activeProfile;", new { activeProfile }, cancellationToken: ct));
        var householdWide = (caller.IsEffectiveHouseholdAdministrator || caller.IsEffectiveAdministrator)
            && household is not null && caller.AccountHouseholdId == household;
        // json_extract compares the household stored on the row, so an event is only ever shown to its own household.
        var rows = connection.Query<ViewRow>(new CommandDefinition("""
            SELECT e.id AS Id, e.occurred_at AS ViewedAt, e.subject_type AS SubjectType, e.subject_id AS SubjectId,
                   vp.display_name AS ViewerName
              FROM authorization_audit_events e
              LEFT JOIN profiles vp ON vp.id = e.actor_profile_id
             WHERE e.event_type = @OpenedEventType
               AND json_extract(e.changes_json, '$.household_id') = @household
               AND (@householdWide = 1 OR (e.subject_type = @personal AND e.subject_id = @own))
             ORDER BY e.occurred_at DESC, e.id DESC
             LIMIT @limit OFFSET @offset;
            """, new
        {
            OpenedEventType,
            personal = PersonalSubject,
            household = household?.ToString("D"),
            householdWide = householdWide ? 1 : 0,
            own = activeProfile.ToString("D"),
            limit = page.Limit + 1,
            offset = page.Offset
        }, cancellationToken: ct)).ToList();

        var names = new Dictionary<string, string>();
        var items = new List<ViewPhotoViewDto>(rows.Count);
        foreach (var row in rows)
        {
            var shared = row.SubjectType == SharedSubject;
            string opened = "Shared Library";
            if (!shared)
            {
                if (!names.TryGetValue(row.SubjectId, out var name))
                {
                    name = Guid.TryParse(row.SubjectId, out var subjectProfile)
                        ? connection.ExecuteScalar<string?>(new CommandDefinition(
                            "SELECT display_name FROM profiles WHERE id = @subjectProfile;",
                            new { subjectProfile }, cancellationToken: ct)) ?? "Someone"
                        : "Someone";
                    names[row.SubjectId] = name;
                }

                opened = $"{name}'s photos";
            }

            items.Add(new ViewPhotoViewDto(row.Id,
                DateTimeOffset.Parse(row.ViewedAt, System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(row.ViewerName) ? "A server administrator" : row.ViewerName,
                opened, shared ? "shared" : "personal"));
        }

        return Task.FromResult(new ViewPhotoViewsPageDto(
            PagedResponse<ViewPhotoViewDto>.FromPage(items, page), householdWide));
    }

    private sealed class PersonRow
    {
        public Guid HouseholdId { get; set; }
        public string HouseholdName { get; set; } = "";
        public Guid ProfileId { get; set; }
        public string DisplayName { get; set; } = "";
        public string? AvatarColor { get; set; }
        public bool HasPersonalSpace { get; set; }
    }

    private sealed class ViewRow
    {
        public long Id { get; set; }
        public string ViewedAt { get; set; } = "";
        public string SubjectType { get; set; } = "";
        public string SubjectId { get; set; } = "";
        public string? ViewerName { get; set; }
    }
}
