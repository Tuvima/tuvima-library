using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// A person or group in the universal editor (spec D17): display name, biography, sort name and library tags as local overrides, with
/// the provider's facts beside them, read-only. It saves through <see cref="PersonEditorReadService.SaveAsync"/>, the same write the old
/// person pop-up used, so the revision check and the activity record are the ones that always applied.
/// </summary>
public sealed class PersonEditSubject(
    IPersonRepository persons,
    PersonEditorReadService editor) : IEditSubject
{
    public const string NameKey = "name";
    public const string BiographyKey = "biography";
    public const string SortNameKey = "sort_name";
    public const string TagsKey = "custom_tags";

    public const string ChangedMeanwhile = "This person changed while you were editing. Close the editor and try again.";

    public string Kind => EditorSubjectKinds.Person;

    public string ArtworkEntityType => "Person";

    public async Task<bool> OwnsAsync(Guid id, CancellationToken ct = default) =>
        await persons.FindByIdAsync(id, ct).ConfigureAwait(false) is not null;

    public async Task<EditorSubjectDto?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        var person = await persons.FindByIdAsync(id, ct).ConfigureAwait(false);
        var state = await editor.GetAsync(id, null, ct).ConfigureAwait(false);
        if (person is null || state is null)
        {
            return null;
        }

        var overrides = state.DisplayOverrides;
        EditorSubjectFieldDto Text(string key, string label, string input, string canonical)
        {
            var overridden = overrides.TryGetValue(key, out var local) && !string.IsNullOrWhiteSpace(local);
            return new EditorSubjectFieldDto
            {
                Key = key,
                Label = label,
                InputKind = input,
                Value = overridden ? local! : canonical,
                CanonicalValue = canonical,
                IsOverridden = overridden,
                SourceLabel = "Provider",
            };
        }

        var facts = new List<EditorSubjectFactDto>();
        void Fact(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add(new EditorSubjectFactDto { Label = label, Value = value.Trim(), SourceLabel = "Provider" });
            }
        }

        Fact("Occupation", person.Occupation);
        Fact("Born", person.DateOfBirth);
        Fact("Nationality", person.Nationality);
        Fact("Wikidata", person.WikidataQid);

        return new EditorSubjectDto
        {
            Id = id,
            Kind = Kind,
            Title = overrides.TryGetValue(NameKey, out var name) && !string.IsNullOrWhiteSpace(name) ? name : state.BaselineName,
            ArtworkEntityKind = ArtworkEntityType,
            ArtworkSlots = ["Headshot"],
            Fields =
            [
                Text(NameKey, "Display name", EditorSubjectInputKinds.Text, state.BaselineName),
                Text(BiographyKey, "Biography", EditorSubjectInputKinds.TextArea, state.BaselineBiography ?? string.Empty),
                Text(SortNameKey, "Sort name", EditorSubjectInputKinds.Text, BuildSortName(state.BaselineName)),
                new EditorSubjectFieldDto
                {
                    Key = TagsKey,
                    Label = "Library tags",
                    InputKind = EditorSubjectInputKinds.Chips,
                    Value = string.Join("; ", state.LocalTags),
                },
            ],
            Facts = facts,
        };
    }

    public string? CheckField(string key, string? value)
    {
        switch (key.Trim().ToLowerInvariant())
        {
            case NameKey or BiographyKey or SortNameKey:
                return null;
            case TagsKey:
                return LibraryTagCatalog.TryNormalizeDisplayValue(value, out _, out var error) ? null : error;
            default:
                return $"'{key.Trim()}' cannot be changed for a person.";
        }
    }

    public async Task<Func<CancellationToken, Task>> ApplyFieldsAsync(
        Guid id, IReadOnlyList<EditSessionFieldChange> fields, CancellationToken ct = default)
    {
        var before = await editor.GetAsync(id, null, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(EditSessionReasons.Gone);

        var overrides = new Dictionary<string, string>(before.DisplayOverrides, StringComparer.OrdinalIgnoreCase);
        var tags = before.LocalTags.ToList();
        foreach (var field in fields)
        {
            var key = field.Key.Trim().ToLowerInvariant();
            if (key == TagsKey)
            {
                tags = LibraryTagCatalog.ParseDisplayValue(field.Value).ToList();
            }
            else if (string.IsNullOrWhiteSpace(field.Value))
            {
                overrides.Remove(key);
            }
            else
            {
                overrides[key] = field.Value.Trim();
            }
        }

        var saved = await editor.SaveAsync(id, new PersonEditorSaveRequest
        {
            ExpectedRevision = before.Revision,
            DisplayOverrides = overrides,
            LocalTags = tags,
        }, ct).ConfigureAwait(false);
        if (!saved.Saved)
        {
            throw new InvalidOperationException(ChangedMeanwhile);
        }

        return async undoCt =>
        {
            var current = await editor.GetAsync(id, null, undoCt).ConfigureAwait(false);
            if (current is null)
            {
                return;
            }

            await editor.SaveAsync(id, new PersonEditorSaveRequest
            {
                ExpectedRevision = current.Revision,
                DisplayOverrides = new Dictionary<string, string>(before.DisplayOverrides, StringComparer.OrdinalIgnoreCase),
                LocalTags = before.LocalTags.ToList(),
            }, undoCt).ConfigureAwait(false);
        };
    }

    // "Frank Herbert" sorts as "Herbert, Frank" (the same rule the old person pop-up showed as the default).
    internal static string BuildSortName(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? name : $"{parts[^1]}, {string.Join(" ", parts[..^1])}";
    }
}
