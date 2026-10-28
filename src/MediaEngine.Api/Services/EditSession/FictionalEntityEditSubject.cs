using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// A character, place or organisation of a Universe in the universal editor (the Chronicle explorer): its name and description, and a
/// portrait and background. Name and description are stored together as one local pair (<c>fictional_entity_user_overrides</c>), the
/// write the old shared entity editor made, so there is no per-field "reset" here. Appearances, relationships, the timeline and sources
/// come from Wikidata and the works, and stay read-only in the explorer.
/// </summary>
public sealed class FictionalEntityEditSubject(IFictionalEntityRepository entities) : IEditSubject
{
    public const string NameKey = "name";
    public const string DescriptionKey = "description";

    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 2000;

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
                facts.Add(new EditorSubjectFactDto { Label = label, Value = value.Trim(), SourceLabel = "Wikidata" });
            }
        }

        Fact("Kind", entity.EntitySubType);
        Fact("Universe", entity.FictionalUniverseLabel);
        Fact("Wikidata", entity.WikidataQid);

        return new EditorSubjectDto
        {
            Id = id,
            Kind = Kind,
            Title = entity.Label,
            ArtworkEntityKind = ArtworkEntityType,
            ArtworkSlots = ["CharacterPortrait", "Background"],
            Fields =
            [
                new EditorSubjectFieldDto { Key = NameKey, Label = "Name", InputKind = EditorSubjectInputKinds.Text, Value = entity.Label },
                new EditorSubjectFieldDto { Key = DescriptionKey, Label = "Description", InputKind = EditorSubjectInputKinds.TextArea, Value = entity.Description ?? string.Empty },
            ],
            Facts = facts,
        };
    }

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
            default:
                return $"'{key.Trim()}' cannot be changed here.";
        }
    }

    public async Task<Func<CancellationToken, Task>> ApplyFieldsAsync(
        Guid id, IReadOnlyList<EditSessionFieldChange> fields, CancellationToken ct = default)
    {
        var before = await entities.FindByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(EditSessionReasons.Gone);

        var label = before.Label;
        var description = before.Description;
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
            }
        }

        await entities.UpdateUserDetailsAsync(id, label, description, ct).ConfigureAwait(false);

        var previousLabel = before.Label;
        var previousDescription = before.Description;
        return async undoCt =>
        {
            await entities.UpdateUserDetailsAsync(id, previousLabel, previousDescription, undoCt).ConfigureAwait(false);
        };
    }
}
