using MediaEngine.AI.Configuration;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Taste;
using MediaEngine.Contracts.Taste;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Whose taste a person may see and change, and how their own removals and additions sit on top of what the Library
/// learned. Real scope authorizer, audit log and data store; the taste profiler is a hand-written spy.
/// </summary>
public sealed class PersonalTasteServiceTests : IAsyncLifetime
{
    private PersonalActivityWorld _world = null!;
    private SpyTasteProfiler _profiler = null!;
    private TasteOverrideRepository _overrides = null!;
    private PersonalTasteService _service = null!;

    public Task InitializeAsync()
    {
        _world = PersonalActivityWorld.Create();
        _profiler = new SpyTasteProfiler();
        _overrides = new TasteOverrideRepository(_world.Database);
        _service = new PersonalTasteService(
            _world.Scope, _profiler, _overrides, new ProfileRepository(_world.Database), new FixedVibeVocabulary(), _world.Clock);
        foreach (var person in new[] { _world.ServerPerson, _world.AdminA, _world.MemberA, _world.KidA, _world.AdminB, _world.MemberB })
        {
            _profiler.Learn(person.ProfileId, genres: new() { ["sci-fi"] = 0.6, ["drama"] = 0.4 }, moods: new() { ["cozy"] = 0.7 }, eras: new() { ["1990s"] = 1 });
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _world.DisposeAsync().AsTask();

    // ----- Reading -----

    [Fact]
    public async Task AMember_SeesTheirOwnLearnedTaste_WithoutNamesOrALogEntry()
    {
        var result = await _service.GetAsync(_world.MemberOfA, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var view = result.Value!;
        var person = Assert.Single(view.People);
        Assert.Equal("me", view.Scope);
        Assert.False(view.IncludesOthers);
        Assert.True(person.IsYou);
        Assert.Null(person.Person);
        Assert.Equal(["sci-fi", "drama"], person.Genres.Select(item => item.Value));
        Assert.All(person.Genres, item => Assert.Equal(TasteValues.SourceLearned, item.Source));
        Assert.Equal(0, _world.AuditCount(PersonalScopeAuthorizer.TasteViewedOthers));
    }

    [Fact]
    public async Task YourRemovalsAreHidden_AndYourAdditionsAreMarkedAsAddedByYou()
    {
        await _service.RemoveAsync(_world.MemberOfA, TasteValues.Genres, "Sci-Fi", CancellationToken.None);
        await _service.AddAsync(_world.MemberOfA, TasteValues.Moods, "  Gritty  ", CancellationToken.None);
        await _service.AddAsync(_world.MemberOfA, TasteValues.Eras, "1980s", CancellationToken.None);

        var person = Assert.Single((await _service.GetAsync(_world.MemberOfA, "me", CancellationToken.None)).Value!.People);

        Assert.Equal(["drama"], person.Genres.Select(item => item.Value));
        Assert.Equal(["cozy", "gritty"], person.Moods.Select(item => item.Value));
        var added = person.Moods.Single(item => item.Value == "gritty");
        Assert.Equal(TasteValues.SourceAdded, added.Source);
        Assert.Null(added.Weight);
        Assert.Equal(TasteValues.SourceLearned, person.Moods.Single(item => item.Value == "cozy").Source);
        Assert.Equal(["1990s", "1980s"], person.Eras.Select(item => item.Value));
    }

    [Fact]
    public async Task TheLearnedProfile_IsNeverChangedByEdits()
    {
        await _service.RemoveAsync(_world.MemberOfA, TasteValues.Genres, "drama", CancellationToken.None);

        var learned = await _profiler.GetProfileAsync(_world.MemberA.ProfileId);

        Assert.Equal(["sci-fi", "drama"], learned.Profile!.GenreDistribution.Keys);
    }

    [Fact]
    public async Task AddingAValueThatWasRemoved_BringsItBack()
    {
        await _service.RemoveAsync(_world.MemberOfA, TasteValues.Genres, "drama", CancellationToken.None);
        await _service.AddAsync(_world.MemberOfA, TasteValues.Genres, "drama", CancellationToken.None);

        var person = Assert.Single((await _service.GetAsync(_world.MemberOfA, null, CancellationToken.None)).Value!.People);

        Assert.Contains(person.Genres, item => item.Value == "drama");
    }

    [Fact]
    public async Task APersonWithNothingLearnedYet_StillSeesWhatTheyAdded()
    {
        _profiler.Forget(_world.MemberA.ProfileId);
        await _service.AddAsync(_world.MemberOfA, TasteValues.Genres, "documentary", CancellationToken.None);

        var person = Assert.Single((await _service.GetAsync(_world.MemberOfA, null, CancellationToken.None)).Value!.People);

        var item = Assert.Single(person.Genres);
        Assert.Equal("documentary", item.Value);
        Assert.Null(person.LastUpdatedAt);
    }

    [Fact]
    public async Task TheAddControlsSuggestKnownValues()
    {
        var suggestions = (await _service.GetAsync(_world.MemberOfA, null, CancellationToken.None)).Value!.Suggestions;

        Assert.Contains("cozy", suggestions.Moods);
        Assert.Contains("1990s", suggestions.Eras);
        Assert.Equal("2020s", suggestions.Eras[0]);
    }

    // ----- Scope: who may see whose taste -----

    [Fact]
    public async Task AMember_CannotWidenTheScope()
    {
        foreach (var scope in new[]
                 {
                     "everyone", $"household:{_world.HouseholdA}", $"profile:{_world.AdminA.ProfileId}", $"profile:{_world.AdminB.ProfileId}",
                 })
        {
            var result = await _service.GetAsync(_world.MemberOfA, scope, CancellationToken.None);

            Assert.Equal(PersonalTasteFailure.Forbidden, result.Failure);
        }

        Assert.Equal(0, _world.AuditCount(PersonalScopeAuthorizer.TasteViewedOthers));
    }

    [Fact]
    public async Task AKidsProfile_SeesOnlyTheirOwnTaste()
    {
        Assert.True((await _service.GetAsync(_world.KidOfA, "me", CancellationToken.None)).IsSuccess);
        Assert.Equal(PersonalTasteFailure.Forbidden, (await _service.GetAsync(_world.KidOfA, "everyone", CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task AHouseholdAdmin_SeesTheirOwnHouseholdAndItsPeople_AndTheyAreNamed()
    {
        var household = await _service.GetAsync(_world.AdminOfA, $"household:{_world.HouseholdA}", CancellationToken.None);
        var friend = await _service.GetAsync(_world.AdminOfA, $"profile:{_world.MemberA.ProfileId}", CancellationToken.None);

        Assert.True(household.IsSuccess);
        Assert.True(household.Value!.IncludesOthers);
        Assert.Equal(["Alex", "Friend of Alex", "Kid of Alex"], household.Value.People.Select(person => person.Person));
        Assert.True(household.Value.People[0].IsYou);
        Assert.Equal("Friend of Alex", Assert.Single(friend.Value!.People).Person);
    }

    [Fact]
    public async Task AHouseholdAdmin_IsRefusedOtherHouseholdsAndEveryone_WithoutAnyHint()
    {
        var otherHousehold = await _service.GetAsync(_world.AdminOfA, $"household:{_world.HouseholdB}", CancellationToken.None);
        var otherPerson = await _service.GetAsync(_world.AdminOfA, $"profile:{_world.MemberB.ProfileId}", CancellationToken.None);
        var missingPerson = await _service.GetAsync(_world.AdminOfA, $"profile:{Guid.NewGuid()}", CancellationToken.None);
        var everyone = await _service.GetAsync(_world.AdminOfA, "everyone", CancellationToken.None);

        Assert.All(new[] { otherHousehold, otherPerson, missingPerson, everyone }, result =>
            Assert.Equal(PersonalTasteFailure.Forbidden, result.Failure));
        Assert.Equal(otherPerson.Message, missingPerson.Message);
        Assert.Equal(0, _world.AuditCount(PersonalScopeAuthorizer.TasteViewedOthers));
    }

    [Fact]
    public async Task AServerAdmin_SeesEveryone_WithEveryNameShown()
    {
        var result = await _service.GetAsync(_world.ServerAdmin, "everyone", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var seeded = new[] { _world.ServerPerson, _world.AdminA, _world.MemberA, _world.KidA, _world.AdminB, _world.MemberB };
        var shownPeople = result.Value!.People;
        Assert.All(seeded, person => Assert.Contains(shownPeople, shown => shown.ProfileId == person.ProfileId.ToString("D")));
        Assert.All(shownPeople, person => Assert.False(string.IsNullOrEmpty(person.Person)));
        Assert.True(shownPeople[0].IsYou);
        Assert.Single(shownPeople, person => person.IsYou);
    }

    [Fact]
    public async Task AServerAdmin_AskingForAnUnknownPerson_IsNotFound()
    {
        var result = await _service.GetAsync(_world.ServerAdmin, $"profile:{Guid.NewGuid()}", CancellationToken.None);

        Assert.Equal(PersonalTasteFailure.NotFound, result.Failure);
    }

    [Fact]
    public async Task AServerAdminWhoHasNotUnlocked_CannotSeeOthers_ButSeesTheirOwn()
    {
        Assert.True((await _service.GetAsync(_world.LockedServerAdmin, "me", CancellationToken.None)).IsSuccess);
        var others = await _service.GetAsync(_world.LockedServerAdmin, "everyone", CancellationToken.None);

        Assert.Equal(PersonalTasteFailure.Forbidden, others.Failure);
    }

    [Fact]
    public async Task APairedApp_SeesItsOwnPersonOnly()
    {
        var own = await _service.GetAsync(_world.ServerAdminApp, "me", CancellationToken.None);
        var others = await _service.GetAsync(_world.ServerAdminApp, "everyone", CancellationToken.None);
        var household = await _service.GetAsync(_world.ServerAdminApp, $"household:{_world.HouseholdA}", CancellationToken.None);

        Assert.True(own.IsSuccess);
        Assert.Single(own.Value!.People);
        Assert.Equal(PersonalTasteFailure.Forbidden, others.Failure);
        Assert.Equal(PersonalTasteFailure.Forbidden, household.Failure);
    }

    [Fact]
    public async Task AnInvalidScope_IsABadRequest()
    {
        Assert.Equal(PersonalTasteFailure.BadRequest, (await _service.GetAsync(_world.MemberOfA, "nobody", CancellationToken.None)).Failure);
        Assert.Equal(PersonalTasteFailure.BadRequest, (await _service.GetAsync(_world.ServerAdmin, "profile:not-an-id", CancellationToken.None)).Failure);
    }

    // ----- Logging -----

    [Fact]
    public async Task ViewingOthers_WritesOneLogEntryPerHalfHour_NamingWhoLookedAtWhom()
    {
        var scope = $"household:{_world.HouseholdA}";

        await _service.GetAsync(_world.ServerAdmin, scope, CancellationToken.None);
        await _service.GetAsync(_world.ServerAdmin, scope, CancellationToken.None);
        Assert.Equal(1, _world.AuditCount(PersonalScopeAuthorizer.TasteViewedOthers));

        _world.Clock.Advance(TimeSpan.FromMinutes(31));
        await _service.GetAsync(_world.ServerAdmin, scope, CancellationToken.None);
        await _service.GetAsync(_world.ServerAdmin, "everyone", CancellationToken.None);

        var rows = _world.AuditRows(PersonalScopeAuthorizer.TasteViewedOthers);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(_world.ServerPerson.ProfileId, row.ActorProfileId));
        Assert.Equal(["household", "household", "everyone"], rows.Select(row => row.SubjectType));
        Assert.Equal(_world.HouseholdA.ToString("D"), rows[0].SubjectId);
        Assert.Equal(0, _world.AuditCount(PersonalScopeAuthorizer.ActivityViewedOthers));
    }

    [Fact]
    public async Task ViewingYourOwnTaste_IsNotLogged_EvenWhenAskedForByYourOwnId()
    {
        await _service.GetAsync(_world.AdminOfA, "me", CancellationToken.None);
        await _service.GetAsync(_world.AdminOfA, $"profile:{_world.AdminA.ProfileId}", CancellationToken.None);

        Assert.Equal(0, _world.AuditCount(PersonalScopeAuthorizer.TasteViewedOthers));
    }

    // ----- Editing: your own taste only -----

    [Fact]
    public async Task EveryoneEditsOnlyTheirOwnTaste_AndNobodyElsesIsTouched()
    {
        foreach (var authority in new[] { _world.MemberOfA, _world.AdminOfA, _world.ServerAdmin, _world.KidOfA })
        {
            Assert.True((await _service.AddAsync(authority, TasteValues.Genres, "western", CancellationToken.None)).IsSuccess);
        }

        var changes = await _overrides.ListAsync(null);
        Assert.Equal(
            new[] { _world.MemberA.ProfileId, _world.AdminA.ProfileId, _world.ServerPerson.ProfileId, _world.KidA.ProfileId }.Order(),
            changes.Select(change => change.ProfileId).Order());
    }

    [Fact]
    public async Task ThereIsNoWayToEditSomeoneElsesTaste_EvenForAServerAdmin()
    {
        // The edit actions carry no person: they always act on the signed-in profile, so an administrator's change lands on their own taste.
        await _service.AddAsync(_world.ServerAdmin, TasteValues.Moods, "bleak", CancellationToken.None);

        var memberView = Assert.Single((await _service.GetAsync(_world.MemberOfA, null, CancellationToken.None)).Value!.People);
        Assert.DoesNotContain(memberView.Moods, item => item.Value == "bleak");
        Assert.Contains(
            Assert.Single((await _service.GetAsync(_world.ServerAdmin, "me", CancellationToken.None)).Value!.People).Moods,
            item => item.Value == "bleak");
    }

    [Fact]
    public async Task ViewingAnotherPersonsTaste_ShowsTheirChanges_ButOffersNoWayToMakeThem()
    {
        await _service.AddAsync(_world.MemberOfA, TasteValues.Genres, "western", CancellationToken.None);

        var seenByAdmin = (await _service.GetAsync(_world.AdminOfA, $"profile:{_world.MemberA.ProfileId}", CancellationToken.None)).Value!;

        var person = Assert.Single(seenByAdmin.People);
        Assert.False(person.IsYou);
        Assert.Contains(person.Genres, item => item.Value == "western" && item.Source == TasteValues.SourceAdded);
    }

    [Fact]
    public async Task APairedApp_CannotEditTaste()
    {
        var add = await _service.AddAsync(_world.ServerAdminApp, TasteValues.Genres, "western", CancellationToken.None);
        var remove = await _service.RemoveAsync(_world.ServerAdminApp, TasteValues.Genres, "drama", CancellationToken.None);

        Assert.Equal(PersonalTasteFailure.Forbidden, add.Failure);
        Assert.Equal(PersonalTasteFailure.Forbidden, remove.Failure);
        Assert.Empty(await _overrides.ListAsync(null));
    }

    [Fact]
    public async Task SomeoneNotSignedIn_CannotEditOrView()
    {
        var nobody = new RequestAuthority(PrincipalKind.Anonymous, false);

        Assert.Equal(PersonalTasteFailure.Forbidden, (await _service.AddAsync(nobody, TasteValues.Genres, "western", CancellationToken.None)).Failure);
        Assert.Equal(PersonalTasteFailure.Forbidden, (await _service.GetAsync(nobody, null, CancellationToken.None)).Failure);
        Assert.Empty(await _overrides.ListAsync(null));
    }

    [Theory]
    [InlineData("flavours", "sweet")]
    [InlineData("genres", "")]
    [InlineData("genres", "   ")]
    [InlineData("moods", "this value is far too long to be a mood, genre or anything a person would reasonably type here")]
    [InlineData("eras", "the nineties")]
    [InlineData("eras", "1995")]
    [InlineData("genres", "bad\u0007value")]
    public async Task AnInvalidGroupOrValue_IsABadRequestAndStoresNothing(string group, string value)
    {
        var add = await _service.AddAsync(_world.MemberOfA, group, value, CancellationToken.None);
        var remove = await _service.RemoveAsync(_world.MemberOfA, group, value, CancellationToken.None);

        Assert.Equal(PersonalTasteFailure.BadRequest, add.Failure);
        Assert.Equal(PersonalTasteFailure.BadRequest, remove.Failure);
        Assert.Empty(await _overrides.ListAsync(null));
    }

    [Fact]
    public async Task TheOverviewSummary_FollowsYourEdits()
    {
        var taste = new TasteProfile
        {
            UserId = _world.MemberA.ProfileId,
            GenreDistribution = new Dictionary<string, double> { ["sci-fi"] = 0.6, ["drama"] = 0.4 },
        };
        await _service.RemoveAsync(_world.MemberOfA, TasteValues.Genres, "sci-fi", CancellationToken.None);
        await _service.AddAsync(_world.MemberOfA, TasteValues.Genres, "western", CancellationToken.None);

        var applied = TasteOverrides.Apply(taste, await _overrides.ListAsync([_world.MemberA.ProfileId]));

        Assert.Equal(["western", "drama"], applied.GenreDistribution.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Select(pair => pair.Key));
    }

    private sealed class SpyTasteProfiler : ITasteProfiler
    {
        private readonly Dictionary<Guid, TasteProfile> _profiles = [];

        public void Learn(
            Guid profileId,
            Dictionary<string, double> genres,
            Dictionary<string, double> moods,
            Dictionary<string, double> eras) =>
            _profiles[profileId] = new TasteProfile
            {
                UserId = profileId,
                GenreDistribution = genres,
                MoodPreferences = moods,
                EraPreferences = eras,
                MediaTypeMix = new Dictionary<string, double> { ["Movies"] = 1 },
                Summary = "You like stories.",
                LastUpdatedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            };

        public void Forget(Guid profileId) => _profiles.Remove(profileId);

        public Task<TasteProfileBuildResult> GetProfileAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(_profiles.TryGetValue(userId, out var profile)
                ? new TasteProfileBuildResult(TasteProfileBuildStatus.Generated, userId, profile, 10, "spy")
                : new TasteProfileBuildResult(TasteProfileBuildStatus.InsufficientData, userId, null, 0, "spy", "Not enough yet."));
    }
}
