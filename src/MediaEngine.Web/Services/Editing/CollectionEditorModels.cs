using MediaEngine.Contracts.Details;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Editing;

public sealed class CollectionEditorLaunchRequest
{
    public CollectionListItemViewModel? EditingCollection { get; init; }
    public Guid? ActiveProfileId { get; init; }
    public ContainerEditorKind Kind { get; init; } = ContainerEditorKind.Collection;

    /// <summary>
    /// The way in the New collection flow opens on: "Smart" highlights Set rules (the default), "Manual" highlights
    /// Pick titles. The first step always shows all four; this only chooses which card is ticked.
    /// </summary>
    public string InitialMembershipMode { get; init; } = "Smart";

    public string InitialPrimaryArea { get; init; } = "Mixed";
    public string InitialOwnerKind { get; init; } = "Profile";
    public string InitialAudience { get; init; } = "Private";
    public string? InitialTitle { get; init; }

    /// <summary>The way in the flow opens on instead of the default: "FromList" opens New collection on From a list. Empty uses the default.</summary>
    public string? InitialWay { get; init; }

    /// <summary>A file the shared Import already read (Settings, Profile, Import hands a list over). The flow opens on From a list with it loaded.</summary>
    public ImportRecognition? InitialImport { get; init; }

    /// <summary>
    /// Titles the new collection starts with ("New collection with this title" from an item's "..." menu or from ticked rows). Empty
    /// for an ordinary New collection. When present the flow opens on Pick titles with these already chosen.
    /// </summary>
    public IReadOnlyList<CollectionEditorSeedItem> InitialItems { get; init; } = [];

    /// <summary>The media type chips ticked when the flow opens (for example Movies and TV from Watch). Empty means Everything.</summary>
    public IReadOnlyList<string> InitialMediaTypes { get; init; } = [];
}

/// <summary>A title a New collection starts with: its work id, the name shown in the staged list and its media type.</summary>
public sealed record CollectionEditorSeedItem(Guid WorkId, string Title, string? MediaType = null);

public enum ContainerEditorKind
{
    Collection,
    Playlist,
    Gallery,
}

/// <summary>
/// Where the New collection flow was opened from (Collections and lists spec 4a), and the chips that place pre-ticks:
/// Watch ticks Movies and TV, Read ticks Books and Comics, Listen ticks Music, and the Collections page ticks none.
/// </summary>
public static class CollectionEditorEntryPoints
{
    public const string Collections = "Collections";
    public const string Watch = "Watch";
    public const string Read = "Read";
    public const string Listen = "Listen";

    public static IReadOnlyList<string> MediaTypesFor(string? area) => area switch
    {
        Watch => ["Movies", "TV"],
        Read => ["Books", "Comics"],
        Listen => ["Music"],
        _ => [],
    };

    /// <summary>The launch request for the New collection flow opened from <paramref name="area"/>. Listen makes a playlist.</summary>
    public static CollectionEditorLaunchRequest For(string? area, Guid? activeProfileId) => new()
    {
        ActiveProfileId = activeProfileId,
        Kind = string.Equals(area, Listen, StringComparison.Ordinal) ? ContainerEditorKind.Playlist : ContainerEditorKind.Collection,
        InitialPrimaryArea = area is Watch or Read or Listen ? area : "Mixed",
        InitialMediaTypes = MediaTypesFor(area),
    };
}
