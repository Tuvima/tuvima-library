using MediaEngine.Contracts.Editing;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// One kind of thing the universal editor edits besides a library title (see <see cref="EditorSubjectKinds"/>): a person, a Series, a
/// curated collection or a Universe. It is the Engine side of that kind's configuration: which Details fields it has, how a field is
/// checked and written, and which kind of picture owner it is. Writing goes through the kind's existing write path (the person editor
/// save, the collection display overrides, the managed collection update), never a second one.
/// </summary>
public interface IEditSubject
{
    /// <summary>The <see cref="EditorSubjectKinds"/> value.</summary>
    string Kind { get; }

    /// <summary>The artwork owner kind the Artwork tool stores pictures under (<c>Person</c>, <c>Collection</c>).</summary>
    string ArtworkEntityType { get; }

    /// <summary>Whether <paramref name="id"/> names a subject of this kind.</summary>
    Task<bool> OwnsAsync(Guid id, CancellationToken ct = default);

    /// <summary>The subject as the editor opens it, or null when it is gone.</summary>
    Task<EditorSubjectDto?> LoadAsync(Guid id, CancellationToken ct = default);

    /// <summary>Checks one field value without writing. Returns null when it can be saved, otherwise the plain-English reason it cannot.</summary>
    string? CheckField(string key, string? value);

    /// <summary>
    /// Writes the fields. An empty value takes a local value away. Throws an <see cref="InvalidOperationException"/> with a plain
    /// sentence when the write is refused. The result puts things back exactly as they were.
    /// </summary>
    Task<Func<CancellationToken, Task>> ApplyFieldsAsync(Guid id, IReadOnlyList<EditSessionFieldChange> fields, CancellationToken ct = default);
}

/// <summary>Finds which <see cref="IEditSubject"/> an id belongs to. A library title is never a subject; the details handler tries titles first.</summary>
public sealed class EditSubjectDirectory(IEnumerable<IEditSubject> subjects)
{
    private readonly IReadOnlyList<IEditSubject> _subjects = subjects.ToList();

    /// <summary>The subject kind that owns <paramref name="id"/>, or null when it is not a person, Series, collection or Universe.</summary>
    public async Task<IEditSubject?> FindAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        foreach (var subject in _subjects)
        {
            if (await subject.OwnsAsync(id, ct).ConfigureAwait(false))
            {
                return subject;
            }
        }

        return null;
    }

    /// <summary>The subject as the editor opens it, or null when <paramref name="id"/> is not one (or it is gone).</summary>
    public async Task<EditorSubjectDto?> LoadAsync(Guid id, CancellationToken ct = default) =>
        await FindAsync(id, ct).ConfigureAwait(false) is { } subject
            ? await subject.LoadAsync(id, ct).ConfigureAwait(false)
            : null;
}
