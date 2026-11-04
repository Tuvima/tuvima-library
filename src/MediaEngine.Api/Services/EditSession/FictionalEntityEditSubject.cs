using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// A character, place or organisation of a Universe in the universal editor (the Chronicle explorer): its name and description, and a
/// portrait and background. Each field is its own local override (<c>fictional_entity_user_overrides</c>): saving only the name leaves
/// the description following the provider, and the other way round. Appearances, relationships, the timeline and sources
/// come from Wikidata and the works, and stay read-only in the explorer.
/// </summary>
public sealed class FictionalEntityEditSubject(IFictionalEntityRepository entities) : IEditSubject
{
    public const string NameKey = "name";
    public const string DescriptionKey = "description";
    public const string AliasesKey = "aliases";

    /// <summary>The thing it belongs to (a character to a faction, a faction to a place): a choice of the Universe's factions, places and species.</summary>
    public const string BelongsToKey = "belongs_to";

    /// <summary>Whether the thing is shown on the Universe page: <c>shown</c> or <c>hidden</c>.</summary>
    public const string VisibilityKey = "visibility";

    public const string NoParentValue = "none";
    public const string ShownValue = "shown";
    public const string HiddenValue = "hidden";

    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 2000;
    private const int MaxAliasLength = 200;
    private const int MaxAliases = 20;

    public string Kind => EditorSubjectKinds.FictionalEntity;

    public string ArtworkEntityType => "FictionalEntity";

    public async Task<bool> OwnsAsync(Guid id, CancellationToken ct = default) =>
        await entities.FindByIdAsync(id, ct).ConfigureAwait(false) is not null;

    public async Task<EditorSubjectDto?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await entities.FindByIdAsync(id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }

        var facts = new List<EditorSubjectFactDto>();
        void Fact(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add(new EditorSubjectFactDto { Label = label, Value = value.Trim(), SourceLabel = entity.AddedByUser ? "You" : "Wikidata" });
            }
        }

        Fact("Kind", entity.EntitySubType);
        Fact("Universe", entity.FictionalUniverseLabel);
        // A thing added by hand has no Wikidata match; say so instead of showing its local ID (Universe contents spec P4).
        Fact("Wikidata", entity.AddedByUser ? "Not on Wikidata, added by you" : entity.WikidataQid);

        var links = await entities.GetUserLinksAsync(id, ct).ConfigureAwait(false);
        var parentOptions = await BelongsToOptionsAsync(entity, links, ct).ConfigureAwait(false);

        return new EditorSubjectDto
        {
            Id = id,
            Kind = Kind,
            Title = entity.Label,
            ArtworkEntityKind = ArtworkEntityType,
            ArtworkSlots = [.. SlotsFor(entity.EntitySubType)],
            Fields =
            [
                new EditorSubjectFieldDto { Key = NameKey, Label = "Name", InputKind = EditorSubjectInputKinds.Text, Value = entity.Label },
                new EditorSubjectFieldDto { Key = DescriptionKey, Label = "Description", InputKind = EditorSubjectInputKinds.TextArea, Value = entity.Description ?? string.Empty },
                new EditorSubjectFieldDto { Key = AliasesKey, Label = "Also known as (one per line)", InputKind = EditorSubjectInputKinds.TextArea, Value = string.Join('\n', links?.Aliases ?? []) },
                new EditorSubjectFieldDto
                {
                    Key = BelongsToKey,
                    Label = "Belongs to",
                    InputKind = EditorSubjectInputKinds.Choice,
                    Value = links?.BelongsToEntityId is { } parent ? parent.ToString("D") : NoParentValue,
                    CanonicalValue = NoParentValue,
                    Options = parentOptions,
                },
                new EditorSubjectFieldDto
                {
                    Key = VisibilityKey,
                    Label = "On the Universe page",
                    InputKind = EditorSubjectInputKinds.Choice,
                    Value = links?.IsHidden == true ? HiddenValue : ShownValue,
                    CanonicalValue = ShownValue,
                    Options =
                    [
                        new EditorSubjectOptionDto { Value = ShownValue, Label = "Shown" },
                        new EditorSubjectOptionDto { Value = HiddenValue, Label = "Hidden" },
                    ],
                },
            ],
            Facts = facts,
        };
    }

    /// <summary>
    /// What a thing can belong to: the Universe's factions, places and species (other than itself, and never something that already
    /// belongs to it, so the links can never go round in a circle). "Nothing" comes first.
    /// </summary>
    private async Task<List<EditorSubjectOptionDto>> BelongsToOptionsAsync(FictionalEntity entity, FictionalEntityUserLinks? links, CancellationToken ct)
    {
        var options = new List<EditorSubjectOptionDto> { new() { Value = NoParentValue, Label = "Nothing" } };
        if (string.IsNullOrWhiteSpace(entity.FictionalUniverseQid))
        {
            return options;
        }

        var universe = await entities.GetByUniverseAsync(entity.FictionalUniverseQid, ct).ConfigureAwait(false);
        var parents = await entities.GetUserLinksAsync(universe.Select(thing => thing.Id), ct).ConfigureAwait(false);
        var below = DescendantsOf(entity.Id, parents);
        foreach (var candidate in universe
            .Where(thing => thing.Id != entity.Id
                && !below.Contains(thing.Id)
                && (thing.EntitySubType is FictionalEntityType.Organization or FictionalEntityType.Location or FictionalEntityType.Species
                    || thing.Id == links?.BelongsToEntityId))
            .OrderBy(thing => thing.Label, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(new EditorSubjectOptionDto { Value = candidate.Id.ToString("D"), Label = $"{candidate.Label} ({KindName(candidate.EntitySubType)})" });
        }

        return options;
    }

    private static string KindName(string kind) => kind switch
    {
        FictionalEntityType.Organization => "Faction",
        FictionalEntityType.Location => "Place",
        _ => kind,
    };

    // Everything that belongs, directly or through others, to the given thing.
    private static HashSet<Guid> DescendantsOf(Guid id, IReadOnlyDictionary<Guid, FictionalEntityUserLinks> links)
    {
        var found = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (childId, childLinks) in links)
            {
                if (childLinks.BelongsToEntityId == current && found.Add(childId))
                {
                    queue.Enqueue(childId);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The picture slots offered for a kind: every kind has a main picture (stored as <c>CharacterPortrait</c>) and a background;
    /// places, factions, objects and species also have a logo. Characters, events and concepts have none (Universe contents spec WP3).
    /// </summary>
    public static IReadOnlyList<string> SlotsFor(string? entitySubType) => entitySubType switch
    {
        FictionalEntityType.Location or FictionalEntityType.Organization or FictionalEntityType.Object or FictionalEntityType.Species
            => ["CharacterPortrait", "Logo", "Background"],
        _ => ["CharacterPortrait", "Background"],
    };

    public string? CheckField(string key, string? value)
    {
        switch (key.Trim().ToLowerInvariant())
        {
            case NameKey:
                if (string.IsNullOrWhiteSpace(value))
                {
                    return "A name is required.";
                }

                return value.Trim().Length > MaxNameLength ? $"The name can be at most {MaxNameLength} characters." : null;
            case DescriptionKey:
                return (value?.Trim().Length ?? 0) > MaxDescriptionLength ? $"The description can be at most {MaxDescriptionLength} characters." : null;
            case AliasesKey:
                var aliases = SplitAliases(value);
                if (aliases.Count > MaxAliases)
                {
                    return $"At most {MaxAliases} other names can be kept.";
                }

                return aliases.Any(alias => alias.Length > MaxAliasLength) ? $"Each other name can be at most {MaxAliasLength} characters." : null;
            case BelongsToKey:
                return string.Equals(value?.Trim(), NoParentValue, StringComparison.OrdinalIgnoreCase) || Guid.TryParse(value?.Trim(), out _)
                    ? null
                    : "Pick what this belongs to from the list.";
            case VisibilityKey:
                return string.Equals(value?.Trim(), ShownValue, StringComparison.OrdinalIgnoreCase) || string.Equals(value?.Trim(), HiddenValue, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "Choose Shown or Hidden.";
            default:
                return $"'{key.Trim()}' cannot be changed here.";
        }
    }

    public async Task<Func<CancellationToken, Task>> ApplyFieldsAsync(
        Guid id, IReadOnlyList<EditSessionFieldChange> fields, CancellationToken ct = default)
    {
        var before = await entities.FindByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(EditSessionReasons.Gone);

        // Start from the overrides as stored (not the merged values), so a field the person did not touch keeps following the provider.
        var stored = await entities.GetUserDetailsAsync(id, ct).ConfigureAwait(false);
        var label = stored?.Label;
        var description = stored?.Description;
        var storedLinks = await entities.GetUserLinksAsync(id, ct).ConfigureAwait(false);
        var links = storedLinks ?? new FictionalEntityUserLinks([], null, false);
        foreach (var field in fields)
        {
            switch (field.Key.Trim().ToLowerInvariant())
            {
                case NameKey:
                    label = field.Value?.Trim() ?? string.Empty;
                    break;
                case DescriptionKey:
                    // An empty text is kept as an empty text (not null), so it blanks the provider's description instead of falling back to it.
                    description = field.Value?.Trim() ?? string.Empty;
                    break;
                case AliasesKey:
                    links = links with { Aliases = SplitAliases(field.Value) };
                    break;
                case BelongsToKey:
                    links = links with { BelongsToEntityId = await ResolveParentAsync(before, field.Value, ct).ConfigureAwait(false) };
                    break;
                case VisibilityKey:
                    links = links with { IsHidden = string.Equals(field.Value?.Trim(), HiddenValue, StringComparison.OrdinalIgnoreCase) };
                    break;
            }
        }

        await entities.UpdateUserDetailsAsync(id, label, description, ct).ConfigureAwait(false);
        var aliasesChanged = fields.Any(field => field.Key.Trim().ToLowerInvariant() is AliasesKey or BelongsToKey or VisibilityKey);
        if (aliasesChanged)
        {
            await entities.UpdateUserLinksAsync(id, links, ct).ConfigureAwait(false);
        }

        var previousLabel = stored?.Label;
        var previousDescription = stored?.Description;
        return async undoCt =>
        {
            await entities.UpdateUserDetailsAsync(id, previousLabel, previousDescription, undoCt).ConfigureAwait(false);
            if (aliasesChanged)
            {
                await entities.UpdateUserLinksAsync(id, storedLinks ?? new FictionalEntityUserLinks([], null, false), undoCt).ConfigureAwait(false);
            }
        };
    }

    // "none" means no parent. Anything else must be another thing of the same Universe that does not already belong to this one.
    private async Task<Guid?> ResolveParentAsync(FictionalEntity entity, string? value, CancellationToken ct)
    {
        if (string.Equals(value?.Trim(), NoParentValue, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(value?.Trim(), out var parentId))
        {
            return null;
        }

        var parent = await entities.FindByIdAsync(parentId, ct).ConfigureAwait(false);
        if (parent is null || parentId == entity.Id
            || !string.Equals(parent.FictionalUniverseQid, entity.FictionalUniverseQid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("That is not something in this Universe that this can belong to.");
        }

        var universe = await entities.GetByUniverseAsync(entity.FictionalUniverseQid ?? string.Empty, ct).ConfigureAwait(false);
        var parents = await entities.GetUserLinksAsync(universe.Select(thing => thing.Id), ct).ConfigureAwait(false);
        if (DescendantsOf(entity.Id, parents).Contains(parentId))
        {
            throw new InvalidOperationException("That already belongs to this, so this cannot belong to it.");
        }

        return parentId;
    }

    private static List<string> SplitAliases(string? value) =>
        (value ?? string.Empty)
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
