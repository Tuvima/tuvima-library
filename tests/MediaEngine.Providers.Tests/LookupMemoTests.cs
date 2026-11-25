using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

public sealed class LookupMemoTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Remembered_Answer_IsReturnedUntilItExpires()
    {
        var clock = new ManualClock();
        var memo = new LookupMemo<bool>(TimeSpan.FromHours(1), clock);
        memo.Set("k", true);

        Assert.True(memo.TryGet("k", out _));

        clock.Now = clock.Now.AddMinutes(61);

        Assert.False(memo.TryGet("k", out _));
    }

    [Fact]
    public void Key_IgnoresCaseAndSurroundingSpaces()
    {
        var memo = new LookupMemo<int>(TimeSpan.FromHours(1));
        memo.Set(LookupMemo<int>.Key("Hans Zimmer ", "Composer", "en"), 7);

        Assert.True(memo.TryGet(LookupMemo<int>.Key("hans zimmer", " composer", "EN"), out var value));
        Assert.Equal(7, value);
    }

    [Fact]
    public async Task GetOrLoad_RunsTheLoaderOncePerKey()
    {
        var memo = new LookupMemo<string>(TimeSpan.FromHours(1));
        var calls = 0;

        for (var i = 0; i < 5; i++)
        {
            await memo.GetOrLoadAsync("series", () => Task.FromResult($"loaded-{++calls}"));
        }

        Assert.Equal(1, calls);
    }
}
