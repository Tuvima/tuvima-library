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

    /// <summary>The members the editor lists for the grouping (a Series lists its works in order); none by default.</summary>
    protected virtual Task<List<EditorSubjectMemberDto>> LoadMembersAsync(Guid id, CancellationToken ct) => Task.FromResult<List<EditorSubjectMemberDto>>([]);

    protected ICollectionRepository Collections => collections;

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
            Members = await LoadMembersAsync(id, ct).ConfigureAwait(false),
        };
    }

    public virtual bool ReadOnly => false;

    /// <summary>The fields a person may write; a Series takes none and a Universe only its description override.</summary>
    protected virtual bool CanWrite(string key) => key is TitleKey or DescriptionKey;

    /// <summary>The plain reason a field the kind could otherwise take is refused (null for the generic "cannot be changed here").</summary>
    protected virtual string? RefusalFor(string key) => null;

    public string? CheckField(string key, string? value)
    {
        var normalized = key.Trim().ToLowerInvariant();
        return CanWrite(normalized) ? null : RefusalFor(normalized) ?? $"'{key.Trim()}' cannot be changed here.";
    }

    public async Task<Func<CancellationToken, Task>> ApplyFieldsAsync(
        Guid id, IReadOnlyList<EditSessionFieldChange> fields, CancellationToken ct = default)
    {
        if (fields.Select(field => CheckField(field.Key, field.Value)).FirstOrDefault(reason => reason is not null) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

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

/// <summary>
/// A Series (or other provider-built grouping that is not a Universe). It is read-only (universal editor v5 D7, D24): its name, order and
/// membership come from its members' own matches, so every field write is refused, and it has no picture of its own (its tile is the
/// automatic stack of its members' covers).
/// </summary>
public sealed class SeriesEditSubject(ICollectionRepository collections, CollectionDisplayOverrideRepository overrides)
    : GroupingEditSubject(collections, overrides)
{
    public override string Kind => EditorSubjectKinds.Series;

    protected override string SourceLabel => "Provider";

    protected override IReadOnlyList<string> ArtworkSlots => [];

    public override bool ReadOnly => true;

    // The Series' works in their order (the number the Series gives each, then title), so the editor can list them and open any one.
    protected override async Task<List<EditorSubjectMemberDto>> LoadMembersAsync(Guid id, CancellationToken ct)
    {
        var full = await Collections.GetCollectionWithWorksAsync(id, ct).ConfigureAwait(false);
        return
        [
            .. (full?.Works ?? [])
                .Select(work => new EditorSubjectMemberDto
                {
                    Id = work.Id,
                    Title = work.CanonicalValues
                        .Where(value => string.Equals(value.Key, "title", StringComparison.OrdinalIgnoreCase))
                        .Select(value => value.Value)
                        .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty,
                    Number = work.Ordinal,
                })
                .Where(member => member.Title.Length > 0)
                .OrderBy(member => member.Number ?? int.MaxValue)
                .ThenBy(member => member.Title, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    protected override bool CanWrite(string key) => false;

    protected override string? RefusalFor(string key) => EditSessionReasons.SeriesIsReadOnly;

    protected override bool OwnsType(CollectionType type) => type is CollectionType.Series or CollectionType.ContentGroup;
}

/// <summary>
/// A Universe: mostly read-only (universal editor v5 D7). It takes a name and description override (restorable, the provider's value is never
/// touched), a cover and a background. There is no Match for a Universe.
/// </summary>
public sealed class UniverseEditSubject(ICollectionRepository collections, CollectionDisplayOverrideRepository overrides)
    : GroupingEditSubject(collections, overrides)
{
    public override string Kind => EditorSubjectKinds.Universe;

    protected override string SourceLabel => "Wikidata";

    protected override IReadOnlyList<string> ArtworkSlots => ["CoverArt", "Background"];

    protected override bool OwnsType(CollectionType type) => type == CollectionType.Universe;
}
