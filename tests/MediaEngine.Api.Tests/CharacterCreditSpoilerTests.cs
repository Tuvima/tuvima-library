using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The one spoiler rule on every "as Character" credit: title cast lists, person pages and the people search. The actor
/// always stays; only the characters the requesting profile has not reached are left off. Real temp SQLite; the request is a
/// hand-made HTTP context for the seed profile.
/// </summary>
public sealed class CharacterCreditSpoilerTests : IDisposable
{
    private const string FilmQid = "Q-credit-film";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_credit_spoilers_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _work = Guid.NewGuid();
    private readonly Guid _edition = Guid.NewGuid();
    private readonly Guid _asset = Guid.NewGuid();
    private readonly Guid _person = Guid.NewGuid();
    private readonly Guid _hiddenCharacter = Guid.NewGuid();
    private readonly Guid _shownCharacter = Guid.NewGuid();

    public CharacterCreditSpoilerTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        Seed();
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { }
        try { File.Delete(_databasePath); } catch { }
    }

    [Fact]
    public async Task ATitleCastList_KeepsTheActor_AndLeavesOffACharacterTheViewerHasNotReached()
    {
        await SetLevelAsync(SpoilerLevels.UpToProgress);

        var credits = await CreateCredits(withRequest: true).BuildForWorkAsync(_work, CancellationToken.None);

        var actor = Assert.Single(credits, credit => credit.PersonId == _person);
        Assert.Equal(["Visible Role"], actor.Characters.Select(character => character.CharacterName));
        Assert.Equal("Visible Role", actor.CharacterName);
    }

    [Fact]
    public async Task ATitleCastList_ShowsEveryCharacter_AtTheLevelEverything()
    {
        await SetLevelAsync(SpoilerLevels.Everything);

        var credits = await CreateCredits(withRequest: true).BuildForWorkAsync(_work, CancellationToken.None);

        var actor = Assert.Single(credits, credit => credit.PersonId == _person);
        Assert.Equal(["Secret Role", "Visible Role"], actor.Characters.Select(character => character.CharacterName).Order());
    }

    [Fact]
    public async Task WorkOutsideARequest_IsNotFiltered()
    {
        await SetLevelAsync(SpoilerLevels.UpToProgress);

        var credits = await CreateCredits(withRequest: false).BuildForWorkAsync(_work, CancellationToken.None);

        var actor = Assert.Single(credits, credit => credit.PersonId == _person);
        Assert.Equal(2, actor.Characters.Count);
    }

    [Fact]
    public async Task APersonPagesCredits_KeepTheWork_AndLeaveOffACharacterTheViewerHasNotReached()
    {
        await SetLevelAsync(SpoilerLevels.UpToProgress);

        var credits = await CreateCredits(withRequest: true).GetLibraryCreditsAsync(_person, CancellationToken.None);

        var actorCredit = Assert.Single(credits, credit => credit.Role == "Actor");
        Assert.Equal(["Visible Role"], actorCredit.Characters.Select(character => character.CharacterName));
    }

    [Fact]
    public async Task APersonPagesCredits_ShowEveryCharacter_AtTheLevelEverything()
    {
        await SetLevelAsync(SpoilerLevels.Everything);

        var credits = await CreateCredits(withRequest: true).GetLibraryCreditsAsync(_person, CancellationToken.None);

        var actorCredit = Assert.Single(credits, credit => credit.Role == "Actor");
        Assert.Equal(2, actorCredit.Characters.Count);
    }

    [Fact]
    public async Task TheFilter_EditsBothKindsOfCreditAndLeavesNameOnlyCharactersAlone()
    {
        await SetLevelAsync(SpoilerLevels.UpToProgress);
        var filter = CreateFilter();
        var cast = new List<CastCreditDto>
        {
            new()
            {
                Name = "Actor",
                Characters =
                [
                    new CharacterPortrayalDto { FictionalEntityId = _hiddenCharacter, CharacterName = "Secret Role" },
                    new CharacterPortrayalDto { FictionalEntityId = Guid.Empty, CharacterName = "Named Only" },
                ],
            },
        };
        var library = new List<PersonLibraryCreditDto>
        {
            new() { Role = "Actor", Characters = [new CharacterPortrayalDto { FictionalEntityId = _hiddenCharacter, CharacterName = "Secret Role" }] },
        };

        await filter.FilterAsync(cast);
        await filter.FilterAsync(library);

        Assert.Equal(["Named Only"], cast[0].Characters.Select(character => character.CharacterName));
        Assert.Empty(library[0].Characters);
    }

    [Fact]
    public async Task ThePeopleSearch_DoesNotMatchOrNameACharacterTheViewerHasNotReached()
    {
        await SetLevelAsync(SpoilerLevels.UpToProgress);
        var service = CreateSearch();

        var hidden = await service.SearchAsync("Secret Role", 20, CancellationToken.None);
        var shown = await service.SearchAsync("Visible Role", 20, CancellationToken.None);

        Assert.DoesNotContain(hidden.Sections.SelectMany(section => section.Results), result => result.Id == _person);
        var person = Assert.Single(shown.Sections.Single(section => section.Key == "people").Results);
        Assert.Equal(_person, person.Id);
        Assert.Equal("Plays Visible Role", person.Subtitle);
    }

    [Fact]
    public async Task ThePeopleSearch_StillMatchesTheActorByName_WithoutNamingHiddenCharacters()
    {
        await SetLevelAsync(SpoilerLevels.UpToProgress);

        var response = await CreateSearch().SearchAsync("Test Actor", 20, CancellationToken.None);

        var person = Assert.Single(response.Sections.Single(section => section.Key == "people").Results);
        Assert.Equal(_person, person.Id);
        Assert.DoesNotContain("Secret", person.Subtitle ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePeopleSearch_MatchesAHiddenCharacter_AtTheLevelEverything()
    {
        await SetLevelAsync(SpoilerLevels.Everything);

        var response = await CreateSearch().SearchAsync("Secret Role", 20, CancellationToken.None);

        var person = Assert.Single(response.Sections.Single(section => section.Key == "people").Results);
        Assert.Equal("Plays Secret Role", person.Subtitle);
    }

    // Setup -------------------------------------------------------------------------------------------------------

    private void Seed()
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, wikidata_qid) VALUES (@work, 'Movies', 'standalone', @filmQid);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@asset, @edition, @hash, 'C:/library/Credit Film.mkv');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@work, 'title', 'Credit Film', CURRENT_TIMESTAMP);
            INSERT INTO persons (id, name, created_at) VALUES (@person, 'Test Actor', CURRENT_TIMESTAMP);
            INSERT INTO person_roles (person_id, role) VALUES (@person, 'Actor');
            INSERT INTO person_media_links (media_asset_id, person_id, role) VALUES (@asset, @person, 'Actor');
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@work, 'cast_member', 0, 'Test Actor');
            INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, fictional_universe_qid, created_at)
            VALUES (@hidden, 'Q-secret', 'Secret Role', 'Character', 'Q-credit-universe', CURRENT_TIMESTAMP),
                   (@shown, 'Q-visible', 'Visible Role', 'Character', 'Q-credit-universe', CURRENT_TIMESTAMP);
            INSERT INTO character_performer_links (person_id, fictional_entity_id, work_qid)
            VALUES (@person, @hidden, @filmQid), (@person, @shown, @filmQid);
            -- Visible Role is plainly in the film; Secret Role is marked as a spoiler for a work the profile does not own.
            INSERT INTO fictional_entity_work_links (id, appearance_key, entity_id, work_qid)
            VALUES (@linkOne, @keyOne, @shown, @filmQid);
            INSERT INTO fictional_entity_work_links (id, appearance_key, entity_id, work_qid, spoiler_for_work_qid)
            VALUES (@linkTwo, @keyTwo, @hidden, @filmQid, 'Q-not-owned');
            """,
            new
            {
                work = _work,
                edition = _edition,
                asset = _asset,
                person = _person,
                hidden = _hiddenCharacter,
                shown = _shownCharacter,
                filmQid = FilmQid,
                hash = _asset.ToString("N"),
                linkOne = Guid.NewGuid(),
                linkTwo = Guid.NewGuid(),
                keyOne = $"{_shownCharacter:N}:{FilmQid}",
                keyTwo = $"{_hiddenCharacter:N}:{FilmQid}:spoiler",
            });
    }

    private Task SetLevelAsync(string level) =>
        new UserPlaybackSettingsService(_database, new ProfileRepository(_database))
            .SetSpoilerLevelAsync(Profile.SeedProfileId, level);

    private DisplayWorkRow[] Rows() =>
    [
        new DisplayWorkRow
        {
            WorkId = _work,
            RootWorkId = _work,
            AssetId = _asset,
            EditionId = _edition,
            MediaType = "Movies",
            IdentityQid = FilmQid,
            Title = "Credit Film",
        },
    ];

    private CharacterCreditSpoilerFilter CreateFilter() =>
        new(
            SpoilerTestKit.Create(new Display(Rows()), _database),
            new HttpContextAccessor { HttpContext = RequestContext() },
            new ProfileRepository(_database));

    private HttpContext RequestContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestAuthorityResolver>(TestViewAuthorityResolver.Human(Profile.SeedProfileId));
        var context = new DefaultHttpContext();
        var provider = services.BuildServiceProvider();
        context.RequestServices = new FilterServices(provider, this);
        return context;
    }

    private PersonCreditReadService CreateCredits(bool withRequest) =>
        new(
            new CanonicalValueArrayRepository(_database),
            new PersonRepository(_database),
            _database,
            withRequest ? new HttpContextAccessor { HttpContext = RequestContext() } : null);

    private UniversalSearchReadService CreateSearch()
    {
        var display = new Display(Rows());
        return new UniversalSearchReadService(
            _database,
            display,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            TestViewAuthorityResolver.Human(Profile.SeedProfileId),
            SpoilerTestKit.Create(display, _database),
            new ProfileRepository(_database));
    }

    // The request's services: the real resolver for the seed profile plus the filter built over this test's data.
    private sealed class FilterServices(IServiceProvider inner, CharacterCreditSpoilerTests owner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(CharacterCreditSpoilerFilter) ? owner.CreateFilter() : inner.GetService(serviceType);
    }

    private sealed class Display(IReadOnlyList<DisplayWorkRow> works) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
