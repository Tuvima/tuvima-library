using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Adds a character, place, faction or other thing by hand to a Universe (universal editor v4, WP11; Universe contents spec P4). The thing
/// gets a local ID in place of a Wikidata ID and the "added by you" mark, providers never touch it and nothing is sent anywhere. The
/// change names the Universe (its editor id) and carries the name, the kind and an optional description; later changes to the new thing
/// (a logo, an alias) are made once it exists. Undo removes the thing again.
/// </summary>
public sealed class ThingAddEditSessionChangeHandler(
    ICollectionRepository collections,
    IFictionalEntityRepository entities,
    IEntityTimelineRepository? timeline = null) : IEditSessionChangeHandler
{
    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 2000;

    public string Kind => EditSessionChangeKinds.ThingAdd;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var universe = await FindUniverseAsync(change.EntityId, ct).ConfigureAwait(false);
        if (universe is null)
        {
            return EditSessionReasons.Gone;
        }

        if (!TryRead(change, out var name, out var section, out var description, out var problem))
        {
            return problem;
        }

        var existing = await entities.GetByUniverseAsync(universe.WikidataQid!, ct).ConfigureAwait(false);
        return existing.Any(thing => thing.EntitySubType == section.Kind && string.Equals(thing.Label, name, StringComparison.OrdinalIgnoreCase))
            ? $"This Universe already has a {EditorUniverseThingsService.SingularOf(section.Key).ToLowerInvariant()} called {name}."
            : null;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var universe = await FindUniverseAsync(change.EntityId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(EditSessionReasons.Gone);
        if (!TryRead(change, out var name, out var section, out var description, out var problem))
        {
            throw new InvalidOperationException(problem);
        }

        var created = await entities.CreateUserEntityAsync(name, section.Kind, universe.WikidataQid!, universe.DisplayName, description, ct).ConfigureAwait(false);
        if (timeline is not null)
        {
            await EditorUniverseHistory.Record(
                timeline, created.Id, EditorSubjectKinds.FictionalEntity, EditorUniverseHistory.AddedEvent,
                $"Added {name} to {universe.DisplayName ?? "the Universe"}.", ct).ConfigureAwait(false);
        }

        return async undoCt => await entities.DeleteUserEntityAsync(created.Id, undoCt).ConfigureAwait(false);
    }

    private async Task<Collection?> FindUniverseAsync(Guid id, CancellationToken ct)
    {
        var universe = await collections.GetByIdAsync(id, ct).ConfigureAwait(false);
        return universe is not null
            && universe.CollectionType == CollectionType.Universe
            && !string.IsNullOrWhiteSpace(universe.WikidataQid)
                ? universe
                : null;
    }

    private static bool TryRead(
        EditSessionChange change,
        out string name,
        out (string Key, string Label, string Kind) section,
        out string? description,
        out string problem)
    {
        string? Field(string key) => change.Fields.FirstOrDefault(field => string.Equals(field.Key.Trim(), key, StringComparison.OrdinalIgnoreCase))?.Value;

        name = (Field(EditSessionThingAdd.NameKey) ?? string.Empty).Trim();
        description = Field(EditSessionThingAdd.DescriptionKey)?.Trim();
        section = default;
        problem = string.Empty;
        if (name.Length == 0)
        {
            problem = "A name is required.";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            problem = $"The name can be at most {MaxNameLength} characters.";
            return false;
        }

        if ((description?.Length ?? 0) > MaxDescriptionLength)
        {
            problem = $"The description can be at most {MaxDescriptionLength} characters.";
            return false;
        }

        var kindKey = (Field(EditSessionThingAdd.KindKey) ?? string.Empty).Trim();
        var match = UniverseContentsReadService.SectionOrder.FirstOrDefault(entry => string.Equals(entry.Key, kindKey, StringComparison.OrdinalIgnoreCase));
        if (match.Key is null)
        {
            problem = "Pick what kind of thing this is.";
            return false;
        }

        section = match;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = null;
        }

        return true;
    }
}
