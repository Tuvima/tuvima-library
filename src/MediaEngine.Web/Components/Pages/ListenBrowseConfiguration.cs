using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Ui;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Components.Pages;

internal static class ListenBrowseConfiguration
{
    /// <summary>The id of the Playlists tab (route <c>/listen/playlists</c>): the page of playlist tiles the lane draws itself.</summary>
    public const string PlaylistsTabId = "playlists";

    /// <summary>The Listen tabs. The Playlists tab's words come from the string table, so the page hands in its localizer.</summary>
    public static LibraryBrowsePreset Create(IStringLocalizer<SharedStrings> strings) => new()
    {
        RouteBase = "/listen",
        Title = "Listen",
        HeroVariant = BrowseHeroVariant.Listen,
        UseExplicitDefaultTabRoute = true,
        Tabs =
        [
            new BrowseTabPreset
            {
                Id = "music",
                Label = "Music",
                MediaType = "Music",
                GroupingOptions =
                [
                    new("songs", "Songs", AppMaterialIcons.Outlined.MusicNote),
                    new("albums", "Albums", AppMaterialIcons.Outlined.Album),
                    new("artists", "Artists", AppMaterialIcons.Outlined.PersonOutline),
                    new("timeline", "Timeline", AppMaterialIcons.Outlined.Timeline),
                ],
                DefaultGrouping = "songs",
                DefaultLayout = LibraryLayoutMode.Card,
                YearSemantic = "Original album release year",
                TimelineAggregation = TimelineAggregation.Albums,
                TimelineItemNoun = "album",
            },
            new BrowseTabPreset
            {
                Id = "audiobooks",
                Label = "Audiobooks",
                MediaType = "Audiobooks",
                GroupingOptions =
                [
                    new("all", "Audiobooks", AppMaterialIcons.Outlined.Headphones),
                    new("series", "Series", AppMaterialIcons.Outlined.CollectionsBookmark),
                    new("authors", "Authors", AppMaterialIcons.Outlined.PersonOutline),
                    new("narrators", "Narrators", AppMaterialIcons.Outlined.RecordVoiceOver),
                    new("timeline", "Timeline", AppMaterialIcons.Outlined.Timeline),
                ],
                DefaultGrouping = "all",
                DefaultLayout = LibraryLayoutMode.Card,
                YearSemantic = "Original publication year",
            },
            new BrowseTabPreset
            {
                Id = PlaylistsTabId,
                Label = strings["Glossary_Playlists"].Value,
                MediaType = "Music",
                GroupingOptions =
                [
                    new("playlists", strings["Glossary_Playlists"].Value, AppMaterialIcons.Outlined.QueueMusic),
                ],
                DefaultGrouping = "playlists",
                DefaultLayout = LibraryLayoutMode.Card,
                YearSemantic = strings["ListenBrowsePage_PlaylistsYearSemantic"].Value,
            },
        ],
    };

    /// <summary>Tabs that are not libraries and stay whatever the library list holds; they follow the library tabs.</summary>
    public static IReadOnlySet<string> FixedTabIds { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PlaylistsTabId,
        };
}
