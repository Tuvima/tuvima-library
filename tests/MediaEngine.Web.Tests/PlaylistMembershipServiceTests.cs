using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The one service every "Add to playlist" entry point uses (Listen playlists spec WP2): the Engine's batch add does the skipping and the
/// counting, requests that do not go through are counted as failed (and the ones before them are kept), the "Already in" tick is read only
/// where it is cheap, and the sentence is the same wording for every result. The Engine is a hand-written stand-in.
/// </summary>
public sealed class PlaylistMembershipServiceTests : LocalizedBunitContext
{
    private static readonly Guid Playlist = Guid.Parse("b1000000-0000-0000-0000-000000000001");
    private readonly List<IReadOnlyList<Guid>> _requests = [];

    private static Guid Song(int number) => new(number, 0, 0, new byte[8]);

    private static IReadOnlyList<Guid> Songs(int count) => [.. Enumerable.Range(1, count).Select(Song)];

    private IEngineApiClient Engine(Func<int, IReadOnlyList<Guid>, CollectionItemsAddedResponse?> answer) =>
        EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.AddCollectionItemsAsync), args =>
        {
            var sent = (IReadOnlyList<Guid>)args![1]!;
            _requests.Add(sent);
            return Task.FromResult(answer(_requests.Count, sent));
        }));

    // -- Duplicates ---------------------------------------------------------------------------

    [Fact]
    public async Task AddingSongsThePlaylistAlreadyHolds_CountsThemAsAlreadyThere_AndNeverSendsTheSameSongTwice()
    {
        var api = Engine((_, sent) => new CollectionItemsAddedResponse(sent.Count - 1, 1));

        var result = await PlaylistMembership.AddAsync(api, Playlist, "Road trip", [Song(1), Song(2), Song(2), Guid.Empty, Song(3)], null);

        Assert.Equal(new PlaylistAddResult("Road trip", 2, 1, 0, Created: false), result);
        Assert.Equal([Song(1), Song(2), Song(3)], Assert.Single(_requests));
    }

    [Fact]
    public async Task AddingToAPlaylist_NeverReadsItsSongsToCheckForDuplicates_SoALongPlaylistIsCheckedInFullByTheEngine()
    {
        EngineApiClientStub? stub = null;
        var api = EngineApiClientStub.Create(created =>
        {
            stub = created;
            created.SetHandler(nameof(IEngineApiClient.AddCollectionItemsAsync), args =>
                Task.FromResult<CollectionItemsAddedResponse?>(new CollectionItemsAddedResponse(0, ((IReadOnlyList<Guid>)args![1]!).Count)));
        });

        var result = await PlaylistMembership.AddAsync(api, Playlist, "Marathon", Songs(3), null);

        Assert.Equal(new PlaylistAddResult("Marathon", 0, 3, 0, Created: false), result);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetCollectionItemsAsync), stub!.Calls);
    }

    [Fact]
    public async Task AnAddOfOver1000Songs_HasNoSizeCap_AndEverySongIsSentOnce()
    {
        var api = Engine((_, sent) => new CollectionItemsAddedResponse(sent.Count, 0));

        var result = await PlaylistMembership.AddAsync(api, Playlist, "Big", Songs(1200), null);

        Assert.Equal(1200, result.Added);
        Assert.Equal(Songs(1200), _requests.SelectMany(batch => batch));
        Assert.All(_requests, batch => Assert.True(batch.Count <= 250));
    }

    // -- Partial failure ----------------------------------------------------------------------

    [Fact]
    public async Task WhenALaterRequestFails_TheEarlierSongsAreKept_AndTheResultSaysHowManyWentInAndHowManyDidNot()
    {
        var api = Engine((call, sent) => call == 2 ? null : new CollectionItemsAddedResponse(sent.Count, 0));

        var result = await PlaylistMembership.AddAsync(api, Playlist, "Big", Songs(600), null);

        Assert.Equal(new PlaylistAddResult("Big", 350, 0, 250, Created: false), result);
    }

    [Fact]
    public async Task WhenTheEngineAnswersNothing_EverySongIsCountedAsFailed()
    {
        var api = Engine((_, _) => null);

        var result = await PlaylistMembership.AddAsync(api, Playlist, "Road trip", Songs(3), null);

        Assert.Equal(new PlaylistAddResult("Road trip", 0, 0, 3, Created: false), result);
    }

    // -- The "Already in" tick ----------------------------------------------------------------

    [Fact]
    public async Task TheAlreadyInTick_IsOnlyForShortPlaylistsHoldingEveryChosenSong()
    {
        var holdsAll = Guid.NewGuid();
        var holdsSome = Guid.NewGuid();
        var tooShort = Guid.NewGuid();
        var tooLong = Guid.NewGuid();
        var unreadable = Guid.NewGuid();
        var read = new List<Guid>();
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetCollectionItemsAsync), args =>
        {
            var id = (Guid)args![0]!;
            read.Add(id);
            IEnumerable<Guid> held = id == holdsAll ? [Song(1), Song(2), Song(9)] : id == holdsSome ? [Song(1)] : [];
            return Task.FromResult(held.Select(work => new CollectionItemDto { Id = Guid.NewGuid(), WorkId = work, Title = "Held" }).ToList());
        }));

        var holding = await PlaylistMembership.FindHoldingAllAsync(
            api,
            [(holdsAll, 3), (holdsSome, 5), (tooShort, 1), (tooLong, PlaylistMembership.AlreadyInProbeLimit + 1), (unreadable, 8)],
            [Song(1), Song(2)],
            null);

        Assert.Equal([holdsAll], holding);
        Assert.DoesNotContain(tooShort, read);
        Assert.DoesNotContain(tooLong, read);
    }

    // -- The one sentence ---------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0, 0, false, "1 song added to Road trip", AppSeverityName.Success)]
    [InlineData(3, 0, 0, false, "3 songs added to Road trip", AppSeverityName.Success)]
    [InlineData(2, 1, 0, false, "2 added, 1 already there", AppSeverityName.Success)]
    [InlineData(0, 3, 0, false, "Already in Road trip", AppSeverityName.Info)]
    [InlineData(2, 0, 1, false, "2 added, 1 could not be added", AppSeverityName.Warning)]
    [InlineData(0, 2, 1, false, "2 already there, 1 could not be added", AppSeverityName.Warning)]
    [InlineData(2, 1, 1, false, "2 added, 1 already there, 1 could not be added", AppSeverityName.Warning)]
    [InlineData(0, 0, 3, false, "The songs could not be added to Road trip.", AppSeverityName.Error)]
    [InlineData(0, 0, 3, true, "The playlist could not be created.", AppSeverityName.Error)]
    [InlineData(3, 0, 0, true, "3 songs added to Road trip", AppSeverityName.Success)]
    public void EveryResult_GetsOneSentence_WithSingularFormsAndAStatedFailure(int added, int already, int failed, bool creating, string sentence, AppSeverityName severity)
    {
        var strings = Services.GetRequiredService<IStringLocalizer<SharedStrings>>();

        var notice = PlaylistMembershipFlow.Describe(new PlaylistAddResult("Road trip", added, already, failed, Created: creating && added > 0, Creating: creating), strings);

        Assert.Equal(sentence, notice.Message);
        Assert.Equal(severity.ToString(), notice.Severity.ToString());
    }

    /// <summary>The severities the sentence can carry, named so the test data reads as words.</summary>
    public enum AppSeverityName
    {
        Info = 1,
        Success = 2,
        Warning = 3,
        Error = 4,
    }
}
