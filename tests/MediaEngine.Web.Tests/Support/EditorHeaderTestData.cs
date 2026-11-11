using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// The Engine's editor header (universal editor v5, 3a) for the kinds the header tests draw: an episode of a show, a film with and without
/// supporting files, and a lesson in a course. The numbers (how many files show inline, the page size) are the Engine's, so they are stated
/// here as test data and never read from Dashboard code.
/// </summary>
internal static class EditorHeaderTestData
{
    public static readonly Guid ShowId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    public static readonly Guid SeasonOneId = Guid.Parse("a1000000-0000-0000-0000-0000000000a1");
    public static readonly Guid SeasonTwoId = Guid.Parse("a1000000-0000-0000-0000-0000000000a2");
    public static readonly Guid EpisodeOneId = Guid.Parse("a1000000-0000-0000-0000-0000000000b1");
    public static readonly Guid EpisodeTwoId = Guid.Parse("a1000000-0000-0000-0000-0000000000b2");
    public static readonly Guid EpisodeThreeId = Guid.Parse("a1000000-0000-0000-0000-0000000000b3");
    public static readonly Guid FileOneId = Guid.Parse("a1000000-0000-0000-0000-0000000000f1");
    public static readonly Guid FileTwoId = Guid.Parse("a1000000-0000-0000-0000-0000000000f2");

    public const string ShowTitle = "The Last Signal";

    public static EditorHeaderConfigDto Config(string kind, params string[] tabs) => new()
    {
        Kind = kind,
        InlineSupportingFiles = 2,
        ItemListPageSize = 50,
        Tabs = tabs.Length == 0 ? ["details", "artwork", "match", "history"] : tabs,
        FactsLine = ["episode_number", "runtime", "air_date", "match_state"],
        QuickFacts = ["match_state", "file_quality", "container", "size", "folder", "supporting_file_count"],
    };

    public static EditorNavItemDto Episode(Guid id, string code, string title, string state = EditorNavItemStates.Matched, bool current = false) => new()
    {
        Id = id,
        Kind = "episode",
        Code = code,
        Title = title,
        StillUrl = $"/stills/{id:N}.jpg",
        State = state,
        IsCurrent = current,
    };

    public static EditorHeaderSupportingFileDto SupportingFile(Guid id, string title, string badge, string kind, string role, string? group = null, string? alongside = null) => new()
    {
        Id = id,
        Title = title,
        TypeBadge = badge,
        Kind = kind,
        Role = role,
        RoleLabel = role,
        Group = group,
        ShownAlongside = alongside is null ? null : new EditorHeaderAlongsideDto { Key = "lesson", Label = alongside },
    };

    /// <summary>Season 2, episode 3 of a show: home, a Season dropdown and an item dropdown, with two supporting files shown of twelve.</summary>
    public static EditorHeaderDto TvEpisode(int supportingTotal = 238, bool hasMore = false) => new()
    {
        Kind = EditorKindNames.TvEpisode,
        Root = new EditorHeaderRootDto { Id = ShowId, Title = ShowTitle, MediaType = "TV", CoverUrl = "/covers/show.jpg" },
        CurrentId = EpisodeThreeId,
        Label = ShowTitle,
        ShowHome = true,
        HomeId = ShowId,
        SecondLevel = new EditorHeaderSecondLevelDto
        {
            Label = "Season",
            CurrentId = SeasonTwoId,
            Entries =
            [
                new EditorHeaderLevelEntryDto { Id = SeasonOneId, Title = "Season 1", Number = 1, ItemCount = 10, SupportingFileCount = 4 },
                new EditorHeaderLevelEntryDto { Id = SeasonTwoId, Title = "Season 2", Number = 2, ItemCount = 8, SupportingFileCount = 12 },
            ],
        },
        ShowItemDropdown = true,
        Level = new EditorHeaderLevelDto
        {
            LevelId = SeasonTwoId,
            LevelTitle = "Season 2",
            Total = hasMore ? 120 : 3,
            Offset = 0,
            PageSize = 50,
            HasMore = hasMore,
            Items =
            [
                Episode(EpisodeOneId, "S02E01", "Static", EditorNavItemStates.Matched),
                Episode(EpisodeTwoId, "S02E02", "The Relay", EditorNavItemStates.NotMatched),
                Episode(EpisodeThreeId, "S02E03", "Echoes in the Dark", EditorNavItemStates.Matched, current: true),
            ],
            SupportingFiles = new EditorHeaderSupportingFilesDto
            {
                ScopeKey = "season:2",
                Count = 12,
                TotalInRoot = supportingTotal,
                ShowAll = true,
                Files =
                [
                    SupportingFile(FileOneId, "Episode guide", "PDF", "document", "Guide", group: "Guides"),
                    SupportingFile(FileTwoId, "Script, S02E03", "ZIP", "archive", "Script", group: "Scripts", alongside: "with episode 3"),
                ],
            },
        },
        FolderButton = new EditorHeaderFolderButtonDto { FolderName = "Season 02", PathLabel = "The Last Signal / Season 02" },
        Colors = new EditorCoverColorsDto { PrimaryHex = "#5a3a8a", SecondaryHex = "#2a1858" },
        FileFacts = new EditorFileFactsDto { FileCount = 1, Quality = "1080p", Container = "MKV", SizeBytes = 1_500_000_000, Folder = "Season 02" },
        Config = Config(EditorKindNames.TvEpisode),
    };

    /// <summary>A film on its own: nothing to move around in, so the header has no nav row unless it has supporting files.</summary>
    public static EditorHeaderDto Movie(bool withSupportingFiles = false, bool withBookmarks = false) => new()
    {
        Kind = EditorKindNames.Movie,
        Root = new EditorHeaderRootDto { Id = EpisodeOneId, Title = "Dune: Part One", MediaType = "Movies" },
        CurrentId = EpisodeOneId,
        Label = "Movie",
        LabelIsKind = true,
        ShowHome = false,
        ShowItemDropdown = withSupportingFiles,
        Level = new EditorHeaderLevelDto
        {
            Total = 1,
            PageSize = 50,
            Items = [new EditorNavItemDto { Id = EpisodeOneId, Kind = "movie", Title = "Dune: Part One", IsCurrent = true }],
            SupportingFiles = withSupportingFiles
                ? new EditorHeaderSupportingFilesDto
                {
                    Count = 1,
                    TotalInRoot = 1,
                    Files = [SupportingFile(FileOneId, "Making of", "MP4", "clip", "Extra")],
                }
                : new EditorHeaderSupportingFilesDto(),
        },
        Config = withBookmarks ? Config(EditorKindNames.Movie, "details", "artwork", "match", "bookmarks", "history") : Config(EditorKindNames.Movie, "details", "artwork", "match", "history"),
    };

    /// <summary>Lesson 2 of a course section: a Section dropdown and lessons with numbers and lengths, never season and episode codes.</summary>
    public static EditorHeaderDto CourseLesson() => new()
    {
        Kind = EditorKindNames.Video,
        Root = new EditorHeaderRootDto { Id = ShowId, Title = "Learn Watercolour", MediaType = "Videos" },
        CurrentId = EpisodeTwoId,
        Label = "Learn Watercolour",
        ShowHome = true,
        HomeId = ShowId,
        SecondLevel = new EditorHeaderSecondLevelDto
        {
            Label = "Section",
            CurrentId = SeasonOneId,
            Entries = [new EditorHeaderLevelEntryDto { Id = SeasonOneId, Title = "Washes", Number = 1, ItemCount = 6 }],
        },
        ShowItemDropdown = true,
        Level = new EditorHeaderLevelDto
        {
            LevelId = SeasonOneId,
            LevelTitle = "Washes",
            Total = 2,
            PageSize = 50,
            Items =
            [
                new EditorNavItemDto { Id = EpisodeOneId, Kind = "video", Number = 1, Title = "Flat wash", DurationSeconds = 720, State = EditorNavItemStates.Watched },
                new EditorNavItemDto { Id = EpisodeTwoId, Kind = "video", Number = 2, Title = "Graded wash", DurationSeconds = 840, State = EditorNavItemStates.Unwatched, IsCurrent = true },
            ],
            SupportingFiles = new EditorHeaderSupportingFilesDto(),
        },
        Config = Config(EditorKindNames.Video, "details", "artwork", "history"),
    };
}
