using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Also appears in": where else a character, place, organisation or person from a Universe shows up across the Library.
/// Matching is by Wikidata item, only Works the profile may see are listed, and the answer says honestly which Works in
/// the Universe have no character details yet. Real temp SQLite; the authorised display projection is a hand-written spy.
/// </summary>
public sealed class EntityAppearanceTests : IDisposable
{
    private const string UniverseQid = "Q4242";
    private const string HalQid = "Q-hal-jordan";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_also_appears_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly FictionalEntityRepository _entities;
    private readonly PersonRepository _people;

    public EntityAppearanceTests()
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
    public async Task SharedCharacter_ListsTheTvSeriesAndTheComic_GroupedByFormat()
    {
        var show = Guid.NewGuid();
        var comic = Guid.NewGuid();
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await LinkAsync(HalQid, "Q-lanterns-show", "protagonist");
        await LinkAsync(HalQid, "Q-green-lantern-1", "lead");
        var service = CreateService(
            TvEpisode(show, Guid.NewGuid(), "Q-lanterns-show", "Pilot", 2024),
            TvEpisode(show, Guid.NewGuid(), "Q-lanterns-show", "Second", 2024),
            Row(comic, "Comics", "Q-green-lantern-1", "Green Lantern #1", 1960));

        var result = await service.GetForEntityAsync(UniverseQid, HalQid, profile: null);

        Assert.NotNull(result);
        Assert.Equal(EntityAppearanceSubjectKinds.Character, result.SubjectKind);
        Assert.Equal("Hal Jordan", result.SubjectLabel);
        Assert.Equal(HalQid, result.SubjectQid);
        Assert.Null(result.PersonId);
        Assert.Equal(["TV", "Comics"], result.Groups.Select(group => group.Label));
        Assert.Equal(["TV", "Comic"], result.Groups.Select(group => group.MediaKind));

        var tv = Assert.Single(Assert.Single(result.Groups, group => group.MediaKind == "TV").Works);
        Assert.Equal(show, tv.WorkId);
        Assert.Equal($"/details/tvshow/{show:D}?context=watch", tv.Route);
        Assert.Equal("protagonist", tv.Role);

        var issue = Assert.Single(Assert.Single(result.Groups, group => group.MediaKind == "Comic").Works);
        Assert.Equal(comic, issue.WorkId);
        Assert.Equal("Green Lantern #1", issue.Title);
        Assert.Equal(1960, issue.Year);
        Assert.Equal($"/details/work/{comic:D}?context=read", issue.Route);
        Assert.Equal("lead", issue.Role);

        // Two owned episodes are one show, so the total counts the show once.
        Assert.Equal(2, result.TotalWorks);
    }

    [Fact]
    public async Task Appearances_ListGroupsInFormatOrder_AndWorksByYearThenTitle()
    {
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        foreach (var qid in new[] { "Q-m-late", "Q-m-early-b", "Q-m-early-a", "Q-book", "Q-song" })
        {
            await LinkAsync(HalQid, qid);
        }

        var service = CreateService(
            Row(Guid.NewGuid(), "Music", "Q-song", "A Song", 2001),
            Row(Guid.NewGuid(), "Books", "Q-book", "A Book", 1999),
            Row(Guid.NewGuid(), "Movies", "Q-m-late", "Zeta", 2019),
            Row(Guid.NewGuid(), "Movies", "Q-m-early-b", "Beta", 1998),
            Row(Guid.NewGuid(), "Movies", "Q-m-early-a", "Alpha", 1998));

        var result = await service.GetForEntityAsync(UniverseQid, HalQid, profile: null);

        Assert.NotNull(result);
        Assert.Equal(["Movies", "Books", "Music"], result.Groups.Select(group => group.Label));
        Assert.Equal(["Alpha", "Beta", "Zeta"], result.Groups[0].Works.Select(work => work.Title));
    }

    [Fact]
    public async Task Appearances_OmitWorksTheProfileCannotSee()
    {
        var visible = Guid.NewGuid();
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await LinkAsync(HalQid, "Q-visible-movie");
        await LinkAsync(HalQid, "Q-restricted-movie");
        var service = CreateService(Row(visible, "Movies", "Q-visible-movie", "Visible Film", 2011));

        var result = await service.GetForEntityAsync(UniverseQid, HalQid, profile: null);

        Assert.NotNull(result);
        var work = Assert.Single(Assert.Single(result.Groups).Works);
        Assert.Equal(visible, work.WorkId);
        Assert.Equal(1, result.TotalWorks);
        Assert.DoesNotContain("Restricted", string.Concat(result.Groups.SelectMany(group => group.Works).Select(entry => entry.Title)));
    }

    [Fact]
    public async Task AppearancesMarkedAsSpoilers_AreLeftOut_SoThePanelMatchesTheCharactersList()
    {
        var plain = Guid.NewGuid();
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await LinkAsync(HalQid, "Q-plain-movie");
        await LinkAsync(HalQid, "Q-spoiler-movie", spoilerFor: "Q-spoiler-movie");
        var service = CreateService(
            Row(plain, "Movies", "Q-plain-movie", "Plain Film", 2011),
            Row(Guid.NewGuid(), "Movies", "Q-spoiler-movie", "Spoiler Film", 2012));

        var result = await service.GetForEntityAsync(UniverseQid, HalQid, profile: null);

        Assert.NotNull(result);
        Assert.Equal(plain, Assert.Single(Assert.Single(result.Groups).Works).WorkId);
        Assert.Equal(1, result.TotalWorks);
    }

    [Fact]
    public async Task ACharacterWhoseOnlyAppearancesAreSpoilersForAnUnfinishedWork_IsHeldBackWithoutBeingDescribed()
    {
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await LinkAsync(HalQid, "Q-spoiler-movie", spoilerFor: "Q-spoiler-movie");
        var service = CreateService(Row(Guid.NewGuid(), "Movies", "Q-spoiler-movie", "Spoiler Film", 2012));

        var result = await service.GetForEntityAsync(UniverseQid, HalQid, profile: null);

        Assert.NotNull(result);
        Assert.True(result.HiddenForSpoilers);
        Assert.Equal(string.Empty, result.SubjectLabel);
        Assert.Null(result.SubjectQid);
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalWorks);
    }

    [Fact]
    public async Task ASpoilerMarkedCharacter_IsDescribedInFull_AtTheLevelEverything()
    {
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await LinkAsync(HalQid, "Q-spoiler-movie", spoilerFor: "Q-spoiler-movie");
        var service = CreateService(Row(Guid.NewGuid(), "Movies", "Q-spoiler-movie", "Spoiler Film", 2012));
        var profile = await ProfileWithLevelAsync(SpoilerLevels.Everything);

        var result = await service.GetForEntityAsync(UniverseQid, HalQid, profile);

        Assert.NotNull(result);
        Assert.False(result.HiddenForSpoilers);
        Assert.Equal("Hal Jordan", result.SubjectLabel);
        Assert.Equal(1, result.TotalWorks);
    }

    [Fact]
    public async Task ACharacterFirstSeenInALaterEpisode_IsHeldBack_UntilTheViewerReachesThatEpisode()
    {
        var show = Guid.NewGuid();
        var episodeOne = TvEpisode(show, Guid.NewGuid(), "Q-ep-1", "Pilot", 2024, season: 1, number: 1);
        var episodeTwo = TvEpisode(show, Guid.NewGuid(), "Q-ep-2", "Second", 2024, season: 1, number: 2);
        SeedOwnedEpisode(show, episodeOne, "Q-ep-1", ordinal: 1);
        SeedOwnedEpisode(show, episodeTwo, "Q-ep-2", ordinal: 2);
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await LinkTypedAsync(HalQid, "Q-ep-2", "first_appearance");
        var service = CreateService(episodeOne, episodeTwo);
        var profile = await ProfileWithLevelAsync(SpoilerLevels.UpToProgress);

        SetProgress(profile.Id, episodeOne, 100);
        var before = await service.GetForEntityAsync(UniverseQid, HalQid, profile);
        SetProgress(profile.Id, episodeTwo, 5);
        var after = await service.GetForEntityAsync(UniverseQid, HalQid, profile);

        Assert.True(before!.HiddenForSpoilers);
        Assert.False(after!.HiddenForSpoilers);
        Assert.Equal("Hal Jordan", after.SubjectLabel);
        Assert.Equal(1, after.TotalWorks);
    }

    [Fact]
    public async Task EntityWithOnlyHiddenWorks_IsNotConfirmedToExist()
    {
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await LinkAsync(HalQid, "Q-restricted-movie");
        var service = CreateService(Row(Guid.NewGuid(), "Movies", "Q-other", "Other Film", 2011));

        Assert.Null(await service.GetForEntityAsync(UniverseQid, HalQid, profile: null));
    }

    [Fact]
    public async Task MatchingIsByQid_NotByName()
    {
        var firstWork = Guid.NewGuid();
        var secondWork = Guid.NewGuid();
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await CreateEntityAsync("Q-other-hal", "Hal Jordan", "Character");
        await LinkAsync(HalQid, "Q-first");
        await LinkAsync("Q-other-hal", "Q-second");
        var service = CreateService(
            Row(firstWork, "Movies", "Q-first", "First Film", 2011),
            Row(secondWork, "Movies", "Q-second", "Second Film", 2012));

        var result = await service.GetForEntityAsync(UniverseQid, HalQid, profile: null);

        Assert.NotNull(result);
        var work = Assert.Single(Assert.Single(result.Groups).Works);
        Assert.Equal(firstWork, work.WorkId);
    }

    [Theory]
    [InlineData("Character", EntityAppearanceSubjectKinds.Character)]
    [InlineData("Location", EntityAppearanceSubjectKinds.Location)]
    [InlineData("Organization", EntityAppearanceSubjectKinds.Organization)]
    [InlineData("Event", EntityAppearanceSubjectKinds.Entity)]
    public async Task SubjectKind_FollowsTheEntitySubType_AndAnythingElseIsEntity(string subType, string expected)
    {
        await CreateEntityAsync("Q-thing", "A Thing", subType);
        await LinkAsync("Q-thing", "Q-film");
        var service = CreateService(Row(Guid.NewGuid(), "Movies", "Q-film", "Film", 2000));

        var result = await service.GetForEntityAsync(UniverseQid, "Q-thing", profile: null);

        Assert.Equal(expected, result!.SubjectKind);
    }

    [Fact]
    public async Task WorksWithNoCharacterData_AreCountedNotGuessed()
    {
        var series = Guid.NewGuid();
        SeedUniverse(series);
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await LinkAsync(HalQid, "Q-with-data");
        var withData = Row(Guid.NewGuid(), "Movies", "Q-with-data", "Has Data", 2011, series);
        var checkedEmpty = Row(Guid.NewGuid(), "Movies", "Q-checked-empty", "Looked Up, Nothing Found", 2012, series);
        var checkedEmptyToo = Row(Guid.NewGuid(), "Books", "Q-checked-empty-too", "Also Nothing Found", 2013, series);
        var notChecked = Row(Guid.NewGuid(), "Movies", "Q-not-checked", "Not Looked Up Yet", 2014, series);
        // A work whose core pass linked entities keeps stage3_enriched_at; one whose core pass linked none has it cleared,
        // and only stage3_enhanced_at (written once the enhancer step finishes) remains. Both markers sit on the media asset.
        MarkUniverseEnriched("stage3_enriched_at", withData);
        MarkUniverseEnriched("stage3_enhanced_at", checkedEmpty, checkedEmptyToo);
        var service = CreateService(
            withData,
            checkedEmpty,
            checkedEmptyToo,
            notChecked,
            // Not a member of this Universe: never counted as a gap in it.
            Row(Guid.NewGuid(), "Movies", "Q-elsewhere", "Elsewhere", 2015));

        var result = await service.GetForEntityAsync(UniverseQid, HalQid, profile: null);

        Assert.NotNull(result);
        Assert.Equal(2, result.WorksWithoutCharacterData);
        Assert.Equal(1, result.WorksNotYetChecked);
        // The gaps are counted, never filled in: the listed Works are only the ones with real links.
        Assert.Equal(1, result.TotalWorks);
    }

    [Fact]
    public async Task GapCounts_TreatAnOwnedShowAsOneWork()
    {
        var series = Guid.NewGuid();
        var show = Guid.NewGuid();
        SeedUniverse(series);
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await LinkAsync(HalQid, "Q-elsewhere-film");
        // Episodes carry no QID of their own, so each row's identity is the show's.
        var episodeOne = TvEpisode(show, Guid.NewGuid(), "Q-show", "One", 2024, series);
        var episodeTwo = TvEpisode(show, Guid.NewGuid(), "Q-show", "Two", 2024, series);
        MarkUniverseEnriched("stage3_enhanced_at", episodeOne, episodeTwo);
        var service = CreateService(
            episodeOne,
            episodeTwo,
            Row(Guid.NewGuid(), "Movies", "Q-elsewhere-film", "Linked Elsewhere", 2001));

        var result = await service.GetForEntityAsync(UniverseQid, HalQid, profile: null);

        Assert.NotNull(result);
        Assert.Equal(1, result.WorksWithoutCharacterData);
        Assert.Equal(0, result.WorksNotYetChecked);
    }

    [Fact]
    public async Task PersonAppearances_UseTheirLibraryCreditsGroupedByFormat()
    {
        var personId = Guid.NewGuid();
        var showWork = Guid.NewGuid();
        var collection = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var movieWork = Guid.NewGuid();
        SeedPersonCredits(personId, collection, showWork, episodeIds, movieWork);
        var rows = new List<DisplayWorkRow>
        {
            Row(showWork, "TV", "QSHOW", "Sample Crime Show", 2008, collection),
            Row(movieWork, "Movies", "QFILM", "Sample Film", 2015),
        };
        rows.AddRange(episodeIds.Select(id => Row(id, "TV", "QEP" + id.ToString("N")[..6], "Episode", 2008, collection)));
        var service = CreateService(rows.ToArray());

        var result = await service.GetForPersonAsync(UniverseQid, personId);

        Assert.NotNull(result);
        Assert.Equal(EntityAppearanceSubjectKinds.Person, result.SubjectKind);
        Assert.Equal("Dana Test", result.SubjectLabel);
        Assert.Equal(personId, result.PersonId);
        Assert.Null(result.SubjectQid);
        Assert.Equal(["TV", "Movies"], result.Groups.Select(group => group.Label));

        // The show is one entry however many episodes are owned, listing both of the person's roles on it.
        var show = Assert.Single(result.Groups[0].Works);
        Assert.Equal("Sample Crime Show", show.Title);
        Assert.Equal($"/details/tvshow/{collection:D}?context=watch", show.Route);
        Assert.Equal("Actor, Director", show.Role);

        var film = Assert.Single(result.Groups[1].Works);
        Assert.Equal(movieWork, film.WorkId);
        Assert.Equal("Sample Film", film.Title);
        Assert.Equal($"/details/work/{movieWork:D}?context=watch", film.Route);
        Assert.Equal("Director", film.Role);
        Assert.Equal(2, result.TotalWorks);

        // People have no Universe gaps to report.
        Assert.Equal(0, result.WorksWithoutCharacterData);
        Assert.Equal(0, result.WorksNotYetChecked);
    }

    [Fact]
    public async Task PersonAppearances_OmitWorksTheProfileCannotSee()
    {
        var personId = Guid.NewGuid();
        var showWork = Guid.NewGuid();
        var collection = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid() };
        var movieWork = Guid.NewGuid();
        SeedPersonCredits(personId, collection, showWork, episodeIds, movieWork);
        // Only the film is visible; the show's rows are withheld.
        var service = CreateService(Row(movieWork, "Movies", "QFILM", "Sample Film", 2015));

        var result = await service.GetForPersonAsync(UniverseQid, personId);

        Assert.NotNull(result);
        Assert.Equal("Sample Film", Assert.Single(Assert.Single(result.Groups).Works).Title);
    }

    [Fact]
    public async Task PersonWithNoVisibleCredits_IsNotFound_AndRevealsNoName()
    {
        var personId = Guid.NewGuid();
        SeedPersonCredits(personId, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Guid.NewGuid());
        // The person exists and has credits, but none of those works is visible to this profile.
        var service = CreateService(Row(Guid.NewGuid(), "Movies", "Q-unrelated", "Unrelated Film", 2000));

        Assert.Null(await service.GetForPersonAsync(UniverseQid, personId));

        var result = await UniverseAppearanceEndpoints.GetAlsoAppearsInAsync(
            UniverseQid, null, personId, HttpContextWithNoActiveProfile(), new ProfileRepository(_database), service, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.DoesNotContain("Dana Test", problem.ProblemDetails.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Dana Test", problem.ProblemDetails.Title ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothOrNeitherParameter_IsABadRequest()
    {
        var service = CreateService();
        var context = HttpContextWithNoActiveProfile();

        var neither = await UniverseAppearanceEndpoints.GetAlsoAppearsInAsync(
            UniverseQid, null, null, context, new ProfileRepository(_database), service, CancellationToken.None);
        var blankEntity = await UniverseAppearanceEndpoints.GetAlsoAppearsInAsync(
            UniverseQid, "  ", null, context, new ProfileRepository(_database), service, CancellationToken.None);
        var both = await UniverseAppearanceEndpoints.GetAlsoAppearsInAsync(
            UniverseQid, HalQid, Guid.NewGuid(), context, new ProfileRepository(_database), service, CancellationToken.None);

        Assert.All([neither, blankEntity, both], result =>
            Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode));
    }

    [Fact]
    public async Task UnknownEntity_IsNotFound()
    {
        var service = CreateService();

        var result = await UniverseAppearanceEndpoints.GetAlsoAppearsInAsync(
            UniverseQid, "Q-nobody", null, HttpContextWithNoActiveProfile(), new ProfileRepository(_database), service, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task UnknownPerson_IsNotFound()
    {
        var service = CreateService();

        var result = await UniverseAppearanceEndpoints.GetAlsoAppearsInAsync(
            UniverseQid, null, Guid.NewGuid(), HttpContextWithNoActiveProfile(), new ProfileRepository(_database), service, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task KnownEntity_IsServedAsTheAppearancesBody()
    {
        await CreateEntityAsync(HalQid, "Hal Jordan", "Character");
        await LinkAsync(HalQid, "Q-film");
        var service = CreateService(Row(Guid.NewGuid(), "Movies", "Q-film", "Film", 2000));

        var result = await UniverseAppearanceEndpoints.GetAlsoAppearsInAsync(
            UniverseQid, HalQid, null, HttpContextWithNoActiveProfile(), new ProfileRepository(_database), service, CancellationToken.None);

        var body = Assert.IsType<Ok<EntityAppearancesDto>>(result);
        Assert.Equal(UniverseQid, body.Value!.UniverseQid);
        Assert.Equal(1, body.Value.TotalWorks);
    }

    private EntityAppearanceReadService CreateService(params DisplayWorkRow[] visibleRows)
    {
        var display = new DisplayStub(visibleRows);
        var cards = new DisplayCardBuilder();
        var connections = new UniverseConnectionsReadService(
            _entities,
            new EntityRelationshipRepository(_database),
            new NarrativeRootRepository(_database),
            _people,
            new PluginLoreRepository(_database),
            SpoilerTestKit.Create(display, _database));
        var universes = new UniverseClientReadService(new CollectionRepository(_database), display, cards, connections);
        var credits = new PersonCreditReadService(new CanonicalValueArrayRepository(_database), _people, _database);
        return new EntityAppearanceReadService(
            _entities, _people, credits, display, cards, universes, SpoilerTestKit.Create(display, _database));
    }

    private static HttpContext HttpContextWithNoActiveProfile()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(
            new RequestAuthority(PrincipalKind.DelegatedUserClient, true)));
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private async Task CreateEntityAsync(string qid, string label, string subType) =>
        await _entities.CreateAsync(new FictionalEntity
        {
            WikidataQid = qid,
            Label = label,
            EntitySubType = subType,
            FictionalUniverseQid = UniverseQid,
        });

    private async Task LinkTypedAsync(string entityQid, string workQid, string linkType)
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

    /// <summary>Writes the work, edition and file behind a hand-made display row, so the profile's progress can be stored against it.</summary>
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

    private void SetProgress(Guid profileId, DisplayWorkRow row, double percent)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO user_states (user_id, asset_id, content_hash, progress_pct, last_accessed, extended_properties, revision)
            VALUES (@profileId, @asset, @hash, @percent, CURRENT_TIMESTAMP, '{}', 0)
            ON CONFLICT(user_id, asset_id) DO UPDATE SET progress_pct = excluded.progress_pct;
            """,
            new { profileId, asset = row.AssetId, hash = row.AssetId.ToString("N"), percent });
    }

    private async Task LinkAsync(string entityQid, string workQid, string? role = null, string? spoilerFor = null)
    {
        var entity = await _entities.FindByQidAsync(entityQid);
        await _entities.LinkToWorkAsync(new FictionalEntityWorkLink(
            entity!.Id, workQid, null, "appears_in", AppearanceRole: role, SpoilerForWorkQid: spoilerFor));
    }

    private void SeedUniverse(Guid series)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, wikidata_qid)
            VALUES (@universe, 'Lanterns Universe', 'Universe', 'library', 'materialized', 1, @qid);
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, parent_collection_id)
            VALUES (@series, 'Lanterns Series', 'Series', 'library', 'query', 1, @universe);
            """,
            new { universe = Guid.NewGuid(), series, qid = UniverseQid });
    }

    /// <summary>Writes a universe-enrichment completion marker on each row's media asset, where the enrichment pass puts it.</summary>
    private void MarkUniverseEnriched(string marker, params DisplayWorkRow[] rows)
    {
        using var connection = _database.CreateConnection();
        foreach (var row in rows)
        {
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, @marker, '2026-01-01T00:00:00Z', datetime('now'));",
                new { id = row.AssetId, marker });
        }
    }

    private void SeedPersonCredits(Guid personId, Guid collectionId, Guid showWorkId, Guid[] episodeIds, Guid movieWorkId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO persons (id, name, created_at) VALUES (@personId, 'Dana Test', @now);
            INSERT INTO collections (id, display_name, collection_type, wikidata_qid, created_at)
            VALUES (@collectionId, 'Sample Crime Show', 'Series', 'QSHOW', @now);
            INSERT INTO works (id, collection_id, media_type, wikidata_qid, work_kind)
            VALUES (@showWorkId, @collectionId, 'TV', 'QSHOW', 'parent');
            INSERT INTO works (id, media_type, wikidata_qid, work_kind)
            VALUES (@movieWorkId, 'Movies', 'QFILM', 'standalone');
            INSERT INTO editions (id, work_id) VALUES (@movieEditionId, @movieWorkId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@movieAssetId, @movieEditionId, 'also-appears-film', 'C:/library/Sample Film.mkv');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@movieWorkId, 'title', 'Sample Film', @now), (@movieWorkId, 'year', '2015', @now);
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
            VALUES (@movieWorkId, 'director', 0, 'Dana Test');
            """,
            new
            {
                personId,
                collectionId,
                showWorkId,
                movieWorkId,
                movieEditionId = Guid.NewGuid(),
                movieAssetId = Guid.NewGuid(),
                now,
            });

        foreach (var (workId, index) in episodeIds.Select((id, index) => (id, index)))
        {
            connection.Execute(
                """
                INSERT INTO works (id, parent_work_id, collection_id, media_type, wikidata_qid, work_kind, ordinal)
                VALUES (@workId, @showWorkId, @collectionId, 'TV', @workQid, 'child', @ordinal);
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@assetId, @editionId, @hash, @path);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@workId, 'title', @title, @now), (@workId, 'year', '2008', @now);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@workId, 'director', 0, 'Dana Test'), (@workId, 'cast_member', 0, 'Dana Test');
                """,
                new
                {
                    workId,
                    showWorkId,
                    collectionId,
                    workQid = "QEP" + workId.ToString("N")[..6],
                    ordinal = index + 1,
                    editionId = Guid.NewGuid(),
                    assetId = Guid.NewGuid(),
                    hash = $"also-appears-episode-{index}",
                    path = $"C:/library/Sample Crime Show/S01E0{index + 1}.mkv",
                    title = $"Episode {index + 1}",
                    now,
                });
        }
    }

    private static DisplayWorkRow Row(Guid workId, string mediaType, string identityQid, string title, int year, Guid? collectionId = null) =>
        new()
        {
            WorkId = workId,
            AssetId = Guid.NewGuid(),
            RootWorkId = workId,
            EditionId = Guid.NewGuid(),
            MediaType = mediaType,
            IdentityQid = identityQid,
            Title = title,
            Year = year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CollectionId = collectionId,
            CoverUrl = $"/stream/artwork/{workId:D}",
            CreatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>One owned episode: its own work id, but the show's root id, which is what groups episodes into a show.</summary>
    private static DisplayWorkRow TvEpisode(
        Guid showRootId, Guid episodeId, string identityQid, string title, int year, Guid? collectionId = null, int? season = null, int? number = null)
    {
        var row = Row(episodeId, "TV", identityQid, title, year, collectionId);
        row.RootWorkId = showRootId;
        row.SeasonNumber = season?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.EpisodeNumber = number?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.ShowName = "Lanterns";
        row.RootCoverUrl = $"/stream/artwork/{showRootId:D}";
        return row;
    }

    private sealed class DisplayStub(IReadOnlyList<DisplayWorkRow> works) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(authority);
    }
}
