using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Applies the one spoiler rule to the "as Character" credits on title cast lists and person pages: the actor stays, a
/// character the requesting profile has not reached is dropped from the credit. The viewer is the profile of the current
/// request. The admin Universe editor reads are not filtered; they are a correction tool.
/// </summary>
public sealed class CharacterCreditSpoilerFilter(
    CharacterSpoilerService spoilers,
    IHttpContextAccessor http,
    IProfileRepository profiles)
{
    /// <summary>The profile of the current request, or null when there is none.</summary>
    public async Task<Profile?> ViewerAsync(CancellationToken ct = default) =>
        http.HttpContext is { } context
            ? await CollectionClientEndpoints.ResolveActiveProfileAsync(context, profiles, ct).ConfigureAwait(false)
            : null;

    /// <summary>The Character entities among those given that the current viewer has not reached.</summary>
    public async Task<IReadOnlySet<Guid>> HiddenAsync(IEnumerable<Guid> entityIds, CancellationToken ct = default) =>
        await spoilers.HiddenEntityIdsAsync(await ViewerAsync(ct).ConfigureAwait(false), entityIds, ct).ConfigureAwait(false);

    /// <summary>Removes hidden characters from each cast credit (the list is edited in place and returned).</summary>
    public async Task<List<CastCreditDto>> FilterAsync(List<CastCreditDto> credits, CancellationToken ct = default)
    {
        await RemoveHiddenAsync(credits.Select(credit => credit.Characters), ct).ConfigureAwait(false);
        return credits;
    }

    /// <summary>Removes hidden characters from each person-page credit (the list is edited in place and returned).</summary>
    public async Task<List<PersonLibraryCreditDto>> FilterAsync(List<PersonLibraryCreditDto> credits, CancellationToken ct = default)
    {
        await RemoveHiddenAsync(credits.Select(credit => credit.Characters), ct).ConfigureAwait(false);
        return credits;
    }

    private async Task RemoveHiddenAsync(IEnumerable<List<CharacterPortrayalDto>> characterLists, CancellationToken ct)
    {
        var lists = characterLists.Where(list => list.Count > 0).ToList();
        var hidden = await HiddenAsync(lists.SelectMany(list => list).Select(character => character.FictionalEntityId), ct)
            .ConfigureAwait(false);
        if (hidden.Count == 0)
        {
            return;
        }

        foreach (var list in lists)
        {
            list.RemoveAll(character => hidden.Contains(character.FictionalEntityId));
        }
    }
}
