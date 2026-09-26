using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Services.Editing;

public sealed class CollectionEditorLaunchRequest
{
    public CollectionListItemViewModel? EditingCollection { get; init; }
    // Automatic shelves retain their canonical parent and membership; they are not curated collections.
    public DetailPageViewModel? StructuralDetail { get; init; }
    public Guid? ActiveProfileId { get; init; }
    public ContainerEditorKind Kind { get; init; } = ContainerEditorKind.Collection;
    public string InitialMembershipMode { get; init; } = "Manual";
    public string InitialPrimaryArea { get; init; } = "Mixed";
    public string InitialOwnerKind { get; init; } = "Profile";
    public string InitialAudience { get; init; } = "Private";
    public string? InitialTitle { get; init; }
}

public enum ContainerEditorKind
{
    Collection,
    Playlist,
    Gallery,
}
