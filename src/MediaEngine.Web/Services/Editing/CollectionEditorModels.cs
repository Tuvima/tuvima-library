using MediaEngine.Contracts.Details;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Editing;

public sealed class CollectionEditorLaunchRequest
{
    public CollectionListItemViewModel? EditingCollection { get; init; }
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
