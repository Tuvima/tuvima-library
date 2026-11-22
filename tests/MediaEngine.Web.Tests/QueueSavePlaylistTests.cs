using Bunit;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Save queue as playlist (Listen playlists spec WP6): the button in the queue panel asks for a name, then makes a hand-made playlist from the
/// current and upcoming songs in play order, says how it went in place, and never changes the queue or playback. The Engine is a hand-written spy.
/// </summary>
public sealed class QueueSavePlaylistTests : AsyncBunitContext
{
    private static readonly Guid Profile = Guid.NewGuid();

    private readonly List<(string Name, string Type, IReadOnlyList<Guid> Works, Guid? Profile)> _created = [];
    private bool _engineAnswers = true;

    public QueueSavePlaylistTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.CreateCollectionWithItemsAsync), args =>
            {
                _created.Add(((string)args![0]!, (string)args[3]!, (IReadOnlyList<Guid>)args[8]!, (Guid?)args[9]));
                return Task.FromResult<Guid?>(_engineAnswers ? Guid.NewGuid() : null);
            })));
    }

    private static ListenQueueItem Song(string title) => Item(title, "Music", Guid.NewGuid());

    private static ListenQueueItem Item(string title, string mediaType, Guid workId) => new()
    {
        QueueEntryId = Guid.NewGuid(),
        WorkId = workId,
        AssetId = Guid.NewGuid(),
        MediaType = mediaType,
        Title = title,
        Subtitle = "The Midnight Coast",
        Duration = "3:46",
    };

    private static ListenPlaybackSnapshot Queue(int songs, int currentIndex = 0) => new()
    {
        ProfileId = Profile,
        CurrentIndex = currentIndex,
        Queue = [.. Enumerable.Range(1, songs).Select(index => Song($"Song {index}"))],
        IsPlaying = true,
        QueueRevision = 7,
    };

    private IRenderedComponent<QueuePanel> Render(ListenPlaybackSnapshot snapshot, RecordingPlaybackSink sink) =>
        Render<QueuePanel>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink));

    private static async Task SaveAsync(IRenderedComponent<QueuePanel> cut, string? name = null)
    {
        await cut.Find("[data-queue-save-button]").ClickAsync();
        if (name is not null)
        {
            cut.Find("[data-queue-save-name] input").Input(name);
        }

        await cut.Find("[data-queue-save-create]").ClickAsync();
    }

    [Fact]
    public async Task SavingATwelveSongQueue_MakesAPlaylistInPlayOrder_AndLeavesTheQueueAndPlaybackUnchanged()
    {
        var snapshot = Queue(12);
        var queueBefore = snapshot.Queue.Select(item => item.QueueEntryId).ToList();
        var sink = new RecordingPlaybackSink();
        var cut = Render(snapshot, sink);

        await SaveAsync(cut, "Road trip");

        var created = Assert.Single(_created);
        Assert.Equal("Road trip", created.Name);
        Assert.Equal("Playlist", created.Type);
        Assert.Equal(snapshot.Queue.Select(item => item.WorkId), created.Works);
        Assert.Equal(Profile, created.Profile);
        Assert.Equal("Saved 12 songs as Road trip", cut.Find("[data-queue-save-notice]").TextContent.Trim());
        Assert.Empty(sink.Commands);
        Assert.Equal(0, snapshot.CurrentIndex);
        Assert.True(snapshot.IsPlaying);
        Assert.Equal(queueBefore, snapshot.Queue.Select(item => item.QueueEntryId));
        Assert.Equal("Up next · 11", cut.Find("[data-queue-heading]").TextContent);
    }

    [Fact]
    public async Task SavingWithoutTypingAName_UsesAPlainQueueAndDateName()
    {
        var cut = Render(Queue(3), new RecordingPlaybackSink());

        await cut.Find("[data-queue-save-button]").ClickAsync();
        var offered = cut.Find("[data-queue-save-name] input").GetAttribute("value");
        await cut.Find("[data-queue-save-create]").ClickAsync();

        Assert.StartsWith("Queue, ", offered);
        Assert.Equal(offered, Assert.Single(_created).Name);
    }

    [Fact]
    public async Task OnlyTheCurrentAndUpcomingSongsAreSaved_NotOnesAlreadyPlayed()
    {
        var snapshot = Queue(6, currentIndex: 2);
        var cut = Render(snapshot, new RecordingPlaybackSink());

        await SaveAsync(cut, "Rest of the night");

        var created = Assert.Single(_created);
        Assert.Equal(snapshot.Queue.Skip(2).Select(item => item.WorkId), created.Works);
        Assert.Equal("Saved 4 songs as Rest of the night", cut.Find("[data-queue-save-notice]").TextContent.Trim());
    }

    [Fact]
    public async Task EntriesThatAreNotLibrarySongs_AreSkippedAndCounted()
    {
        var snapshot = Queue(2) with
        {
            Queue =
            [
                Song("Song 1"),
                Item("A film", "Movies", Guid.NewGuid()),
                Song("Song 2"),
                Item("A book", "Audiobooks", Guid.NewGuid()),
                Item("Not in the library", "Music", Guid.Empty),
            ],
        };
        var cut = Render(snapshot, new RecordingPlaybackSink());

        await SaveAsync(cut, "Mixed");

        Assert.Equal([snapshot.Queue[0].WorkId, snapshot.Queue[2].WorkId], Assert.Single(_created).Works);
        Assert.Equal(
            "Saved 2 songs as Mixed. 3 items were skipped because they are not songs in your library.",
            cut.Find("[data-queue-save-notice]").TextContent.Trim());
    }

    [Fact]
    public async Task WhenTheEngineRefuses_TheNoticeSaysWhatFailed_AndTheQueueIsUnchanged()
    {
        _engineAnswers = false;
        var snapshot = Queue(4);
        var sink = new RecordingPlaybackSink();
        var cut = Render(snapshot, sink);

        await SaveAsync(cut, "Road trip");

        Assert.Equal(
            "The queue could not be saved as a playlist. Your queue was not changed.",
            cut.Find("[data-queue-save-notice]").TextContent.Trim());
        Assert.Empty(sink.Commands);
        Assert.Equal(4, snapshot.Queue.Count);
        Assert.Equal("Up next · 3", cut.Find("[data-queue-heading]").TextContent);
        Assert.NotNull(cut.Find("[data-queue-save-name]"));
    }

    [Fact]
    public async Task CancellingTheNameStep_MakesNothingAndTouchesNothing()
    {
        var sink = new RecordingPlaybackSink();
        var cut = Render(Queue(3), sink);

        await cut.Find("[data-queue-save-button]").ClickAsync();
        await cut.Find("[data-queue-save-cancel]").ClickAsync();

        Assert.Empty(_created);
        Assert.Empty(sink.Commands);
        Assert.NotNull(cut.Find("[data-queue-save-button]"));
    }

    [Fact]
    public void AnEmptyQueue_ShowsTheButtonDisabledWithATooltip()
    {
        var cut = Render(new ListenPlaybackSnapshot { ProfileId = Profile, CurrentIndex = -1 }, new RecordingPlaybackSink());

        var button = cut.Find("[data-queue-save-button]");

        Assert.True(button.HasAttribute("disabled"));
        Assert.Contains("There are no songs in the queue to save.", cut.Find("[data-queue-save]").InnerHtml);
    }

    [Fact]
    public void ASongQueuedTwice_IsSavedOnce()
    {
        var repeated = Guid.NewGuid();
        var snapshot = new ListenPlaybackSnapshot
        {
            CurrentIndex = 0,
            Queue = [Item("Again", "Music", repeated), Song("Other"), Item("Again", "Music", repeated)],
        };

        var selection = QueuePlaylistSaver.Select(snapshot);

        Assert.Equal(2, selection.SongIds.Count);
        Assert.Equal(0, selection.Skipped);
    }
}
