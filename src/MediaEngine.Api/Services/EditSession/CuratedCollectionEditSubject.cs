using MediaEngine.Api.Models;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// A curated collection someone built, in the universal editor (spec D17): its own name and description, and a cover. It is the person's
/// own, so there is no provider value to fall back to; the fields are written the way the collection update action writes them
/// (<see cref="ICollectionRepository.UpdateManagedCollectionAsync"/>), with the same rule that a collection never loses its last item.
/// Its items and rules are not Details fields and keep their own screen.
/// </summary>
public sealed class CuratedCollectionEditSubject(ICollectionRepository collections) : IEditSubject
{
    public const string NameKey = "title";
    public const string DescriptionKey = "description";

    public const string NameRequired = "A collection needs a name.";

    public string Kind => EditorSubjectKinds.Collection;

    public string ArtworkEntityType => "Collection";

    public async Task<bool> OwnsAsync(Guid id, CancellationToken ct = default) =>
        await collections.GetByIdAsync(id, ct).ConfigureAwait(false) is { } collection
        && CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType);

    public async Task<EditorSubjectDto?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        var collection = await collections.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (collection is null || !CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType))
        {
            return null;
        }

        return new EditorSubjectDto
        {
            Id = id,
            Kind = Kind,
            Title = string.IsNullOrWhiteSpace(collection.DisplayName) ? "Untitled collection" : collection.DisplayName,
            ArtworkEntityKind = ArtworkEntityType,
            ArtworkSlots = ["CoverArt", "Background"],
            Fields =
            [
                new EditorSubjectFieldDto
                {
                    Key = NameKey,
                    Label = "Name",
                    InputKind = EditorSubjectInputKinds.Text,
                    Value = collection.DisplayName ?? string.Empty,
                },
                new EditorSubjectFieldDto
                {
                    Key = DescriptionKey,
                    Label = "Description",
                    InputKind = EditorSubjectInputKinds.TextArea,
                    Value = collection.Description ?? string.Empty,
                },
            ],
        };
    }

    public string? CheckField(string key, string? value) =>
        key.Trim().ToLowerInvariant() switch
        {
            NameKey => string.IsNullOrWhiteSpace(value) ? NameRequired : null,
            DescriptionKey => null,
            _ => $"'{key.Trim()}' cannot be changed here.",
        };

    public async Task<Func<CancellationToken, Task>> ApplyFieldsAsync(
        Guid id, IReadOnlyList<EditSessionFieldChange> fields, CancellationToken ct = default)
    {
        var collection = await collections.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (collection is null || !CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType))
        {
            throw new InvalidOperationException(EditSessionReasons.Gone);
        }

        var previousName = collection.DisplayName;
        var previousDescription = collection.Description;
        foreach (var field in fields)
        {
            switch (field.Key.Trim().ToLowerInvariant())
            {
                case NameKey:
                    collection.DisplayName = field.Value!.Trim();
                    break;
                case DescriptionKey:
                    collection.Description = string.IsNullOrWhiteSpace(field.Value) ? null : field.Value.Trim();
                    break;
            }
        }

        collection.ModifiedAt = DateTimeOffset.UtcNow;
        if (!await collections.UpdateManagedCollectionAsync(collection, [], CollectionAccessPolicy.IsCuratedCollection(collection), ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(CollectionAccessPolicy.CuratedCollectionNeedsItemMessage);
        }

        return async undoCt =>
        {
            var current = await collections.GetByIdAsync(id, undoCt).ConfigureAwait(false);
            if (current is null)
            {
                return;
            }

            current.DisplayName = previousName;
            current.Description = previousDescription;
            current.ModifiedAt = DateTimeOffset.UtcNow;
            await collections.UpdateManagedCollectionAsync(current, [], false, undoCt).ConfigureAwait(false);
        };
    }
}
