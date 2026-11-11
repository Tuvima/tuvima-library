using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>The sort the phone Songs header remembers per profile (spec music-table-and-reader-frame D21): its text form and the browser store.</summary>
public sealed class SongSortPreferencesTests
{
    private static readonly Guid Ada = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bea = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(SongColumn.Song, false, "song:asc")]
    [InlineData(SongColumn.Artist, true, "artist:desc")]
    [InlineData(SongColumn.DateAdded, true, "dateAdded:desc")]
    [InlineData(SongColumn.Time, false, "time:asc")]
    public void ASortRoundTripsThroughItsText(SongColumn column, bool descending, string text)
    {
        Assert.Equal(text, SongSortPreferences.Encode(new SongSortState(column, descending)));
        Assert.Equal(new SongSortState(column, descending), SongSortPreferences.Decode(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("song")]
    [InlineData("song:sideways")]
    [InlineData("genre:asc")]
    [InlineData("nothing:asc")]
    public void DamagedOrUnofferedTextReadsAsNoChoice(string? text)
    {
        Assert.Null(SongSortPreferences.Decode(text));
    }

    [Fact]
    public void ASortThePhoneListDoesNotOfferIsNotStored()
    {
        Assert.Null(SongSortPreferences.Encode(new SongSortState(SongColumn.Genre, false)));
        Assert.Null(SongSortPreferences.Encode(new SongSortState(null, false)));
        Assert.Null(SongSortPreferences.Encode(null));
    }

    [Fact]
    public async Task TheStoreKeepsOneSortPerProfileAndForgetsOnNull()
    {
        var (store, storage, _) = Create();

        await store.SaveAsync(Ada, new SongSortState(SongColumn.Plays, true));

        Assert.Equal(new SongSortState(SongColumn.Plays, true), await store.LoadAsync(Ada));
        Assert.Null(await store.LoadAsync(Bea));
        Assert.Contains("tuvima.songSort.v1.11111111111111111111111111111111", storage.Values.Keys);

        await store.SaveAsync(Ada, null);

        Assert.Empty(storage.Values);
        Assert.Null(await store.LoadAsync(Ada));
    }

    [Fact]
    public async Task BlockedStorageUsesTheDefaultAndLogsInsteadOfSwallowingTheFailure()
    {
        var (store, _, log) = Create(blocked: true);

        var loaded = await store.LoadAsync(Ada);
        await store.SaveAsync(Ada, new SongSortState(SongColumn.Song, false));

        Assert.Null(loaded);
        Assert.Equal(2, log.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Exception is JSException));
    }

    private static (BrowserSongSortPreferenceStore Store, FakeStorage Storage, CapturingLoggerProvider Log) Create(bool blocked = false)
    {
        var storage = new FakeStorage { Blocked = blocked };
        var log = new CapturingLoggerProvider();
        var store = new BrowserSongSortPreferenceStore(storage, LoggerFactory.Create(builder => builder.AddProvider(log)).CreateLogger<BrowserSongSortPreferenceStore>());
        return (store, storage, log);
    }

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
