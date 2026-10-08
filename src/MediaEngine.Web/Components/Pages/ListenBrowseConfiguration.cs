using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Pages;

internal static class ListenBrowseConfiguration
{
    public static readonly LibraryBrowsePreset Preset = new()
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
                Id = "playlists",
                Label = "Playlists",
                MediaType = "Music",
                GroupingOptions =
                [
                    new("playlists", "Playlists", AppMaterialIcons.Outlined.QueueMusic),
                ],
                DefaultGrouping = "playlists",
                DefaultLayout = LibraryLayoutMode.Card,
                YearSemantic = "Playlist update year",
            },
        ],
    };

    public static IReadOnlyList<MediaHubModeViewModel> LaneModes { get; } =
        MediaLaneConfigurationBuilder.BuildModes(Preset);

    public static IReadOnlySet<string> LibraryRailTabIds { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "music",
            "audiobooks",
        };
}
