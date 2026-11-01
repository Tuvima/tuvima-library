namespace MediaEngine.Web.Components.Details;

/// <summary>
/// The one list of detail-page action keys the Dashboard knows how to act on. An action the Engine sends is
/// either a link (it carries a <c>Route</c>) or one of these keys; anything else would be a button that goes
/// nowhere. <c>DetailPage.HandleDetailActionAsync</c> switches on these constants, and a Dashboard test keeps
/// <see cref="Handled"/> equal to the list in the Engine's "every action has a destination" test, so adding a
/// key on one side without the other fails a test.
/// </summary>
public static class DetailActionKeys
{
    public const string PlayFromBeginning = "play-from-beginning";
    public const string Read = "read";
    public const string Listen = "listen";
    public const string PlayAlbum = "play-album";
    public const string Shuffle = "shuffle";
    public const string ShuffleCollection = "shuffle-collection";
    public const string PlayNext = "play-next";
    public const string AddQueue = "add-queue";
    public const string AddPlaylist = "add-playlist";
    public const string StatusComplete = "status-complete";
    public const string StatusReset = "status-reset";
    public const string StatusHide = "status-hide";
    public const string StatusShow = "status-show";
    public const string StatusHistory = "status-history";
    public const string Edit = "edit";
    public const string EditMedia = "edit-media";
    public const string MyList = "my-list";
    public const string ReactionDislike = "reaction-dislike";
    public const string ReactionLike = "reaction-like";
    public const string ReactionLove = "reaction-love";
    public const string Details = "details";
    /// <summary>A video's "..." shortcut into Editing Mode with Match and Identity open (shown only for a video not yet matched).</summary>
    public const string Identify = "identify";
    /// <summary>A video's "..." shortcut into Editing Mode with Manage Artwork open.</summary>
    public const string ChangeThumbnail = "change-thumbnail";
    /// <summary>An administrator's "..." entry for a file the Engine could not match: look it up again now (Library setup spec D34).</summary>
    public const string UnmatchedRetry = "unmatched-retry";
    /// <summary>An administrator's "..." entry for an Unmatched file: stop retrying it and mark it Kept (D40).</summary>
    public const string UnmatchedKeep = "unmatched-keep";
    /// <summary>An administrator's "..." entry for a kept Unmatched file: bring it back and look it up now (D40).</summary>
    public const string UnmatchedLookUpAgain = "unmatched-look-up-again";
    /// <summary>Added by the Dashboard itself to Editing Mode's More list, never sent by the Engine; <c>DetailPage</c> opens the delete confirmation.</summary>
    public const string DeleteFromLibrary = "delete-from-library";

    /// <summary>
    /// Added by the Dashboard itself to a single title's "..." menu for a profile that may make collections (every adult profile,
    /// on a desktop); never sent by the Engine. <c>DetailPage</c> opens the "Add to collection" list.
    /// </summary>
    public const string AddToCollection = "add-collection";

    /// <summary>Added by the Dashboard itself to a show page's "..." menu while a season is selected ("Delete Season 2…"); never sent by the Engine.</summary>
    public const string DeleteSeason = "delete-season";

    /// <summary>Added by the Dashboard itself to a set's "..." menu for a person who may edit it ("Remove set…"); never sent by the Engine.</summary>
    public const string RemoveSet = "remove-set";

    /// <summary>Added by the Dashboard itself to a set's "..." menu ("Export as a pack file"); never sent by the Engine.</summary>
    public const string ExportSetPack = "export-set-pack";

    /// <summary>Every key <c>DetailPage</c> handles itself (in handler order); see the class summary.</summary>
    public static IReadOnlyList<string> Handled { get; } =
    [
        PlayFromBeginning,
        Read,
        Listen,
        PlayAlbum,
        Shuffle,
        ShuffleCollection,
        PlayNext,
        AddQueue,
        AddPlaylist,
        StatusComplete,
        StatusReset,
        StatusHide,
        StatusShow,
        StatusHistory,
        Edit,
        EditMedia,
        MyList,
        ReactionDislike,
        ReactionLike,
        ReactionLove,
        Details,
        Identify,
        ChangeThumbnail,
        DeleteFromLibrary,
        DeleteSeason,
        AddToCollection,
        RemoveSet,
        ExportSetPack,
        UnmatchedRetry,
        UnmatchedKeep,
        UnmatchedLookUpAgain,
    ];

    /// <summary>True for the administrator's Unmatched actions; they are not edits, so a device that cannot edit still shows them.</summary>
    public static bool IsUnmatchedAction(string key) => key is UnmatchedRetry or UnmatchedKeep or UnmatchedLookUpAgain;

    /// <summary>True for the personal-status actions (mark complete or reset, hide or show in Continue, history).</summary>
    public static bool IsPersonalStatus(string key) => key is StatusComplete or StatusReset or StatusHide or StatusShow or StatusHistory;
}
