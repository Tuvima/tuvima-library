using MediaEngine.Web.Services.Editing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The part that starts and stops the keyboard fit for a phone sheet: it names itself to the script, asks once however many times it is synced, and
/// never leaves a watch behind when it is disposed, even while the script was still loading.
/// </summary>
public sealed class EditorKeyboardFitTests
{
    [Fact]
    public async Task AnOwnerWatchesAndStopsUnderItsOwnNameAndAskingAgainChangesNothing()
    {
        var js = new SpyRuntime();
        var fit = new EditorKeyboardFit(js, NullLogger.Instance);

        await fit.SyncAsync(true);
        await fit.SyncAsync(true);
        await fit.DisposeAsync();

        Assert.Equal(["watchKeyboard", "stopKeyboard"], js.Module.Calls.Select(call => call.Identifier).ToArray());
        var owner = Assert.Single(js.Module.Calls[0].Args!);
        Assert.False(string.IsNullOrWhiteSpace((string?)owner));
        Assert.Equal(owner, Assert.Single(js.Module.Calls[1].Args!));
    }

    [Fact]
    public async Task TwoOwnersNameThemselvesDifferently()
    {
        var js = new SpyRuntime();
        var first = new EditorKeyboardFit(js, NullLogger.Instance);
        var second = new EditorKeyboardFit(js, NullLogger.Instance);

        await first.SyncAsync(true);
        await second.SyncAsync(true);

        Assert.NotEqual(js.Module.Calls[0].Args![0], js.Module.Calls[1].Args![0]);
    }

    [Fact]
    public async Task DisposedWhileTheScriptIsLoading_NothingIsStartedAndNothingIsLeftToStop()
    {
        var js = new SpyRuntime { HoldImport = new TaskCompletionSource() };
        var fit = new EditorKeyboardFit(js, NullLogger.Instance);

        var syncing = fit.SyncAsync(true);
        await fit.DisposeAsync();
        js.HoldImport.SetResult();
        await syncing;

        Assert.DoesNotContain(js.Module.Calls, call => call.Identifier == "watchKeyboard");
    }

    [Fact]
    public async Task DisposedWhileTheWatchIsInFlight_TheStopIsStillSent()
    {
        var js = new SpyRuntime();
        js.Module.HoldWatch = new TaskCompletionSource();
        var fit = new EditorKeyboardFit(js, NullLogger.Instance);

        var syncing = fit.SyncAsync(true);
        await fit.DisposeAsync();
        js.Module.HoldWatch.SetResult();
        await syncing;

        Assert.Contains(js.Module.Calls, call => call.Identifier == "stopKeyboard");
    }

    private sealed class SpyRuntime : IJSRuntime
    {
        public SpyModule Module { get; } = new();
        public TaskCompletionSource? HoldImport { get; init; }

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (HoldImport is not null)
            {
                await HoldImport.Task;
            }

            return (TValue)(object)Module;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class SpyModule : IJSObjectReference
    {
        public List<(string Identifier, object?[]? Args)> Calls { get; } = [];
        public TaskCompletionSource? HoldWatch { get; set; }

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args));
            if (identifier == "watchKeyboard" && HoldWatch is not null)
            {
                await HoldWatch.Task;
            }

            return default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
