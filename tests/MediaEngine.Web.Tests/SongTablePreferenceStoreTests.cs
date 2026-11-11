using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>The browser store for the song table's remembered columns: one entry per profile and place, defaults on any failure, never silent.</summary>
public sealed class SongTablePreferenceStoreTests
{
    private static readonly Guid Ada = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bea = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SongTableChoice Chosen() => new()
    {
        Shown = new HashSet<SongColumn> { SongColumn.Genre },
        Hidden = new HashSet<SongColumn> { SongColumn.Plays },
        Widths = new Dictionary<SongColumn, int> { [SongColumn.Artist] = 220 },
    };

    private static (BrowserSongTablePreferenceStore Store, FakeStorage Storage, CapturingLoggerProvider Log) Create(bool blocked = false)
    {
        var storage = new FakeStorage { Blocked = blocked };
        var log = new CapturingLoggerProvider();
        var store = new BrowserSongTablePreferenceStore(storage, LoggerFactory.Create(builder => builder.AddProvider(log)).CreateLogger<BrowserSongTablePreferenceStore>());
        return (store, storage, log);
    }

    [Fact]
    public async Task AChoiceRoundTripsPerProfileAndPlace()
    {
        var (store, _, _) = Create();

        await store.SaveAsync(Ada, SongTablePlace.Songs, Chosen());

        var back = await store.LoadAsync(Ada, SongTablePlace.Songs);
        Assert.Equal(Chosen().Shown, back.Shown);
        Assert.Equal(Chosen().Hidden, back.Hidden);
        Assert.Equal(220, back.Widths[SongColumn.Artist]);
        Assert.True((await store.LoadAsync(Bea, SongTablePlace.Songs)).IsDefault);
        Assert.True((await store.LoadAsync(Ada, SongTablePlace.Playlist)).IsDefault);
    }

    [Fact]
    public async Task ResetForgetsTheEntryAndNothingBlocksTheDefaults()
    {
        var (store, storage, _) = Create();
        await store.SaveAsync(Ada, SongTablePlace.Albums, Chosen());
        Assert.Single(storage.Values);

        await store.SaveAsync(Ada, SongTablePlace.Albums, SongTableChoice.Default);

        Assert.Empty(storage.Values);
        Assert.True((await store.LoadAsync(Ada, SongTablePlace.Albums)).IsDefault);
    }

    [Fact]
    public async Task EachPlaceAndProfileGetsItsOwnStorageKey()
    {
        var (store, storage, _) = Create();

        await store.SaveAsync(Ada, SongTablePlace.Songs, Chosen());
        await store.SaveAsync(Bea, SongTablePlace.Songs, Chosen());
        await store.SaveAsync(Ada, SongTablePlace.AlbumPage, Chosen());

        Assert.Equal(3, storage.Values.Count);
        Assert.Contains("tuvima.songTable.v1.11111111111111111111111111111111.songTable.songs", storage.Values.Keys);
    }

    [Fact]
    public async Task AnUnreadableStoredValueFallsBackTowardTheDefaults()
    {
        var (store, storage, _) = Create();
        storage.Values[BrowserSongTablePreferenceStore_Key(Ada, SongTablePlace.Songs)] = "show=nothing;width=artist:wide;;=";

        Assert.True((await store.LoadAsync(Ada, SongTablePlace.Songs)).IsDefault);
    }

    [Fact]
    public async Task BlockedStorageUsesTheDefaultsAndLogsInsteadOfSwallowingTheFailure()
    {
        var (store, _, log) = Create(blocked: true);

        var loaded = await store.LoadAsync(Ada, SongTablePlace.Songs);
        await store.SaveAsync(Ada, SongTablePlace.Songs, Chosen());

        Assert.True(loaded.IsDefault);
        Assert.Equal(2, log.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Exception is JSException));
    }

    private static string BrowserSongTablePreferenceStore_Key(Guid profile, SongTablePlace place) =>
        $"tuvima.songTable.v1.{profile:N}.{SongTablePreferences.KeyFor(place)}";

    /// <summary>A browser's local storage behind the module the store imports; "blocked" throws like a private window does.</summary>
    private sealed class FakeStorage : IJSRuntime
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Blocked { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                return ValueTask.FromResult((TValue)(object)new Module(this));
            }

            throw new InvalidOperationException($"Unexpected call {identifier}.");
        }

        private sealed class Module(FakeStorage owner) : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            {
                if (owner.Blocked)
                {
                    throw new JSException("The operation is insecure.");
                }

                var key = (string)args![0]!;
                switch (identifier)
                {
                    case "load":
                        return ValueTask.FromResult((TValue)(object?)(owner.Values.TryGetValue(key, out var text) ? text : null)!);
                    case "save":
                        owner.Values[key] = (string)args[1]!;
                        return ValueTask.FromResult((TValue)(object)true);
                    case "remove":
                        owner.Values.Remove(key);
                        return ValueTask.FromResult((TValue)(object)true);
                    default:
                        throw new InvalidOperationException($"Unexpected call {identifier}.");
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
