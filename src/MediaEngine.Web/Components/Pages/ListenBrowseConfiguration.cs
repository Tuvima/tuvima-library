using MediaEngine.Web.Models.ViewDTOs;
using MudBlazor;

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
                    new("songs", "Songs", Icons.Material.Outlined.MusicNote),
                    new("albums", "Albums", Icons.Material.Outlined.Album),
                    new("artists", "Artists", Icons.Material.Outlined.PersonOutline),
                    new("timeline", "Timeline", Icons.Material.Outlined.Timeline),
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
                    new("all", "Audiobooks", Icons.Material.Outlined.Headphones),
                    new("series", "Series", Icons.Material.Outlined.CollectionsBookmark),
                    new("authors", "Authors", Icons.Material.Outlined.PersonOutline),
                    new("narrators", "Narrators", Icons.Material.Outlined.RecordVoiceOver),
                    new("timeline", "Timeline", Icons.Material.Outlined.Timeline),
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
                    new("playlists", "Playlists", Icons.Material.Outlined.QueueMusic),
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
