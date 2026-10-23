using MediaEngine.Contracts.Display;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>The circuit's held copy of the Engine's browse library list: reused for about a minute, and only for the profile it was read for.</summary>
public sealed class BrowseLibrariesStateTests
{
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid AccountA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid AccountB = Guid.Parse("a0000000-0000-0000-0000-00000000000b");

    private static BrowseLibraryDto Library(string name) =>
        new(Guid.NewGuid(), name, "other_videos", "watch", "Videos", true);

    private sealed class Harness : IDisposable
    {
        private readonly ManualClock _clock = new();
        private TaskCompletionSource<IReadOnlyList<BrowseLibraryDto>?>? _pending;

        public Harness()
        {
            var api = EngineApiClientStub.Create(stub =>
            {
                stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
                {
                    new(First, "One", "#C9922E", "Administrator", DateTimeOffset.UtcNow),
                    new(Second, "Two", "#C9922E", "Standard", DateTimeOffset.UtcNow),
                }));
                stub.SetHandler(nameof(IEngineApiClient.GetBrowseLibrariesAsync), _ =>
                {
                    Reads++;
                    return _pending is { } pending ? pending.Task : Task.FromResult<IReadOnlyList<BrowseLibraryDto>?>(Next);
                });
            });
            Profiles = new ActiveProfileSessionService(null!, api);
            Session = new DashboardSessionAccessor();
            State = new BrowseLibrariesState(api, Profiles, Session, _clock);
        }

        public ActiveProfileSessionService Profiles { get; }
        public DashboardSessionAccessor Session { get; }
        public BrowseLibrariesState State { get; }
        public int Reads { get; private set; }
        public IReadOnlyList<BrowseLibraryDto>? Next { get; set; } = [Library("Lectures")];

        public void Wait(TimeSpan time) => _clock.Advance(time);
        public void HoldNextRead() => _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseRead(IReadOnlyList<BrowseLibraryDto>? libraries) => _pending!.SetResult(libraries);

        public void Dispose() => Profiles.Dispose();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }

    [Fact]
    public async Task TheListIsReadOnceAndReusedWhileItIsUnderAMinuteOld()
    {
        using var harness = new Harness();

        var first = await harness.State.GetAsync();
        harness.Wait(TimeSpan.FromSeconds(50));
        var second = await harness.State.GetAsync();

        Assert.Equal(1, harness.Reads);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task TheListIsReadAgainOnceItIsAMinuteOld()
    {
        using var harness = new Harness();
        await harness.State.GetAsync();
        harness.Wait(BrowseLibrariesState.MaxAge + TimeSpan.FromSeconds(1));
        harness.Next = [Library("Family recordings")];

        var again = await harness.State.GetAsync();

        Assert.Equal(2, harness.Reads);
        Assert.Equal("Family recordings", Assert.Single(again!).Name);
    }

    [Fact]
    public async Task AnotherActiveProfileNeverSeesTheListReadForTheFirst()
    {
        using var harness = new Harness();
        await harness.State.GetAsync();
        await harness.Profiles.SetActiveProfileAsync(Second);
        harness.Next = [];

        var forSecond = await harness.State.GetAsync();

        Assert.Equal(2, harness.Reads);
        Assert.Empty(forSecond!);
    }

    [Fact]
    public async Task AnotherSignedInAccountNeverSeesTheListReadForTheFirst_EvenWithTheSameProfileId()
    {
        using var harness = new Harness();
        harness.Session.Set("token-a", AccountA, First, Guid.NewGuid(), null);
        await harness.State.GetAsync();
        harness.Session.Set("token-b", AccountB, First, Guid.NewGuid(), null);
        harness.Next = [];

        var forB = await harness.State.GetAsync();

        Assert.Equal(2, harness.Reads);
        Assert.Empty(forB!);

        // Back to the first account the list is read again; the held copy belongs to the account that read it last.
        harness.Session.Set("token-a", AccountA, First, Guid.NewGuid(), null);
        harness.Next = [Library("Lectures")];
        Assert.Single((await harness.State.GetAsync())!);
        Assert.Equal(3, harness.Reads);
    }

    [Fact]
    public async Task TwoPagesAskingAtOnceShareOneRead()
    {
        using var harness = new Harness();
        harness.HoldNextRead();

        var a = harness.State.GetAsync();
        var b = harness.State.GetAsync();
        while (harness.Reads == 0)
        {
            await Task.Yield();
        }

        harness.ReleaseRead([Library("Lectures")]);

        Assert.Same(await a, await b);
        Assert.Equal(1, harness.Reads);
    }

    [Fact]
    public async Task AFailedReadIsNotKept_SoTheNextAskReadsAgain()
    {
        using var harness = new Harness();
        harness.Next = null;
        Assert.Null(await harness.State.GetAsync());

        harness.Next = [Library("Lectures")];
        var again = await harness.State.GetAsync();

        Assert.Equal(2, harness.Reads);
        Assert.Single(again!);
    }

    [Fact]
    public async Task InvalidatingForgetsTheHeldList()
    {
        using var harness = new Harness();
        await harness.State.GetAsync();

        harness.State.Invalidate();
        await harness.State.GetAsync();

        Assert.Equal(2, harness.Reads);
    }
}
