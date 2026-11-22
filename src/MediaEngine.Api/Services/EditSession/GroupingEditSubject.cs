using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
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

    /// <summary>More Details fields a kind has beside the name and description (a Series' sort title and tags).</summary>
    protected virtual IReadOnlyList<EditorSubjectFieldDto> ExtraFields(IReadOnlyDictionary<string, string> local) => [];

    /// <summary>The header's match badge for the grouping; null for a kind that has none.</summary>
    protected virtual Task<EditorSubjectMatchBadgeDto?> LoadMatchBadgeAsync(Collection collection, CancellationToken ct) =>
        Task.FromResult<EditorSubjectMatchBadgeDto?>(null);

    /// <summary>The kinds of title the grouping holds, for the header's first dropdown; none by default.</summary>
    protected virtual Task<List<EditorSeriesMediaTypeDto>> LoadMediaTypesAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<List<EditorSeriesMediaTypeDto>>([]);

    /// <summary>Puts a value into the form it is stored in (a Series' tags are normalized). The default keeps it as typed.</summary>
    protected virtual string Normalize(string key, string? value) => value ?? string.Empty;

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
                .. ExtraFields(local),
            ],
            Members = await LoadMembersAsync(id, ct).ConfigureAwait(false),
            MatchBadge = await LoadMatchBadgeAsync(collection, ct).ConfigureAwait(false),
            MediaTypes = await LoadMediaTypesAsync(id, ct).ConfigureAwait(false),
        };
    }

    /// <summary>The fields a person may write; a Series takes its sort title and tags only, and a Universe its name and description overrides.</summary>
    protected virtual bool CanWrite(string key) => key is TitleKey or DescriptionKey;

    /// <summary>The plain reason a field the kind could otherwise take is refused (null for the generic "cannot be changed here").</summary>
    protected virtual string? RefusalFor(string key) => null;

    public virtual string? CheckField(string key, string? value)
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
            field => Normalize(field.Key.Trim().ToLowerInvariant(), field.Value),
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
/// A Series (or other provider-built grouping that is not a Universe). Editable only for what is the owner's own (universal editor v8 D5,
/// which amends v5 D7 and D24): its cover, background and logo, its tags and its sort title, kept as the Series' own local values
/// (<c>collections.display_overrides_json</c>, the same write a Universe's overrides use). Its name and description come from Wikidata and
/// its members' own matches, so they and its order and members stay locked: a write to them is refused with a plain reason.
/// </summary>
public sealed class SeriesEditSubject(
    ICollectionRepository collections,
    CollectionDisplayOverrideRepository overrides,
    EditorSeriesReadService? series = null)
    : GroupingEditSubject(collections, overrides)
{
    /// <summary>The key a Series' sort title is stored under: the same key a title's own sort title uses.</summary>
    public const string SortTitleKey = "sort_title";

    /// <summary>The key a Series' library tags are stored under: the same key a title's own tags use.</summary>
    public const string TagsKey = "custom_tags";

    public override string Kind => EditorSubjectKinds.Series;

    protected override string SourceLabel => "Provider";

    protected override IReadOnlyList<string> ArtworkSlots => ["CoverArt", "Background", "Logo"];

    protected override IReadOnlyList<EditorSubjectFieldDto> ExtraFields(IReadOnlyDictionary<string, string> local) =>
    [
        OwnField(local, SortTitleKey, "Sort title", EditorSubjectInputKinds.Text),
        OwnField(local, TagsKey, "Tags", EditorSubjectInputKinds.Chips),
    ];

    // The owner's own fields hold nothing a provider fills, so there is no canonical value to go back to and no lock.
    private static EditorSubjectFieldDto OwnField(IReadOnlyDictionary<string, string> local, string key, string label, string input)
    {
        var value = local.TryGetValue(key, out var stored) ? stored : string.Empty;
        return new EditorSubjectFieldDto
        {
            Key = key,
            Label = label,
            InputKind = input,
            Value = value,
            CanonicalValue = string.Empty,
            IsOverridden = !string.IsNullOrWhiteSpace(value),
        };
    }

    // The Series' works in their order (the number the Series gives each, then publication date, then title), each naming the rule that placed it,
    // so the editor can list them and open any one.
    protected override async Task<List<EditorSubjectMemberDto>> LoadMembersAsync(Guid id, CancellationToken ct)
    {
        var full = await Collections.GetCollectionWithWorksAsync(id, ct).ConfigureAwait(false);
        return EditorSeriesOrder.Place((full?.Works ?? []).Select(EditorSeriesOrder.EntryOf));
    }

    protected override async Task<EditorSubjectMatchBadgeDto?> LoadMatchBadgeAsync(Collection collection, CancellationToken ct) =>
        series is null ? null : await series.ReadBadgeAsync(collection.Id, collection.WikidataQid, ct).ConfigureAwait(false);

    protected override async Task<List<EditorSeriesMediaTypeDto>> LoadMediaTypesAsync(Guid id, CancellationToken ct) =>
        series is null ? [] : await series.ReadMediaTypesAsync(id, ct).ConfigureAwait(false);

    protected override bool CanWrite(string key) => key is SortTitleKey or TagsKey;

    protected override string? RefusalFor(string key) => EditSessionReasons.SeriesIsReadOnly;

    public override string? CheckField(string key, string? value)
    {
        if (base.CheckField(key, value) is { } refusal)
        {
            return refusal;
        }

        return DetailDisplayOverrideCatalog.TryValidateValue(key.Trim(), value, out _, out var error) ? null : error ?? $"'{key.Trim()}' cannot be changed here.";
    }

    protected override string Normalize(string key, string? value) =>
        DetailDisplayOverrideCatalog.TryValidateValue(key, value, out var normalized, out _) ? normalized : value ?? string.Empty;

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
