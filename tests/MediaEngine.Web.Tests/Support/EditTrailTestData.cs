using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// One show (two seasons, three episodes) and one album (two tracks), as the detail page and the editor describe them, for the tests of the
/// item trail and of switching between items while editing (universal editor spec D7).
/// </summary>
public static class EditTrailTestData
{
    public static readonly Guid ShowId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid SeasonOneId = Guid.Parse("a0000000-0000-0000-0000-000000000011");
    public static readonly Guid SeasonTwoId = Guid.Parse("a0000000-0000-0000-0000-000000000012");
    public static readonly Guid EpisodeOneId = Guid.Parse("a0000000-0000-0000-0000-000000000101");
    public static readonly Guid EpisodeTwoId = Guid.Parse("a0000000-0000-0000-0000-000000000102");
    public static readonly Guid EpisodeThreeId = Guid.Parse("a0000000-0000-0000-0000-000000000103");

    public static readonly Guid ArtistId = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    public static readonly Guid AlbumId = Guid.Parse("b0000000-0000-0000-0000-000000000011");
    public static readonly Guid TrackOneId = Guid.Parse("b0000000-0000-0000-0000-000000000101");
    public static readonly Guid TrackTwoId = Guid.Parse("b0000000-0000-0000-0000-000000000102");

    public const string ShowTitle = "Le Continental";

    /// <summary>The show page: its seasons, with the episodes of the first one listed.</summary>
    public static DetailPageViewModel ShowPage() => new()
    {
        Id = ShowId.ToString("D"),
        EntityType = DetailEntityType.TvShow,
        Title = ShowTitle,
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions = [new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" }],
        EditorTarget = new DetailEditorTarget { EntityId = ShowId.ToString("D"), EntityKind = "Work" },
        SequencePlacement = new SequencePlacementViewModel
        {
            ContainerId = ShowId.ToString("D"),
            ContainerTitle = ShowTitle,
            Groups =
            [
                new SequenceGroupViewModel
                {
                    Key = "season-1",
                    Title = "Season 1",
                    EntityId = SeasonOneId.ToString("D"),
                    Items = [Episode(EpisodeOneId, "Episode 1", "season-1"), Episode(EpisodeTwoId, "Episode 2", "season-1")],
                },
                new SequenceGroupViewModel { Key = "season-2", Title = "Season 2", EntityId = SeasonTwoId.ToString("D"), Items = [] },
            ],
        },
    };

    /// <summary>The page of the second episode of the first season, which names its show and season through the placement.</summary>
    public static DetailPageViewModel EpisodePage() => new()
    {
        Id = EpisodeTwoId.ToString("D"),
        EntityType = DetailEntityType.TvEpisode,
        Title = "Episode 2",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions = [new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" }],
        EditorTarget = new DetailEditorTarget { EntityId = EpisodeTwoId.ToString("D"), EntityKind = "Work" },
        SequencePlacement = new SequencePlacementViewModel
        {
            ContainerId = ShowId.ToString("D"),
            ContainerTitle = ShowTitle,
            Groups =
            [
                new SequenceGroupViewModel
                {
                    Key = "season-1",
                    Title = "Season 1",
                    EntityId = SeasonOneId.ToString("D"),
                    Items =
                    [
                        Episode(EpisodeOneId, "Episode 1", "season-1"),
                        Episode(EpisodeTwoId, "Episode 2", "season-1", current: true),
                    ],
                },
            ],
        },
    };

    public static DetailPageViewModel AlbumPage() => new()
    {
        Id = AlbumId.ToString("D"),
        EntityType = DetailEntityType.MusicAlbum,
        Title = "OK Computer",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions = [new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" }],
        EditorTarget = new DetailEditorTarget { EntityId = AlbumId.ToString("D"), EntityKind = "Work" },
        MusicAlbumCompanion = new MusicAlbumCompanionViewModel { PrimaryArtistId = ArtistId.ToString("D"), PrimaryArtistName = "Radiohead" },
        MediaGroups =
        [
            new MediaGroupingViewModel
            {
                Key = "tracks",
                Title = "Tracks",
                Items =
                [
                    new MediaGroupingItemViewModel { Id = TrackOneId.ToString("D"), Title = "Airbag", TrackNumber = "1" },
                    new MediaGroupingItemViewModel { Id = TrackTwoId.ToString("D"), Title = "Paranoid Android", TrackNumber = "2" },
                    new MediaGroupingItemViewModel { Id = Guid.NewGuid().ToString("D"), Title = "Not in the library", IsOwned = false },
                ],
            },
        ],
    };

    /// <summary>
    /// The Engine's family reads for the editor's tree (navigator and owned-children), for the show and the album above. Season 1 holds a matched
    /// Episode 1 and an unmatched Episode 2; Season 2 holds an unmatched Episode 3; the album holds two matched tracks. Every other id has no family.
    /// <paramref name="asked"/> collects "navigator:{id}" and "children:{id}:page{n}" in the order they were asked.
    /// </summary>
    internal static void RegisterFamily(EngineApiClientStub stub, List<string>? asked = null)
    {
        stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorNavigatorAsync), args =>
        {
            var id = (Guid)args![0]!;
            asked?.Add($"navigator:{id:N}");
            return Task.FromResult(Navigator(id));
        });
        stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorOwnedChildrenAsync), args =>
        {
            var parent = (Guid)args![0]!;
            var page = (int)args[2]!;
            asked?.Add($"children:{parent:N}:page{page}");
            return Task.FromResult(OwnedChildren(parent));
        });
    }

    private static MediaEditorNavigatorDto? Navigator(Guid id)
    {
        if (id == ShowId || id == SeasonOneId || id == SeasonTwoId || id == EpisodeOneId || id == EpisodeTwoId || id == EpisodeThreeId)
        {
            return new MediaEditorNavigatorDto
            {
                Enabled = true,
                MediaType = "TV",
                ContainerEntityId = ShowId,
                SelectedEntityId = id,
                Nodes =
                [
                    Node(ShowId, null, "series", ShowTitle, 0),
                    Node(SeasonOneId, ShowId, "season", "Season 1", 1),
                    Node(SeasonTwoId, ShowId, "season", "Season 2", 1),
                ],
            };
        }

        if (id == AlbumId || id == TrackOneId || id == TrackTwoId)
        {
            return new MediaEditorNavigatorDto
            {
                Enabled = true,
                MediaType = "Music",
                ContainerEntityId = AlbumId,
                SelectedEntityId = id,
                Nodes = [Node(AlbumId, null, "album", "OK Computer", 0)],
            };
        }

        return null;
    }

    private static MediaEditorNavigatorNodeDto Node(Guid id, Guid? parent, string kind, string title, int depth) => new()
    {
        NodeId = id,
        EntityId = id,
        ParentNodeId = parent,
        NodeKind = kind,
        Title = title,
        Depth = depth,
        IsRoot = depth == 0,
        IsLeaf = false,
        IsOwned = true,
    };

    private static MediaEditorOwnedChildSearchDto OwnedChildren(Guid parent)
    {
        var items = parent switch
        {
            _ when parent == SeasonOneId => new List<MediaEditorOwnedChildDto> { File(EpisodeOneId, "Episode 1", "1", true), File(EpisodeTwoId, "Episode 2", "2", false) },
            _ when parent == SeasonTwoId => [File(EpisodeThreeId, "Episode 3", "3", false)],
            _ when parent == AlbumId => [File(TrackOneId, "Airbag", "1", true), File(TrackTwoId, "Paranoid Android", "2", true)],
            _ => [],
        };
        return new MediaEditorOwnedChildSearchDto { ParentEntityId = parent, Page = 1, PageSize = 50, TotalCount = items.Count, Items = items };
    }

    private static MediaEditorOwnedChildDto File(Guid workId, string title, string number, bool matched) => new()
    {
        WorkId = workId,
        AssetId = Guid.NewGuid(),
        Title = title,
        MatchedNumber = number,
        SourceFileName = title + ".mkv",
        SourceFilePath = "/library/" + title + ".mkv",
        MatchState = matched ? "matched" : "unmatched",
        MatchReason = matched ? null : "No show, season or episode on a provider matches this file yet.",
    };

    private static SequenceItemViewModel Episode(Guid id, string title, string groupKey, bool current = false) => new()
    {
        Id = id.ToString("D"),
        EntityType = DetailEntityType.TvEpisode,
        Title = title,
        GroupKey = groupKey,
        IsOwned = true,
        IsCurrent = current,
    };

    /// <summary>The editor's answer for a show, season or episode, with a title field the Details tool can stage.</summary>
    public static MediaEditorContextDto TvContext(Guid launch, string scopeId)
    {
        var scopes = new List<MediaEditorScopeDto> { Scope("series", ShowId, ShowTitle, 0) };
        if (scopeId is "season" or "episode")
        {
            var season = scopeId == "season" ? launch : SeasonOneId;
            scopes.Add(Scope("season", season, season == SeasonTwoId ? "Season 2" : "Season 1", 1));
        }

        if (scopeId == "episode")
        {
            scopes.Add(Scope("episode", launch, launch == EpisodeOneId ? "Episode 1" : "Episode 2", 2));
        }

        return new MediaEditorContextDto
        {
            LaunchEntityId = launch,
            MediaType = "TV",
            InitialScope = scopeId,
            AvailableTabs = ["details", "artwork", "links", "history"],
            Scopes = scopes,
        };
    }

    public static MediaEditorContextDto AlbumContext(Guid launch, string scopeId)
    {
        var scopes = new List<MediaEditorScopeDto> { Scope("album", AlbumId, "OK Computer", 0) };
        if (scopeId == "track")
        {
            scopes.Add(Scope("track", launch, launch == TrackOneId ? "Airbag" : "Paranoid Android", 1));
        }

        return new MediaEditorContextDto
        {
            LaunchEntityId = launch,
            MediaType = "Music",
            InitialScope = scopeId,
            AvailableTabs = ["details", "artwork", "links", "history"],
            Scopes = scopes,
        };
    }

    private static MediaEditorScopeDto Scope(string scopeId, Guid entityId, string title, int order) => new()
    {
        ScopeId = scopeId,
        Label = scopeId,
        Order = order,
        FieldEntityId = entityId,
        DisplayTitle = title,
        CanEditFields = true,
        CanEditArtwork = true,
        ArtworkSlots = ["CoverArt"],
        FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
        {
            CanonicalFields =
            [
                new MediaEditorScopedFieldValueDto { Key = "title", Value = title, ProviderName = "tvdb" },
                new MediaEditorScopedFieldValueDto { Key = "description", Value = $"About {title}", ProviderName = "tvdb" },
            ],
            AllowedOverrideKeys = ["title", "description", "tagline", "genre", "custom_tags", "year"],
        },
    };
}
