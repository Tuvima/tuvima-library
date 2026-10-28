using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// A provider-built grouping in the universal editor: a Series (spec D17) or a Universe (spec D17a). Their name and description come from
/// the provider, so the editor only ever stores a local override beside them (<c>collections.display_overrides_json</c>, the write the
/// collection display-overrides action makes). The canonical value is never touched, and removing the override puts it back. A Series
/// and a Universe differ only in which collection rows they own and what the source is called.
/// </summary>
public abstract class GroupingEditSubject(
    ICollectionRepository collections,
    CollectionDisplayOverrideRepository overrides) : IEditSubject
{
    public const string TitleKey = "title";
    public const string DescriptionKey = "description";

    public abstract string Kind { get; }

    public string ArtworkEntityType => "Collection";

    protected abstract bool OwnsType(CollectionType type);

    protected abstract string SourceLabel { get; }

    protected abstract IReadOnlyList<string> ArtworkSlots { get; }

    public async Task<bool> OwnsAsync(Guid id, CancellationToken ct = default) =>
        await collections.GetByIdAsync(id, ct).ConfigureAwait(false) is { } collection && OwnsType(collection.CollectionType);

    public async Task<EditorSubjectDto?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        var collection = await collections.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (collection is null || !OwnsType(collection.CollectionType))
        {
            return null;
        }

        var local = await overrides.LoadAsync(id, ct).ConfigureAwait(false) ?? new Dictionary<string, string>();
        EditorSubjectFieldDto Field(string key, string label, string input, string canonical)
        {
            var overridden = local.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
            return new EditorSubjectFieldDto
            {
                Key = key,
                Label = label,
                InputKind = input,
                Value = overridden ? value! : canonical,
                CanonicalValue = canonical,
                IsOverridden = overridden,
                SourceLabel = SourceLabel,
            };
        }

        var title = Field(TitleKey, "Name", EditorSubjectInputKinds.Text, collection.DisplayName ?? string.Empty);
        return new EditorSubjectDto
        {
            Id = id,
            Kind = Kind,
            Title = string.IsNullOrWhiteSpace(title.Value) ? "Untitled" : title.Value,
            ArtworkEntityKind = ArtworkEntityType,
            ArtworkSlots = [.. ArtworkSlots],
            Fields =
            [
                title,
                Field(DescriptionKey, "Description", EditorSubjectInputKinds.TextArea, collection.Description ?? string.Empty),
            ],
        };
    }

    public string? CheckField(string key, string? value) =>
        key.Trim().ToLowerInvariant() is TitleKey or DescriptionKey ? null : $"'{key.Trim()}' cannot be changed here.";

    public async Task<Func<CancellationToken, Task>> ApplyFieldsAsync(
        Guid id, IReadOnlyList<EditSessionFieldChange> fields, CancellationToken ct = default)
    {
        var before = await overrides.LoadAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(EditSessionReasons.Gone);

        var changes = fields.ToDictionary(
            field => field.Key.Trim().ToLowerInvariant(),
            field => field.Value ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
        if (!await overrides.SaveAsync(id, changes, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(EditSessionReasons.Gone);
        }

        // Undo puts back each touched key exactly: its previous local value, or nothing (an empty value removes the override).
        var previous = changes.Keys.ToDictionary(
            key => key,
            key => before.TryGetValue(key, out var value) ? value : string.Empty,
            StringComparer.OrdinalIgnoreCase);
        return async undoCt =>
        {
            await overrides.SaveAsync(id, previous, undoCt).ConfigureAwait(false);
        };
    }
}

/// <summary>A Series (or other provider-built grouping that is not a Universe): a title and description override, and a cover.</summary>
public sealed class SeriesEditSubject(ICollectionRepository collections, CollectionDisplayOverrideRepository overrides)
    : GroupingEditSubject(collections, overrides)
{
    public override string Kind => EditorSubjectKinds.Series;

    protected override string SourceLabel => "Provider";

    protected override IReadOnlyList<string> ArtworkSlots => ["CoverArt", "Background"];

    protected override bool OwnsType(CollectionType type) => type is CollectionType.Series or CollectionType.ContentGroup;
}

/// <summary>A Universe: a name and description override (restorable), a cover and a background. There is no Match for a Universe.</summary>
public sealed class UniverseEditSubject(ICollectionRepository collections, CollectionDisplayOverrideRepository overrides)
    : GroupingEditSubject(collections, overrides)
{
    public override string Kind => EditorSubjectKinds.Universe;

    protected override string SourceLabel => "Wikidata";

    protected override IReadOnlyList<string> ArtworkSlots => ["CoverArt", "Background"];

    protected override bool OwnsType(CollectionType type) => type == CollectionType.Universe;
}
