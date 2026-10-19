using System.Globalization;
using MediaEngine.AI.Configuration;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Taste;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Taste;

public enum PersonalTasteFailure
{
    None,
    BadRequest,
    Forbidden,
    NotFound,
}

/// <summary>A successful answer, or the reason it was refused and the sentence to show.</summary>
public sealed record PersonalTasteResult<T>(T? Value, PersonalTasteFailure Failure, string? Message)
{
    public bool IsSuccess => Failure == PersonalTasteFailure.None;

    public static PersonalTasteResult<T> Ok(T value) => new(value, PersonalTasteFailure.None, null);

    public static PersonalTasteResult<T> Fail(PersonalTasteFailure failure, string message) => new(default, failure, message);
}

/// <summary>
/// The Taste page's reads and edits. The Engine decides whose taste the caller may see through
/// <see cref="IPersonalScopeAuthorizer"/> (which also records the viewing of other people's taste); only the signed-in
/// person may change their own taste, and nobody changes anyone else's. The learned profile is never rewritten: removals
/// and additions are stored separately and applied here on read.
/// </summary>
public sealed class PersonalTasteService(
    IPersonalScopeAuthorizer scopes,
    ITasteProfiler profiler,
    ITasteOverrideRepository overrides,
    IProfileRepository profiles,
    AiSettings aiSettings,
    TimeProvider clock)
{
    private const int GenreSuggestionLimit = 60;

    public async Task<PersonalTasteResult<TasteViewDto>> GetAsync(RequestAuthority authority, string? scope, CancellationToken ct)
    {
        var parsed = ParseScope(scope);
        if (parsed is null)
        {
            return PersonalTasteResult<TasteViewDto>.Fail(
                PersonalTasteFailure.BadRequest, "The scope is me, profile:{id}, household:{id} or everyone.");
        }

        var decision = await scopes.AuthorizeAsync(authority, PersonalScopeKind.Taste, parsed.Value.Scope, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            return Refused(decision);
        }

        var active = authority.ActiveProfileId;
        var people = (await profiles.GetAllAsync(ct).ConfigureAwait(false))
            .Where(profile => decision.AllProfiles || decision.ProfileIds.Contains(profile.Id))
            .OrderByDescending(profile => profile.Id == active)
            .ThenBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var changes = await overrides.ListAsync(
            decision.AllProfiles ? null : decision.ProfileIds, ct).ConfigureAwait(false);

        var view = new TasteViewDto { Scope = parsed.Value.Text, IncludesOthers = decision.IncludesOthers };
        foreach (var profile in people)
        {
            var learned = await profiler.GetProfileAsync(profile.Id, ct).ConfigureAwait(false);
            var taste = learned.Status == TasteProfileBuildStatus.Generated ? learned.Profile : null;
            var own = changes.Where(change => change.ProfileId == profile.Id).ToList();
            view.People.Add(new TastePersonDto
            {
                ProfileId = profile.Id.ToString("D"),
                Person = decision.IncludesOthers ? profile.DisplayName : null,
                IsYou = profile.Id == active,
                Genres = TasteOverrides.Items(taste?.GenreDistribution, own, TasteGroups.Genre),
                Moods = TasteOverrides.Items(taste?.MoodPreferences, own, TasteGroups.Mood),
                Eras = TasteOverrides.Items(taste?.EraPreferences, own, TasteGroups.Era),
                MediaMix = taste is null ? [] : new Dictionary<string, double>(taste.MediaTypeMix),
                Summary = taste?.Summary,
                LastUpdatedAt = taste?.LastUpdatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            });
        }

        view.Suggestions = await SuggestionsAsync(ct).ConfigureAwait(false);
        return PersonalTasteResult<TasteViewDto>.Ok(view);
    }

    public Task<PersonalTasteResult<bool>> AddAsync(RequestAuthority authority, string? group, string? value, CancellationToken ct) =>
        ChangeAsync(authority, group, value, TasteOverrideActions.Added, ct);

    public Task<PersonalTasteResult<bool>> RemoveAsync(RequestAuthority authority, string? group, string? value, CancellationToken ct) =>
        ChangeAsync(authority, group, value, TasteOverrideActions.Removed, ct);

    private async Task<PersonalTasteResult<bool>> ChangeAsync(
        RequestAuthority authority, string? group, string? value, string action, CancellationToken ct)
    {
        // Only a signed-in person edits, and only their own taste. A paired app acts for a person but never edits their taste.
        if (authority.PrincipalKind != PrincipalKind.Human ||
            authority.ActiveProfileId is not { } profileId ||
            !scopes.CanEditTaste(authority, profileId))
        {
            return PersonalTasteResult<bool>.Fail(PersonalTasteFailure.Forbidden, "You can only change your own taste.");
        }

        if (TasteOverrides.ToStoredGroup(group) is not { } stored)
        {
            return PersonalTasteResult<bool>.Fail(PersonalTasteFailure.BadRequest, "A group is genres, moods or eras.");
        }

        if (TasteOverrides.Normalize(stored, value) is not { } normalized)
        {
            return PersonalTasteResult<bool>.Fail(
                PersonalTasteFailure.BadRequest,
                stored == TasteGroups.Era
                    ? "An era is a decade such as 1990s."
                    : $"A value is 1 to {TasteOverrides.MaxValueLength} characters.");
        }

        await overrides.SetAsync(profileId, stored, normalized, action, clock.GetUtcNow(), ct).ConfigureAwait(false);
        return PersonalTasteResult<bool>.Ok(true);
    }

    private async Task<TasteSuggestionsDto> SuggestionsAsync(CancellationToken ct)
    {
        var vocabulary = aiSettings.VibeVocabulary;
        var moods = vocabulary.Books
            .Concat(vocabulary.MoviesTv).Concat(vocabulary.Music).Concat(vocabulary.Comics)
            .Select(mood => mood.Trim().ToLowerInvariant())
            .Where(mood => mood.Length > 0)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        return new TasteSuggestionsDto
        {
            Genres = [.. await overrides.ListKnownGenresAsync(GenreSuggestionLimit, ct).ConfigureAwait(false)],
            Moods = moods,
            Eras = TasteOverrides.DecadeSuggestions(clock.GetUtcNow().Year),
        };
    }

    private static PersonalTasteResult<TasteViewDto> Refused(PersonalScopeDecision decision) => decision.Refusal switch
    {
        PersonalScopeRefusal.UnknownTarget => PersonalTasteResult<TasteViewDto>.Fail(
            PersonalTasteFailure.NotFound, "That person or household was not found."),
        PersonalScopeRefusal.UnlockRequired => PersonalTasteResult<TasteViewDto>.Fail(
            PersonalTasteFailure.Forbidden, "Unlock administrator access to see other people's taste."),
        PersonalScopeRefusal.NotSignedIn => PersonalTasteResult<TasteViewDto>.Fail(
            PersonalTasteFailure.Forbidden, "Sign in as a person to see taste."),
        _ => PersonalTasteResult<TasteViewDto>.Fail(PersonalTasteFailure.Forbidden, "You can't see that taste."),
    };

    private static (PersonalScope Scope, string Text)? ParseScope(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            return (PersonalScope.Self, "me");
        }

        if (value.Equals("everyone", StringComparison.OrdinalIgnoreCase))
        {
            return (PersonalScope.Everyone, "everyone");
        }

        var split = value.IndexOf(':', StringComparison.Ordinal);
        if (split > 0 && Guid.TryParse(value[(split + 1)..], out var id) && id != Guid.Empty)
        {
            switch (value[..split].ToLowerInvariant())
            {
                case "profile":
                    return (PersonalScope.Profile(id), $"profile:{id:D}");
                case "household":
                    return (PersonalScope.Household(id), $"household:{id:D}");
            }
        }

        return null;
    }
}
