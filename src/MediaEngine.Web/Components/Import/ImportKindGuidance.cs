using MediaEngine.Contracts.Import;

namespace MediaEngine.Web.Components.Import;

/// <summary>Where a file of each kind goes, said when it is dropped somewhere that cannot use it.</summary>
public static class ImportKindGuidance
{
    public static string WhereItGoes(string kind) => kind switch
    {
        ImportKinds.CollectionList => "Make a collection from it: Collections, New collection, From a list.",
        ImportKinds.PackFile => "Add it from Collections, New collection, Start from a pack.",
        ImportKinds.BookmarkPack => "Add it from the title's player: open it, choose More in the bookmarks panel, then Import.",
        ImportKinds.PluginSettings => "Add it in Settings, Profile, Import.",
        _ => "Library cannot use this file here.",
    };

    public static string KindNoun(string kind) => kind switch
    {
        ImportKinds.CollectionList => "a list",
        ImportKinds.PackFile => "a collection pack",
        ImportKinds.BookmarkPack => "a bookmark pack",
        ImportKinds.PluginSettings => "a plugin settings file",
        _ => "a file",
    };
}
