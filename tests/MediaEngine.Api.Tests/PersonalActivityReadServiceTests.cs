using System.Text;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Activity;
using MediaEngine.Contracts.Activity;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Who may read, export and delete whose activity, and what comes back. Real temp data store, the real scope
/// authorizer and audit log; every refusal is tested.
/// </summary>
public sealed class PersonalActivityReadServiceTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    // ----- Whose activity -----

    [Fact]
    public async Task AMember_SeesOnlyTheirOwnActivity_AndCannotAskForAnyoneElses()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Mine", Day);
        await world.AddAsync(world.AdminA, PersonalActivityKinds.Watch, "Theirs", Day);

        var own = await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery(), default);

        var page = Assert.IsType<PersonalActivityPageDto>(own.Value);
        Assert.Equal(["Mine"], page.Items.Select(item => item.Title));
        Assert.False(page.IncludesOthers);
        Assert.All(page.Items, item => Assert.Null(item.Person));

        foreach (var scope in new[]
                 {
                     "everyone",
                     $"household:{world.HouseholdA}",
                     $"profile:{world.AdminA.ProfileId}",
                     $"profile:{world.MemberB.ProfileId}",
                 })
        {
            var refused = await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery { Scope = scope }, default);
            Assert.Equal(PersonalActivityFailure.Forbidden, refused.Failure);
            Assert.Null(refused.Value);
        }

        Assert.Equal(0, world.AuditCount(PersonalScopeAuthorizer.ActivityViewedOthers));
    }

    [Fact]
    public async Task AskingForYourOwnProfileByIdIsTheSameAsMe()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Mine", Day);

        var result = await world.Service.ListAsync(
            world.MemberOfA, new PersonalActivityQuery { Scope = $"profile:{world.MemberA.ProfileId}" }, default);

        Assert.Equal(["Mine"], result.Value!.Items.Select(item => item.Title));
        Assert.False(result.Value.IncludesOthers);
    }

    [Fact]
    public async Task AChildProfile_SeesOnlyTheirOwnActivity()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.KidA, PersonalActivityKinds.Watch, "Cartoon", Day);

        var refused = await world.Service.ListAsync(
            world.KidOfA, new PersonalActivityQuery { Scope = $"household:{world.HouseholdA}" }, default);
        var own = await world.Service.ListAsync(world.KidOfA, new PersonalActivityQuery(), default);

        Assert.Equal(PersonalActivityFailure.Forbidden, refused.Failure);
        Assert.Equal(["Cartoon"], own.Value!.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task AHouseholdAdministrator_SeesTheirOwnHouseholdOnly_NotAnotherHouseholdOrEveryone()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.AdminA, PersonalActivityKinds.Watch, "Alex film", Day);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Friend film", Day.AddDays(1));
        await world.AddAsync(world.MemberB, PersonalActivityKinds.Watch, "Bea friend film", Day.AddDays(2));

        var household = await world.Service.ListAsync(
            world.AdminOfA, new PersonalActivityQuery { Scope = $"household:{world.HouseholdA}" }, default);
        var oneProfile = await world.Service.ListAsync(
            world.AdminOfA, new PersonalActivityQuery { Scope = $"profile:{world.MemberA.ProfileId}" }, default);

        Assert.Equal(["Friend film", "Alex film"], household.Value!.Items.Select(item => item.Title));
        Assert.True(household.Value.IncludesOthers);
        Assert.Equal(["Friend of Alex", "Alex"], household.Value.Items.Select(item => item.Person!.Name));
        Assert.Equal(["Friend film"], oneProfile.Value!.Items.Select(item => item.Title));

        foreach (var scope in new[]
                 {
                     "everyone",
                     $"household:{world.HouseholdB}",
                     $"profile:{world.MemberB.ProfileId}",
                     $"profile:{Guid.NewGuid()}",
                 })
        {
            var refused = await world.Service.ListAsync(world.AdminOfA, new PersonalActivityQuery { Scope = scope }, default);
            Assert.Equal(PersonalActivityFailure.Forbidden, refused.Failure);
        }
    }

    [Fact]
    public async Task AServerAdministrator_SeesEveryone_WithAPersonOnEveryRow()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.AdminA, PersonalActivityKinds.Watch, "A", Day);
        await world.AddAsync(world.MemberB, PersonalActivityKinds.Listen, "B", Day.AddDays(1), mediaType: "Audiobooks");

        var everyone = await world.Service.ListAsync(world.ServerAdmin, new PersonalActivityQuery { Scope = "everyone" }, default);
        var oneHousehold = await world.Service.ListAsync(
            world.ServerAdmin, new PersonalActivityQuery { Scope = $"household:{world.HouseholdB}" }, default);

        Assert.True(everyone.Value!.IncludesOthers);
        Assert.Equal([("B", "Friend of Bea"), ("A", "Alex")], everyone.Value.Items.Select(item => (item.Title, item.Person!.Name)));
        Assert.Equal(world.MemberB.ProfileId.ToString("D"), everyone.Value.Items[0].Person!.ProfileId);
        Assert.Equal(["B"], oneHousehold.Value!.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task AServerAdministratorAskingForAnUnknownPerson_GetsNotFound()
    {
        await using var world = PersonalActivityWorld.Create();

        var unknown = await world.Service.ListAsync(
            world.ServerAdmin, new PersonalActivityQuery { Scope = $"profile:{Guid.NewGuid()}" }, default);

        Assert.Equal(PersonalActivityFailure.NotFound, unknown.Failure);
    }

    [Fact]
    public async Task AServerAdministratorWhoHasNotUnlocked_SeesOnlyTheirOwnActivity()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.ServerPerson, PersonalActivityKinds.Watch, "Mine", Day);

        var others = await world.Service.ListAsync(world.LockedServerAdmin, new PersonalActivityQuery { Scope = "everyone" }, default);
        var own = await world.Service.ListAsync(world.LockedServerAdmin, new PersonalActivityQuery(), default);

        Assert.Equal(PersonalActivityFailure.Forbidden, others.Failure);
        Assert.Contains("Unlock", others.Message, StringComparison.Ordinal);
        Assert.Equal(["Mine"], own.Value!.Items.Select(item => item.Title));
        Assert.Equal(0, world.AuditCount(PersonalScopeAuthorizer.ActivityViewedOthers));
    }

    [Fact]
    public async Task APairedApp_ActingForAServerAdministrator_GetsTheirOwnActivityOnly()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.ServerPerson, PersonalActivityKinds.Watch, "Mine", Day);
        await world.AddAsync(world.AdminA, PersonalActivityKinds.Watch, "Theirs", Day);

        var own = await world.Service.ListAsync(world.ServerAdminApp, new PersonalActivityQuery(), default);
        var recent = await world.Service.RecentAsync(world.ServerAdminApp, 20, default);

        Assert.Equal(["Mine"], own.Value!.Items.Select(item => item.Title));
        Assert.Equal(["Mine"], recent.Value!.Tiles.Select(tile => tile.Title));
        foreach (var scope in new[] { "everyone", $"household:{world.HouseholdA}", $"profile:{world.AdminA.ProfileId}" })
        {
            var refused = await world.Service.ListAsync(world.ServerAdminApp, new PersonalActivityQuery { Scope = scope }, default);
            Assert.Equal(PersonalActivityFailure.Forbidden, refused.Failure);
        }
    }

    [Fact]
    public async Task ASignedOutCaller_IsRefused()
    {
        await using var world = PersonalActivityWorld.Create();
        var nobody = new RequestAuthority(PrincipalKind.Anonymous, false);

        var result = await world.Service.ListAsync(nobody, new PersonalActivityQuery(), default);

        Assert.Equal(PersonalActivityFailure.Forbidden, result.Failure);
    }

    [Fact]
    public async Task ReadingOthersActivity_IsLoggedOncePerThirtyMinutesPerScope()
    {
        await using var world = PersonalActivityWorld.Create();
        var everyone = new PersonalActivityQuery { Scope = "everyone" };

        await world.Service.ListAsync(world.ServerAdmin, everyone, default);
        await world.Service.PeriodsAsync(world.ServerAdmin, everyone, default);
        await world.Service.ListAsync(world.ServerAdmin, everyone with { Cursor = null }, default);
        Assert.Single(world.AuditRows(PersonalScopeAuthorizer.ActivityViewedOthers));

        world.Clock.Advance(TimeSpan.FromMinutes(29));
        await world.Service.ListAsync(world.ServerAdmin, everyone, default);
        Assert.Single(world.AuditRows(PersonalScopeAuthorizer.ActivityViewedOthers));

        world.Clock.Advance(TimeSpan.FromMinutes(2));
        await world.Service.ListAsync(world.ServerAdmin, everyone, default);
        await world.Service.ListAsync(world.ServerAdmin, new PersonalActivityQuery { Scope = $"household:{world.HouseholdA}" }, default);

        var rows = world.AuditRows(PersonalScopeAuthorizer.ActivityViewedOthers);
        Assert.Equal(3, rows.Count);
        Assert.Equal(world.ServerPerson.ProfileId, rows[0].ActorProfileId);
        Assert.Equal(["everyone", "everyone", "household"], rows.Select(row => row.SubjectType));
    }

    [Fact]
    public async Task ReadingYourOwnActivity_IsNeverLogged()
    {
        await using var world = PersonalActivityWorld.Create();

        await world.Service.ListAsync(world.ServerAdmin, new PersonalActivityQuery(), default);
        await world.Service.RecentAsync(world.ServerAdmin, 20, default);

        Assert.Equal(0, world.AuditCount(PersonalScopeAuthorizer.ActivityViewedOthers));
    }

    // ----- Query validation -----

    [Theory]
    [InlineData("everybody", null, null, null, null)]
    [InlineData("profile:not-a-guid", null, null, null, null)]
    [InlineData(null, "dance", null, null, null)]
    [InlineData(null, null, "almost", null, null)]
    [InlineData(null, null, null, "decade", null)]
    [InlineData(null, null, null, null, "everything")]
    public async Task AQueryThatIsNotValid_IsRefusedBeforeAnyDataIsRead(
        string? scope, string? kind, string? outcome, string? group, string? expand)
    {
        await using var world = PersonalActivityWorld.Create();
        var query = new PersonalActivityQuery
        {
            Scope = scope,
            Kinds = kind is null ? null : [kind],
            Outcome = outcome,
            Group = group,
            Expand = expand,
        };

        var result = await world.Service.ListAsync(world.ServerAdmin, query, default);

        Assert.Equal(PersonalActivityFailure.BadRequest, result.Failure);
        Assert.Equal(0, world.AuditCount(PersonalScopeAuthorizer.ActivityViewedOthers));
    }

    [Fact]
    public async Task ADateRangeThatEndsBeforeItStarts_IsRefused()
    {
        await using var world = PersonalActivityWorld.Create();

        var result = await world.Service.ListAsync(
            world.MemberOfA, new PersonalActivityQuery { From = Day, To = Day.AddDays(-1) }, default);

        Assert.Equal(PersonalActivityFailure.BadRequest, result.Failure);
    }

    [Fact]
    public async Task KindsMayBeRepeatedOrCommaSeparated()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Film", Day);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Read, "Book", Day.AddDays(1), mediaType: "Books");
        await world.AddAsync(world.MemberA, PersonalActivityKinds.MyList, "Saved", Day.AddDays(2));

        var repeated = await world.Service.ListAsync(
            world.MemberOfA, new PersonalActivityQuery { Kinds = ["watch", "read"] }, default);
        var commas = await world.Service.ListAsync(
            world.MemberOfA, new PersonalActivityQuery { Kinds = ["watch,read"] }, default);

        Assert.Equal(["Book", "Film"], repeated.Value!.Items.Select(item => item.Title));
        Assert.Equal(["Book", "Film"], commas.Value!.Items.Select(item => item.Title));
    }

    // ----- What comes back -----

    [Fact]
    public async Task SearchingForATitle_GroupsItsAttemptsWithTotals()
    {
        await using var world = PersonalActivityWorld.Create();
        var tremors = Guid.NewGuid();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Tremors", Day, work: tremors, percent: 31, played: 1800, device: "Phone");
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Tremors", Day.AddDays(3), work: tremors, percent: 43, played: 720);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Tremors", Day.AddDays(7), work: tremors, percent: 100, played: 5760, finished: true);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Dune", Day.AddDays(8));

        var result = await world.Service.ListAsync(
            world.MemberOfA, new PersonalActivityQuery { Q = "tremors", Group = "title" }, default);

        var group = Assert.Single(result.Value!.Items);
        Assert.Equal("Tremors", group.Title);
        Assert.Equal(3, group.AttemptCount);
        Assert.Equal(1800 + 720 + 5760, group.PlayedSeconds);
        Assert.Equal(100, group.ProgressPct);
        Assert.True(group.Finished);
        Assert.Equal([31d, 43d, 100d], group.Attempts!.Select(attempt => attempt.ProgressPct!.Value));
        Assert.Equal("Phone", group.Attempts![0].DeviceName);
        Assert.Equal(tremors.ToString("D"), group.WorkId);
        Assert.Null(group.Songs);
    }

    [Fact]
    public async Task EntryRows_CarryTheirSnapshotAndNoIpAddressOrSignInDetail()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Severance", Day, subtitle: "S2 E4", mediaType: "TV");

        var item = Assert.Single((await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery(), default)).Value!.Items);

        Assert.Equal(PersonalActivityKinds.Watch, item.Kind);
        Assert.Equal("Severance", item.Title);
        Assert.Equal("S2 E4", item.Subtitle);
        Assert.Equal("TV", item.MediaType);
        Assert.Equal("Dashboard", item.DeviceName);
        var properties = typeof(PersonalActivityItemDto).GetProperties().Select(property => property.Name);
        Assert.DoesNotContain(properties, name => name.Contains("Ip", StringComparison.Ordinal) && name.Contains("Address", StringComparison.Ordinal));
        Assert.DoesNotContain(properties, name => name.Contains("Session", StringComparison.OrdinalIgnoreCase) && !name.Contains("Music", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AMusicSession_IsOneItemWithCountsAndAtMostNineRandomDistinctAlbumCovers()
    {
        await using var world = PersonalActivityWorld.Create();
        var covers = Enumerable.Range(0, 12).Select(album => Convert.ToHexStringLower(new byte[32].Select((_, index) => (byte)(album + index)).ToArray())).ToList();
        foreach (var cover in covers)
        {
            await world.Repository.SaveArtworkAsync(new PersonalActivityArtwork(cover, [1], "image/jpeg", 10, 10));
        }

        for (var song = 0; song < 50; song++)
        {
            await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, $"Song {song}", Day.AddMinutes(song * 4),
                mediaType: "Music", subtitle: "Pink Floyd", parentTitle: $"Album {song % 12}", artwork: covers[song % 12], played: 200, percent: 100);
        }

        var first = await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery(), default);
        var expanded = await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery { Expand = "songs" }, default);

        var session = Assert.Single(first.Value!.Items);
        Assert.Equal(PersonalActivityKindValues.MusicSession, session.Kind);
        Assert.Equal(50, session.SongCount);
        Assert.Equal(12, session.AlbumCount);
        Assert.Equal(50 * 200, session.PlayedSeconds);
        Assert.Equal(PersonalActivityReadService.MaxCoversPerSession, session.CoverUrls!.Count);
        Assert.Equal(session.CoverUrls.Count, session.CoverUrls.Distinct().Count());
        var allowed = covers.Select(cover => "/api/v1/activity/artwork/" + cover).ToHashSet();
        Assert.All(session.CoverUrls, url => Assert.Contains(url, allowed));
        Assert.Null(session.Songs);

        var songs = Assert.Single(expanded.Value!.Items).Songs!;
        Assert.Equal(50, songs.Count);
        Assert.Equal("Song 0", songs[0].Title);
        Assert.Equal("Song 49", songs[^1].Title);
    }

    [Fact]
    public async Task AMusicSessionWithFewAlbums_ListsOnlyThoseAlbumCovers()
    {
        await using var world = PersonalActivityWorld.Create();
        var one = new string('1', 64);
        var two = new string('2', 64);
        foreach (var cover in new[] { one, two })
        {
            await world.Repository.SaveArtworkAsync(new PersonalActivityArtwork(cover, [1], "image/jpeg", 10, 10));
        }

        await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, "A", Day, mediaType: "Music", parentTitle: "One", artwork: one);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, "B", Day.AddMinutes(4), mediaType: "Music", parentTitle: "One", artwork: one);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, "C", Day.AddMinutes(8), mediaType: "Music", parentTitle: "Two", artwork: two);

        var session = Assert.Single((await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery(), default)).Value!.Items);

        Assert.Equal(2, session.AlbumCount);
        Assert.Equal(2, session.CoverUrls!.Count);
    }

    [Fact]
    public async Task AMovieBetweenSongs_SplitsTheMusicIntoTwoSessions()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, "One", Day, mediaType: "Music");
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Tremors", Day.AddMinutes(10));
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, "Two", Day.AddMinutes(20), mediaType: "Music");

        var items = (await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery(), default)).Value!.Items;

        Assert.Equal(
            [PersonalActivityKindValues.MusicSession, PersonalActivityKinds.Watch, PersonalActivityKindValues.MusicSession],
            items.Select(item => item.Kind));
    }

    [Fact]
    public async Task ThePictureUrl_CarriesTheScopeWhenItIsNotMe()
    {
        await using var world = PersonalActivityWorld.Create();
        var key = new string('f', 64);
        await world.Repository.SaveArtworkAsync(new PersonalActivityArtwork(key, [1], "image/jpeg", 10, 10));
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Tremors", Day, artwork: key);

        var mine = await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery(), default);
        var admin = await world.Service.ListAsync(world.ServerAdmin, new PersonalActivityQuery { Scope = "everyone" }, default);

        Assert.Equal("/api/v1/activity/artwork/" + key, mine.Value!.Items[0].ArtworkUrl);
        Assert.Equal("/api/v1/activity/artwork/" + key + "?scope=everyone", admin.Value!.Items[0].ArtworkUrl);
    }

    [Fact]
    public async Task Periods_CountPerYearAndMonthAndFollowTheScopeAndFilters()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "A", new DateTimeOffset(2024, 3, 5, 10, 0, 0, TimeSpan.Zero));
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Read, "B", new DateTimeOffset(2024, 4, 5, 10, 0, 0, TimeSpan.Zero), mediaType: "Books");
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "C", new DateTimeOffset(2025, 12, 31, 23, 59, 0, TimeSpan.Zero));
        await world.AddAsync(world.MemberB, PersonalActivityKinds.Watch, "Someone else", new DateTimeOffset(2025, 12, 1, 10, 0, 0, TimeSpan.Zero));

        var mine = (await world.Service.PeriodsAsync(world.MemberOfA, new PersonalActivityQuery(), default)).Value!;
        var onlyReading = (await world.Service.PeriodsAsync(
            world.MemberOfA, new PersonalActivityQuery { Kinds = ["read"] }, default)).Value!;
        var everyone = (await world.Service.PeriodsAsync(world.ServerAdmin, new PersonalActivityQuery { Scope = "everyone" }, default)).Value!;

        Assert.Equal([2025, 2024], mine.Years.Select(year => year.Year));
        Assert.Equal([1, 2], mine.Years.Select(year => year.Count));
        Assert.Equal([(4, 1), (3, 1)], mine.Years[1].Months.Select(month => (month.Month, month.Count)));
        var reading = Assert.Single(onlyReading.Years);
        Assert.Equal(2024, reading.Year);
        Assert.Equal(1, reading.Count);
        Assert.Equal(2, everyone.Years[0].Count);
    }

    [Fact]
    public async Task Periods_RefuseWhatTheCallerMayNotSee()
    {
        await using var world = PersonalActivityWorld.Create();

        var refused = await world.Service.PeriodsAsync(world.MemberOfA, new PersonalActivityQuery { Scope = "everyone" }, default);

        Assert.Equal(PersonalActivityFailure.Forbidden, refused.Failure);
    }

    [Fact]
    public async Task Recent_ReturnsTheCallersLatestItems_WithNaturalShapesAndMusicCollapsed()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Read, "Hyperion", Day, mediaType: "Books", subtitle: "Dan Simmons");
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Severance", Day.AddDays(1), mediaType: "TV", subtitle: "S2 E4");
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, "Wish You Were Here", Day.AddDays(2), mediaType: "Audiobooks");
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, "One", Day.AddDays(3), mediaType: "Music", parentTitle: "Album");
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, "Two", Day.AddDays(3).AddMinutes(4), mediaType: "Music", parentTitle: "Album");
        await world.AddAsync(world.AdminA, PersonalActivityKinds.Watch, "Not mine", Day.AddDays(9));

        var tiles = (await world.Service.RecentAsync(world.MemberOfA, 20, default)).Value!.Tiles;

        Assert.Equal(
            [("music_session", "square"), ("listen", "square"), ("watch", "landscape"), ("read", "portrait")],
            tiles.Select(tile => (tile.Kind, tile.Shape)));
        Assert.Equal(2, tiles[0].SongCount);
        Assert.Equal("Severance", tiles[2].Title);
        Assert.Equal("S2 E4", tiles[2].Subtitle);
        Assert.DoesNotContain(tiles, tile => tile.Title == "Not mine");
    }

    [Fact]
    public async Task Recent_ListsOnlyAsManyTilesAsAsked()
    {
        await using var world = PersonalActivityWorld.Create();
        for (var day = 0; day < 30; day++)
        {
            await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, $"Film {day}", Day.AddDays(day));
        }

        var tiles = (await world.Service.RecentAsync(world.MemberOfA, 20, default)).Value!.Tiles;

        Assert.Equal(20, tiles.Count);
        Assert.Equal("Film 29", tiles[0].Title);
    }

    // ----- Summary -----

    [Fact]
    public async Task TheSummary_IsTheCallersOwnLifetimeTotals_SplitIntoMusicAndAudiobooks()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Tremors", Day, played: 5000, finished: true, percent: 100);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Dune", Day.AddDays(1), played: 100, percent: 10);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Read, "Hyperion", Day.AddDays(2), mediaType: "Books", played: 3600);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, "Audio", Day.AddDays(3), mediaType: "Audiobooks", played: 7200);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Listen, "Song", Day.AddDays(4), mediaType: "Music", played: 250);
        await world.AddAsync(world.AdminA, PersonalActivityKinds.Watch, "Not mine", Day, played: 99999);

        var summary = (await world.Service.SummaryAsync(world.MemberOfA, default)).Value!;

        Assert.Equal(5100, summary.WatchSeconds);
        Assert.Equal(3600, summary.ReadSeconds);
        Assert.Equal(7450, summary.ListenSeconds);
        Assert.Equal(250, summary.MusicSeconds);
        Assert.Equal(7200, summary.AudiobookSeconds);
        Assert.Equal(1, summary.FinishedCount);
        Assert.Equal(3, summary.InProgressCount);
        Assert.Equal(Day.ToString("O"), summary.FirstActivityAt);
    }

    [Fact]
    public async Task TheSummaryOfAServerAdministratorOrAPairedApp_IsStillOnlyTheirOwn()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.ServerPerson, PersonalActivityKinds.Watch, "Mine", Day, played: 10);
        await world.AddAsync(world.AdminA, PersonalActivityKinds.Watch, "Theirs", Day, played: 1000);

        foreach (var caller in new[] { world.ServerAdmin, world.LockedServerAdmin, world.ServerAdminApp })
        {
            Assert.Equal(10, (await world.Service.SummaryAsync(caller, default)).Value!.WatchSeconds);
        }

        Assert.Equal(0, world.AuditCount(PersonalScopeAuthorizer.ActivityViewedOthers));
    }

    [Fact]
    public async Task TheSummaryOfSomeoneWithNoActivity_IsZeroWithNoFirstDate()
    {
        await using var world = PersonalActivityWorld.Create();

        var summary = (await world.Service.SummaryAsync(world.MemberOfA, default)).Value!;

        Assert.Equal(0, summary.WatchSeconds + summary.ReadSeconds + summary.ListenSeconds);
        Assert.Equal(0, summary.FinishedCount + summary.InProgressCount);
        Assert.Null(summary.FirstActivityAt);
    }

    [Fact]
    public async Task ASignedOutCallerGetsNoSummary()
    {
        await using var world = PersonalActivityWorld.Create();

        var result = await world.Service.SummaryAsync(new RequestAuthority(PrincipalKind.Anonymous, false), default);

        Assert.Equal(PersonalActivityFailure.Forbidden, result.Failure);
    }

    // ----- Export -----

    [Fact]
    public async Task AMember_ExportsTheirOwnMatchingActivityAsCsv_WithoutAPersonColumn()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Tremors, the film", Day, percent: 100, finished: true, played: 5760, device: "Living room TV");
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Read, "Hyperion", Day.AddDays(1), mediaType: "Books", percent: 40, played: 3600);
        await world.AddAsync(world.AdminA, PersonalActivityKinds.Watch, "Theirs", Day);

        var export = await world.Service.ExportAsync(world.MemberOfA, new PersonalActivityQuery { Kinds = ["watch"] }, default);
        var csv = await ReadAsync(export.Value!);

        Assert.StartsWith("tuvima-activity-", export.Value!.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".csv", export.Value.FileName, StringComparison.Ordinal);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Date (UTC),Time (UTC),Kind,Title,Subtitle,Played,Progress,Outcome,Device", lines[0].TrimStart('﻿'));
        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-09-09,12:00:00,watch,\"Tremors, the film\",,1:36:00,100,finished,Living room TV", lines[1]);
        Assert.Equal(0, world.AuditCount(PersonalActivityReadService.ExportedOthers));
    }

    [Fact]
    public async Task ExportedTitles_ThatLookLikeFormulasAreMadeSafeForSpreadsheets()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "=HYPERLINK(\"http://example.test\")", Day);

        var csv = await ReadAsync((await world.Service.ExportAsync(world.MemberOfA, new PersonalActivityQuery(), default)).Value!);

        Assert.Contains("\"'=HYPERLINK(\"\"http://example.test\"\")\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHouseholdAdministrator_CannotExportTheirHousehold_AndNothingIsLogged()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Friend film", Day);

        foreach (var scope in new[] { $"household:{world.HouseholdA}", $"profile:{world.MemberA.ProfileId}", "everyone" })
        {
            var refused = await world.Service.ExportAsync(world.AdminOfA, new PersonalActivityQuery { Scope = scope }, default);
            Assert.Equal(PersonalActivityFailure.Forbidden, refused.Failure);
        }

        var own = await world.Service.ExportAsync(world.AdminOfA, new PersonalActivityQuery(), default);
        Assert.True(own.IsSuccess);
        Assert.Equal(0, world.AuditCount(PersonalActivityReadService.ExportedOthers));
        Assert.Equal(0, world.AuditCount(PersonalScopeAuthorizer.ActivityViewedOthers));
    }

    [Fact]
    public async Task AMember_CannotExportAnyoneElses()
    {
        await using var world = PersonalActivityWorld.Create();

        var refused = await world.Service.ExportAsync(world.MemberOfA, new PersonalActivityQuery { Scope = "everyone" }, default);

        Assert.Equal(PersonalActivityFailure.Forbidden, refused.Failure);
    }

    [Fact]
    public async Task AServerAdministratorExportingOthers_GetsAPersonColumn_AndEveryExportIsLogged()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Friend film", Day);
        await world.AddAsync(world.MemberB, PersonalActivityKinds.Watch, "Bea friend film", Day.AddDays(1));
        var query = new PersonalActivityQuery { Scope = $"household:{world.HouseholdA}", Q = "film" };

        var first = await ReadAsync((await world.Service.ExportAsync(world.ServerAdmin, query, default)).Value!);
        await world.Service.ExportAsync(world.ServerAdmin, query, default);

        var lines = first.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Date (UTC),Time (UTC),Person,Kind,Title,Subtitle,Played,Progress,Outcome,Device", lines[0].TrimStart('﻿'));
        Assert.Equal(2, lines.Length);
        Assert.Contains("Friend of Alex,watch,Friend film", lines[1], StringComparison.Ordinal);
        var logged = world.AuditRows(PersonalActivityReadService.ExportedOthers);
        Assert.Equal(2, logged.Count);
        Assert.Equal(world.ServerPerson.ProfileId, logged[0].ActorProfileId);
        Assert.Equal("household", logged[0].SubjectType);
        Assert.Equal(world.HouseholdA.ToString("D"), logged[0].SubjectId);
    }

    [Fact]
    public async Task AServerAdministratorWhoHasNotUnlocked_CannotExportOthers()
    {
        await using var world = PersonalActivityWorld.Create();

        var refused = await world.Service.ExportAsync(world.LockedServerAdmin, new PersonalActivityQuery { Scope = "everyone" }, default);

        Assert.Equal(PersonalActivityFailure.Forbidden, refused.Failure);
        Assert.Equal(0, world.AuditCount(PersonalActivityReadService.ExportedOthers));
    }

    // ----- Deleting history -----

    [Fact]
    public async Task APreview_CountsExactlyWhatDeletingWouldRemove()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Old", Day);
        await world.AddAsync(world.AdminB, PersonalActivityKinds.Watch, "Old B", Day.AddDays(1));
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "New", Day.AddDays(40));
        var cutoff = Day.AddDays(10);

        var everyone = await world.Service.DeletePreviewAsync(world.ServerAdmin, "everyone", cutoff, default);
        var household = await world.Service.DeletePreviewAsync(world.ServerAdmin, $"household:{world.HouseholdA}", cutoff, default);

        Assert.Equal(2, everyone.Value!.Count);
        Assert.Equal(1, household.Value!.Count);
    }

    [Fact]
    public async Task AServerAdministrator_DeletesOnlyEntriesBeforeTheDateInTheScope_AndItIsLogged()
    {
        await using var world = PersonalActivityWorld.Create();
        var kept = new string('a', 64);
        var orphaned = new string('b', 64);
        foreach (var key in new[] { kept, orphaned })
        {
            await world.Repository.SaveArtworkAsync(new PersonalActivityArtwork(key, [1], "image/jpeg", 10, 10));
        }

        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Old A", Day, artwork: orphaned);
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "New A", Day.AddDays(40), artwork: kept);
        await world.AddAsync(world.MemberB, PersonalActivityKinds.Watch, "Old B", Day);
        var cutoff = Day.AddDays(10);

        var result = await world.Service.DeleteAsync(world.ServerAdmin, $"household:{world.HouseholdA}", cutoff, default);

        Assert.Equal(1, result.Value!.Deleted);
        var left = await world.Service.ListAsync(world.ServerAdmin, new PersonalActivityQuery { Scope = "everyone" }, default);
        Assert.Equal(["New A", "Old B"], left.Value!.Items.Select(item => item.Title));
        Assert.False(await world.Repository.HasArtworkAsync(orphaned));
        Assert.True(await world.Repository.HasArtworkAsync(kept));
        var logged = Assert.Single(world.AuditRows(PersonalActivityReadService.HistoryDeleted));
        Assert.Equal(world.ServerPerson.ProfileId, logged.ActorProfileId);
        Assert.Equal("household", logged.SubjectType);
        Assert.Contains("\"count\":\"1\"", logged.ChangesJson, StringComparison.Ordinal);
        Assert.Contains($"household:{world.HouseholdA:D}", logged.ChangesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeleteWhoseAuditEntryCannotBeWritten_LeavesHistoryIntact()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Old", Day);
        // A caller whose account is not in the data store: the audit entry cannot be written.
        var unknownAdmin = world.ServerAdmin with { AccountId = Guid.NewGuid() };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            world.Service.DeleteAsync(unknownAdmin, "everyone", Day.AddDays(10), default));

        Assert.Single((await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery(), default)).Value!.Items);
        Assert.Equal(0, world.AuditCount(PersonalActivityReadService.HistoryDeleted));
    }

    [Fact]
    public async Task OnlyAServerAdministrator_CanDeleteOrPreviewDeletingHistory()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Old", Day);
        var cutoff = Day.AddDays(10);

        var callers = new (string Name, RequestAuthority Authority)[]
        {
            ("member", world.MemberOfA),
            ("child profile", world.KidOfA),
            ("household administrator", world.AdminOfA),
            ("server administrator who has not unlocked", world.LockedServerAdmin),
            ("paired app of a server administrator", world.ServerAdminApp),
            ("signed-out caller", new RequestAuthority(PrincipalKind.Anonymous, false)),
        };
        foreach (var (name, authority) in callers)
        {
            foreach (var scope in new[] { "me", $"household:{world.HouseholdA}", "everyone" })
            {
                var preview = await world.Service.DeletePreviewAsync(authority, scope, cutoff, default);
                var delete = await world.Service.DeleteAsync(authority, scope, cutoff, default);
                Assert.True(preview.Failure == PersonalActivityFailure.Forbidden, $"{name} previewed {scope}");
                Assert.True(delete.Failure == PersonalActivityFailure.Forbidden, $"{name} deleted {scope}");
            }
        }

        Assert.Single((await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery(), default)).Value!.Items);
        Assert.Equal(0, world.AuditCount(PersonalActivityReadService.HistoryDeleted));
    }

    [Fact]
    public async Task DeletingAnUnknownPerson_IsNotFound_AndRemovesNothing()
    {
        await using var world = PersonalActivityWorld.Create();
        await world.AddAsync(world.MemberA, PersonalActivityKinds.Watch, "Old", Day);

        var result = await world.Service.DeleteAsync(world.ServerAdmin, $"profile:{Guid.NewGuid()}", Day.AddDays(10), default);

        Assert.Equal(PersonalActivityFailure.NotFound, result.Failure);
        Assert.Equal(0, world.AuditCount(PersonalActivityReadService.HistoryDeleted));
        Assert.Single((await world.Service.ListAsync(world.MemberOfA, new PersonalActivityQuery(), default)).Value!.Items);
    }

    // ----- The picture route -----

    [Fact]
    public async Task APicture_IsOnlyGivenToSomeoneWhoMaySeeAnEntryThatUsesIt()
    {
        await using var world = PersonalActivityWorld.Create();
        var key = new string('c', 64);
        await world.Repository.SaveArtworkAsync(new PersonalActivityArtwork(key, [7, 7, 7], "image/jpeg", 10, 10));
        await world.AddAsync(world.AdminA, PersonalActivityKinds.Watch, "Alex film", Day, artwork: key);

        var owner = await world.Service.ArtworkAsync(world.AdminOfA, key, null, default);
        var member = await world.Service.ArtworkAsync(world.MemberOfA, key, null, default);
        var memberAskingWider = await world.Service.ArtworkAsync(world.MemberOfA, key, "everyone", default);
        var householdAdmin = await world.Service.ArtworkAsync(world.AdminOfA, key, $"profile:{world.MemberA.ProfileId}", default);
        var otherHousehold = await world.Service.ArtworkAsync(world.AdminOfB, key, $"household:{world.HouseholdB}", default);
        var otherHouseholdWider = await world.Service.ArtworkAsync(world.AdminOfB, key, "everyone", default);
        var serverAdmin = await world.Service.ArtworkAsync(world.ServerAdmin, key, "everyone", default);

        Assert.Equal([7, 7, 7], owner.Value!.Image);
        Assert.Equal(PersonalActivityFailure.NotFound, member.Failure);
        Assert.Equal(PersonalActivityFailure.Forbidden, memberAskingWider.Failure);
        Assert.Equal(PersonalActivityFailure.NotFound, householdAdmin.Failure);
        Assert.Equal(PersonalActivityFailure.NotFound, otherHousehold.Failure);
        Assert.Equal(PersonalActivityFailure.Forbidden, otherHouseholdWider.Failure);
        Assert.True(serverAdmin.IsSuccess);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("short")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public async Task AMalformedPictureKey_IsNotFound(string key)
    {
        await using var world = PersonalActivityWorld.Create();

        var result = await world.Service.ArtworkAsync(world.MemberOfA, key, null, default);

        Assert.Equal(PersonalActivityFailure.NotFound, result.Failure);
    }

    private static async Task<string> ReadAsync(PersonalActivityExport export)
    {
        using var stream = new MemoryStream();
        await export.WriteAsync(stream, default);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
