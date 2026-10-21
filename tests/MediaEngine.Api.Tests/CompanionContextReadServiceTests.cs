using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The phone companion's context for one Work at one position: who is here up to that point (the one spoiler rule, with the
/// viewer counted as standing at the companion's place), where else the profile has met them, the previous owned episode, the
/// chapter of a book or audiobook, and honest empty answers. Real temp SQLite; the authorised display projection and the
/// chapter source are hand-written spies.
/// </summary>
public sealed class CompanionContextReadServiceTests : IDisposable
{
    private const string UniverseQid = "Q4242";
    private const string ShowQid = "Q-show";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_companion_context_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly FictionalEntityRepository _entities;
    private readonly PersonRepository _people;

    public CompanionContextReadServiceTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        _entities = new FictionalEntityRepository(_database);
        _people = new PersonRepository(_database);
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { }
        try { File.Delete(_databasePath); } catch { }
    }

    [Fact]
    public async Task AnEpisode_ShowsCharactersUpToThatEpisode_HoldsBackLaterOnes_AndNamesTitleLevelOnesWithoutPlotDetails()
    {
        var (service, episodes) = await ShowWithThreeEpisodesAsync();
        var profile = await ProfileWithLevelAsync(SpoilerLevels.UpToProgress);

        var context = await service.GetAsync(episodes[1].WorkId, null, null, profile);

        Assert.NotNull(context);
        Assert.Equal(CompanionKinds.Episode, context.Kind);
        Assert.Equal("Season 1 · Episode 2", context.PositionLabel);
        Assert.Equal("Lanterns", context.ShowTitle);
        Assert.Equal(SpoilerLevels.UpToProgress, context.SpoilerLevel);
        Assert.True(context.WhosHere.HasCharacterDetails);
        // Alice first appears in episode 1 (shown), Bob only in episode 3 (held back, not named), Cleo is linked to the show as
        // a whole so the Library cannot place her within the episodes (named, without plot details, last).
        Assert.Equal(["Alice", "Cleo"], context.WhosHere.Characters.Select(character => character.Name));
        Assert.Equal(CompanionCharacterVisibility.Shown, context.WhosHere.Characters[0].Visibility);
        Assert.Equal(CompanionCharacterVisibility.NoPlotDetails, context.WhosHere.Characters[1].Visibility);
        Assert.Equal(1, context.WhosHere.HeldBackCount);
        Assert.DoesNotContain(context.WhosHere.Characters, character => character.Name == "Bob");
        Assert.All(context.WhosHere.Characters, character => Assert.False(string.IsNullOrWhiteSpace(character.Source)));
    }

    [Fact]
    public async Task AtTheLevelEverything_EveryCharacterIsShown_AndNothingIsHeldBack()
    {
        var (service, episodes) = await ShowWithThreeEpisodesAsync();
        var profile = await ProfileWithLevelAsync(SpoilerLevels.Everything);

        var context = await service.GetAsync(episodes[0].WorkId, null, null, profile);

        Assert.NotNull(context);
        Assert.Equal(SpoilerLevels.Everything, context.SpoilerLevel);
        Assert.Equal(["Alice", "Bob", "Cleo"], context.WhosHere.Characters.Select(character => character.Name).Order());
        Assert.All(context.WhosHere.Characters, character => Assert.Equal(CompanionCharacterVisibility.Shown, character.Visibility));
        Assert.Equal(0, context.WhosHere.HeldBackCount);
    }

    [Fact]
    public async Task TheCompanionsPlace_CountsAsReached_SoMovingItForwardRevealsTheNextCharacter()
    {
        var (service, episodes) = await ShowWithThreeEpisodesAsync();
        var profile = await ProfileWithLevelAsync(SpoilerLevels.UpToProgress);

        var atTwo = await service.GetAsync(episodes[1].WorkId, null, null, profile);
        var atThree = await service.GetAsync(episodes[2].WorkId, null, null, profile);

        Assert.DoesNotContain(atTwo!.WhosHere.Characters, character => character.Name == "Bob");
        Assert.Contains(atThree!.WhosHere.Characters, character => character.Name == "Bob" && character.Visibility == CompanionCharacterVisibility.Shown);
        Assert.Equal(0, atThree.WhosHere.HeldBackCount);
    }

    [Fact]
    public async Task APersonWithNoProfile_IsJudgedAtTheDefaultLevel()
    {
        var (service, episodes) = await ShowWithThreeEpisodesAsync();

        var context = await service.GetAsync(episodes[0].WorkId, null, null, profile: null);

        Assert.NotNull(context);
        Assert.Equal(SpoilerLevels.UpToProgress, context.SpoilerLevel);
        Assert.DoesNotContain(context.WhosHere.Characters, character => character.Name == "Bob");
    }

    [Fact]
    public async Task Previously_IsTheLastOwnedEpisodeBeforeThisOne_AndAbsentForTheFirst()
    {
        var (service, episodes) = await ShowWithThreeEpisodesAsync();

        var second = await service.GetAsync(episodes[1].WorkId, null, null, profile: null);
        var first = await service.GetAsync(episodes[0].WorkId, null, null, profile: null);

        Assert.NotNull(second);
        Assert.NotNull(second.Previously);
        Assert.Equal("Pilot", second.Previously.Title);
        Assert.Equal("Season 1 · Episode 1", second.Previously.Label);
        Assert.Equal("The pilot synopsis.", second.Previously.Synopsis);
        Assert.Equal(episodes[0].WorkId, second.Previously.WorkId);
        Assert.Null(first!.Previously);
    }

    [Fact]
    public async Task AnEpisodesPicker_ListsTheOwnedEpisodesInOrder_ForPickingByHand()
    {
        var (service, episodes) = await ShowWithThreeEpisodesAsync();

        var context = await service.GetAsync(episodes[2].WorkId, null, null, profile: null);

        var picker = context!.Picker;
        Assert.NotNull(picker);
        Assert.Equal(CompanionPickerKinds.Episode, picker.Kind);
        Assert.Equal(episodes.Select(episode => episode.WorkId), picker.Choices.Select(choice => choice.WorkId));
        Assert.Equal(["Season 1 · Episode 1", "Season 1 · Episode 2", "Season 1 · Episode 3"], picker.Choices.Select(choice => choice.Label));
        Assert.All(picker.Choices, choice => Assert.Null(choice.ChapterIndex));
    }

    [Fact]
    public async Task ACharacterAlsoMetInAFilm_CarriesHowManyOtherWorksTheProfileOwns()
    {
        var (service, episodes, film) = await ShowAndFilmAsync();
        var profile = await ProfileWithLevelAsync(SpoilerLevels.Everything);

        var inShow = await service.GetAsync(episodes[0].WorkId, null, null, profile);
        var inFilm = await service.GetAsync(film.WorkId, null, null, profile);

        var alice = Assert.Single(inShow!.WhosHere.Characters, character => character.Name == "Alice");
        Assert.Equal(1, alice.OtherWorkCount);
        Assert.Equal("Q-alice", alice.Qid);
        Assert.Equal(UniverseQid, alice.UniverseQid);
        var aliceInFilm = Assert.Single(inFilm!.WhosHere.Characters, character => character.Name == "Alice");
        Assert.Equal(1, aliceInFilm.OtherWorkCount);
        Assert.Equal("Film", inFilm.PositionLabel);
        Assert.Equal(CompanionKinds.Film, inFilm.Kind);
        Assert.Null(inFilm.Picker);
    }

    [Fact]
    public async Task AWorkWithNoCharacterData_SaysSo_AndShowsTheSynopsisItHas()
    {
        var film = Row(Guid.NewGuid(), "Movies", "Q-lonely-film", "A Lonely Film", description: "A quiet film.");
        var service = CreateService([film]);

        var context = await service.GetAsync(film.WorkId, null, null, profile: null);

        Assert.NotNull(context);
        Assert.False(context.WhosHere.HasCharacterDetails);
        Assert.Empty(context.WhosHere.Characters);
        Assert.Equal(0, context.WhosHere.HeldBackCount);
        Assert.Equal("A quiet film.", context.Synopsis);
        Assert.Null(context.Previously);
        Assert.Null(context.Universe);
    }

    [Fact]
    public async Task ABookChapter_IsLabelledFromTheTableOfContents_AndItsCharactersStayWithoutPlotDetails()
    {
        var book = Row(Guid.NewGuid(), "Books", "Q-book", "The Gate");
        await CreateEntityAsync("Q-dana", "Dana", "Character");
        await LinkAsync("Q-dana", "Q-book");
        var service = CreateService([book], new FixedChapters(new CompanionChapter(0, "Opening"), new CompanionChapter(1, "The Gate")));
        var profile = await ProfileWithLevelAsync(SpoilerLevels.UpToProgress);

        var context = await service.GetAsync(book.WorkId, 1, null, profile);

        Assert.NotNull(context);
        Assert.Equal(CompanionKinds.Book, context.Kind);
        Assert.Equal("Chapter 2: The Gate", context.PositionLabel);
        Assert.Equal(1, context.ChapterIndex);
        Assert.Equal("The Gate", context.ChapterTitle);
        // No source places a character within a book's chapters, so the rule keeps her to a name with no plot details.
        var dana = Assert.Single(context.WhosHere.Characters);
        Assert.Equal(CompanionCharacterVisibility.NoPlotDetails, dana.Visibility);
        Assert.Equal(0, dana.OtherWorkCount);
        Assert.Null(dana.PerformedBy);
        Assert.Equal(CompanionPickerKinds.Chapter, context.Picker!.Kind);
        Assert.Equal([0, 1], context.Picker.Choices.Select(choice => choice.ChapterIndex));
        Assert.All(context.Picker.Choices, choice => Assert.Equal(book.WorkId, choice.WorkId));
        Assert.Empty(context.Cast);
    }

    [Fact]
    public async Task AnAudiobooksChapter_FollowsTheDevicesPosition_WhenNoChapterIsGiven()
    {
        var audiobook = Row(Guid.NewGuid(), "Audiobooks", "Q-audio", "The Gate (read aloud)");
        var service = CreateService(
            [audiobook],
            new FixedChapters(new CompanionChapter(0, "Opening", 0), new CompanionChapter(1, "The Gate", 600), new CompanionChapter(2, "The Road", 1200)));

        var followed = await service.GetAsync(audiobook.WorkId, null, 700, profile: null);
        var byHand = await service.GetAsync(audiobook.WorkId, 0, 700, profile: null);
        var beforeAnyChapter = await service.GetAsync(audiobook.WorkId, null, null, profile: null);

        Assert.Equal(CompanionKinds.Audiobook, followed!.Kind);
        Assert.Equal(1, followed.ChapterIndex);
        Assert.Equal("Chapter 2: The Gate", followed.PositionLabel);
        // A chapter chosen by hand wins over the device's position.
        Assert.Equal("Chapter 1: Opening", byHand!.PositionLabel);
        Assert.Equal("No chapter chosen", beforeAnyChapter!.PositionLabel);
        Assert.Null(beforeAnyChapter.ChapterIndex);
    }

    [Fact]
    public async Task ABookWithNoChapterList_StillShowsAChapterNumberTheProfileSet_AndOffersNoPicker()
    {
        var book = Row(Guid.NewGuid(), "Books", "Q-flat-book", "No Contents");
        var service = CreateService([book], new FixedChapters());

        var context = await service.GetAsync(book.WorkId, 4, null, profile: null);

        Assert.Equal("Chapter 5", context!.PositionLabel);
        Assert.Equal(4, context.ChapterIndex);
        Assert.Null(context.ChapterTitle);
        Assert.Null(context.Picker);
    }

    [Fact]
    public async Task AWorkTheProfileCannotSee_IsNotDescribed()
    {
        var visible = Row(Guid.NewGuid(), "Movies", "Q-visible", "Visible Film");
        var service = CreateService([visible]);

        Assert.Null(await service.GetAsync(Guid.NewGuid(), null, null, profile: null));
    }

    [Fact]
    public async Task ACharacterLinkedOnlyToAWorkTheProfileCannotSee_IsNeverNamed()
    {
        var film = Row(Guid.NewGuid(), "Movies", "Q-film", "Film");
        await CreateEntityAsync("Q-secret", "Secret", "Character");
        await LinkAsync("Q-secret", "Q-film");
        await LinkAsync("Q-secret", "Q-restricted");
        var service = CreateService([film]);
        var profile = await ProfileWithLevelAsync(SpoilerLevels.Everything);

        var context = await service.GetAsync(film.WorkId, null, null, profile);

        var secret = Assert.Single(context!.WhosHere.Characters);
        Assert.Equal(0, secret.OtherWorkCount);
    }

    private async Task<(CompanionContextReadService Service, DisplayWorkRow[] Episodes)> ShowWithThreeEpisodesAsync()
    {
        var show = Guid.NewGuid();
        var episodes = new[]
        {
            TvEpisode(show, "Q-ep-1", "Pilot", 1, "The pilot synopsis."),
            TvEpisode(show, "Q-ep-2", "Second", 2, "The second synopsis."),
            TvEpisode(show, "Q-ep-3", "Third", 3, "The third synopsis."),
        };
        for (var index = 0; index < episodes.Length; index++)
        {
            SeedOwnedEpisode(show, episodes[index], episodes[index].IdentityQid!, index + 1);
        }

        SetShowQid(show, ShowQid);
        await CreateEntityAsync("Q-alice", "Alice", "Character");
        await CreateEntityAsync("Q-bob", "Bob", "Character");
        await CreateEntityAsync("Q-cleo", "Cleo", "Character");
        await LinkAsync("Q-alice", "Q-ep-1", "first_appearance");
        await LinkAsync("Q-bob", "Q-ep-3", "first_appearance");
        await LinkAsync("Q-cleo", ShowQid);
        return (CreateService(episodes), episodes);
    }

    private async Task<(CompanionContextReadService Service, DisplayWorkRow[] Episodes, DisplayWorkRow Film)> ShowAndFilmAsync()
    {
        var show = Guid.NewGuid();
        var episodes = new[] { TvEpisode(show, "Q-ep-1", "Pilot", 1, "The pilot synopsis.") };
        SeedOwnedEpisode(show, episodes[0], "Q-ep-1", 1);
        SetShowQid(show, ShowQid);
        var film = Row(Guid.NewGuid(), "Movies", "Q-lantern-film", "The Lantern Film");
        await CreateEntityAsync("Q-alice", "Alice", "Character");
        await LinkAsync("Q-alice", "Q-ep-1", "first_appearance");
        await LinkAsync("Q-alice", "Q-lantern-film", "appears_in");
        return (CreateService([.. episodes, film]), episodes, film);
    }

    private CompanionContextReadService CreateService(IReadOnlyList<DisplayWorkRow> rows, ICompanionChapterSource? chapters = null)
    {
        var display = new DisplayStub(rows);
        var cards = new DisplayCardBuilder();
        var spoilers = SpoilerTestKit.Create(display, _database);
        var connections = new UniverseConnectionsReadService(
            _entities,
            new EntityRelationshipRepository(_database),
            new NarrativeRootRepository(_database),
            _people,
            new PluginLoreRepository(_database),
            spoilers);
        var universes = new UniverseClientReadService(new CollectionRepository(_database), display, cards, connections);
        return new CompanionContextReadService(
            display,
            cards,
            spoilers,
            _entities,
            _people,
            new LibraryItemRepository(_database),
            universes,
            new PersonCreditReadService(new CanonicalValueArrayRepository(_database), _people, _database),
            chapters ?? new FixedChapters());
    }

    private async Task CreateEntityAsync(string qid, string label, string subType) =>
        await _entities.CreateAsync(new FictionalEntity
        {
            WikidataQid = qid,
            Label = label,
            EntitySubType = subType,
            FictionalUniverseQid = UniverseQid,
        });

    private async Task LinkAsync(string entityQid, string workQid, string linkType = "appears_in")
    {
        var entity = await _entities.FindByQidAsync(entityQid);
        await _entities.LinkToWorkAsync(new FictionalEntityWorkLink(entity!.Id, workQid, null, linkType));
    }

    private async Task<Profile> ProfileWithLevelAsync(string level)
    {
        var profiles = new ProfileRepository(_database);
        await new UserPlaybackSettingsService(_database, profiles).SetSpoilerLevelAsync(Profile.SeedProfileId, level);
        return (await profiles.GetByIdAsync(Profile.SeedProfileId))!;
    }

    /// <summary>Writes the work, edition and file behind a hand-made display row, so the owned story can place the episode.</summary>
    private void SeedOwnedEpisode(Guid show, DisplayWorkRow row, string qid, int ordinal)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT OR IGNORE INTO works (id, media_type, work_kind) VALUES (@show, 'TV', 'parent');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, wikidata_qid)
            VALUES (@work, 'TV', 'child', @show, @ordinal, @qid);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@asset, @edition, @hash, @path);
            """,
            new
            {
                show,
                work = row.WorkId,
                edition = row.EditionId,
                asset = row.AssetId,
                qid,
                ordinal,
                hash = row.AssetId.ToString("N"),
                path = $"C:/library/Lanterns/{row.AssetId:N}.mkv",
            });
    }

    private void SetShowQid(Guid show, string qid)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE works SET wikidata_qid = @qid WHERE id = @show;", new { show, qid });
    }

    private static DisplayWorkRow Row(Guid workId, string mediaType, string identityQid, string title, string? description = null) =>
        new()
        {
            WorkId = workId,
            AssetId = Guid.NewGuid(),
            RootWorkId = workId,
            EditionId = Guid.NewGuid(),
            MediaType = mediaType,
            IdentityQid = identityQid,
            Title = title,
            Description = description,
            Year = "2020",
            CoverUrl = $"/stream/artwork/{workId:D}",
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static DisplayWorkRow TvEpisode(Guid showRootId, string identityQid, string title, int number, string description)
    {
        var row = Row(Guid.NewGuid(), "TV", identityQid, title, description);
        row.RootWorkId = showRootId;
        row.SeasonNumber = "1";
        row.EpisodeNumber = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.ShowName = "Lanterns";
        row.RootCoverUrl = $"/stream/artwork/{showRootId:D}";
        return row;
    }

    private sealed class FixedChapters(params CompanionChapter[] chapters) : ICompanionChapterSource
    {
        public Task<IReadOnlyList<CompanionChapter>> GetChaptersAsync(DisplayWorkRow row, Guid? profileId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CompanionChapter>>(chapters);
    }

    private sealed class DisplayStub(IReadOnlyList<DisplayWorkRow> works) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
