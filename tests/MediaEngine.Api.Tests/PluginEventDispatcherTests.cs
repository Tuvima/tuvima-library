using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Plugin event subscriptions: events from the Library's one event list reach switched-on, approved subscribers whose
/// permissions cover them; a new subscriber starts at the newest event; a handler gets one try and thirty seconds; five
/// failures in a row pause the subscription.
/// </summary>
public sealed class PluginEventDispatcherTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private HostServicesHarness NewHost(PluginHostLimits? limits = null, params HostServicesTestPlugin[] plugins)
    {
        var host = new HostServicesHarness(limits, plugins);
        _disposables.Add(host);
        return host;
    }

    [Fact]
    public async Task AnItemAddedEvent_ReachesASubscriber_WithTheSameEnvelopeWebhooksGet()
    {
        var plugin = new HostServicesTestPlugin();
        var subscriber = plugin.Subscribe("library.item_added");
        var host = NewHost(plugins: plugin);
        host.Enable();
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        var sequence = await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        var received = Assert.Single(subscriber.Received);
        Assert.Equal("library.item_added", received.Type);
        Assert.Equal(sequence, received.Sequence);
        Assert.Equal(host.Clock.GetUtcNow(), received.OccurredAt);
        using var envelope = JsonDocument.Parse(received.PayloadJson);
        Assert.Equal("library.item_added", envelope.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(received.EventId, envelope.RootElement.GetProperty("event_id").GetString());
        Assert.Equal("Dune", envelope.RootElement.GetProperty("payload").GetProperty("title").GetString());
    }

    [Fact]
    public async Task OnlyTheTypesTheSubscriberAskedFor_AreDelivered()
    {
        var plugin = new HostServicesTestPlugin();
        var subscriber = plugin.Subscribe("ingestion.completed");
        var host = NewHost(plugins: plugin);
        host.Enable();
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        await host.AppendEventAsync("library.item_added");
        await host.AppendEventAsync("ingestion.completed");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(["ingestion.completed"], subscriber.Received.Select(received => received.Type));
    }

    [Fact]
    public async Task WatchActivityEvents_NeedTheWatchActivityPermission_LibraryEventsDoesNotCoverThem()
    {
        var libraryOnly = new HostServicesTestPlugin("test.library-only", [PluginPermissionIds.LibraryEvents], "Library only");
        var libraryOnlySubscriber = libraryOnly.Subscribe("playback.started", "library.item_added");
        var watcher = new HostServicesTestPlugin("test.watcher", [PluginPermissionIds.LibraryEvents, PluginPermissionIds.WatchActivity], "Watcher");
        var watcherSubscriber = watcher.Subscribe("playback.started", "library.item_added");
        var watchOnly = new HostServicesTestPlugin("test.watch-only", [PluginPermissionIds.WatchActivity], "Watch only");
        var watchOnlySubscriber = watchOnly.Subscribe("playback.started", "library.item_added");
        var host = NewHost(plugins: [libraryOnly, watcher, watchOnly]);
        host.Enable(libraryOnly.Manifest.Id);
        host.Enable(watcher.Manifest.Id);
        host.Enable(watchOnly.Manifest.Id);
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        await host.AppendEventAsync("playback.started");
        await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(["library.item_added"], libraryOnlySubscriber.Received.Select(received => received.Type));
        Assert.Equal(["playback.started", "library.item_added"], watcherSubscriber.Received.Select(received => received.Type));
        Assert.Equal(["playback.started"], watchOnlySubscriber.Received.Select(received => received.Type));
    }

    [Fact]
    public async Task ANewSubscriber_StartsAtTheNewestEvent_WithNoBacklog()
    {
        var plugin = new HostServicesTestPlugin();
        var subscriber = plugin.Subscribe("library.item_added");
        var host = NewHost(plugins: plugin);
        await host.AppendEventAsync("library.item_added");
        await host.AppendEventAsync("library.item_added");
        host.Enable();
        var dispatcher = host.NewDispatcher();

        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Empty(subscriber.Received);

        var fresh = await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal([fresh], subscriber.Received.Select(received => received.Sequence));
    }

    [Fact]
    public async Task TheCursorSurvivesARestart_SoNothingIsDeliveredTwiceAndNothingIsMissedWhileTheEngineWasDown()
    {
        var plugin = new HostServicesTestPlugin();
        var subscriber = plugin.Subscribe("library.item_added");
        var host = NewHost(plugins: plugin);
        host.Enable();
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        var first = await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        // The plugin stays switched on across the restart (its on/off is kept in its configuration file).
        host.Restart();
        var missedWhileDown = await host.AppendEventAsync("library.item_added");
        var afterRestart = host.NewDispatcher();
        await afterRestart.DispatchOnceAsync(CancellationToken.None);
        await afterRestart.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal([first, missedWhileDown], subscriber.Received.Select(received => received.Sequence));
        Assert.Equal(missedWhileDown, await host.Cursors.GetAsync(plugin.Manifest.Id));
    }

    [Fact]
    public async Task ASwitchedOffPlugin_GetsNothing_AndSwitchingItOnAgainStartsAtTheNewestEvent()
    {
        var plugin = new HostServicesTestPlugin();
        var subscriber = plugin.Subscribe("library.item_added");
        var host = NewHost(plugins: plugin);
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Empty(subscriber.Received);

        host.Enable();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Empty(subscriber.Received);

        host.Disable();
        await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        host.Enable();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Empty(subscriber.Received);

        var fresh = await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal([fresh], subscriber.Received.Select(received => received.Sequence));
    }

    [Fact]
    public async Task AHandlerThatTakesTooLong_IsCancelled_CountsAsAFailure_AndTheCursorMovesOn()
    {
        var limits = PluginHostLimits.Default with { EventHandlerTimeout = TimeSpan.FromMilliseconds(200) };
        var plugin = new HostServicesTestPlugin();
        var subscriber = plugin.Subscribe("library.item_added");
        subscriber.Handler = (_, _, token) => Task.Delay(Timeout.Infinite, token);
        var host = NewHost(limits, plugin);
        host.Enable();
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        var sequence = await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Single(subscriber.Received);
        Assert.Equal(sequence, await host.Cursors.GetAsync(plugin.Manifest.Id));
        var description = host.Statuses.Get(plugin.Manifest.Id).Describe();
        Assert.Equal("working", description.State);
        Assert.Equal("Could not handle library.item_added: took longer than 1 second", description.LastProblem);
    }

    [Fact]
    public async Task AFailedHandler_IsNotRetried_ItsFailureIsTheLastProblem_AndASuccessResetsTheCount()
    {
        var plugin = new HostServicesTestPlugin();
        var subscriber = plugin.Subscribe("library.item_added");
        var fail = true;
        subscriber.Handler = (_, _, _) => fail ? throw new InvalidOperationException("No reading list yet") : Task.CompletedTask;
        var host = NewHost(plugins: plugin);
        host.Enable();
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        for (var i = 0; i < 4; i++)
        {
            await host.AppendEventAsync("library.item_added");
        }

        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(4, subscriber.Received.Count);
        var description = host.Statuses.Get(plugin.Manifest.Id).Describe();
        Assert.Equal("working", description.State);
        Assert.Equal("Could not handle library.item_added: No reading list yet", description.LastProblem);

        fail = false;
        await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        fail = true;
        for (var i = 0; i < 4; i++)
        {
            await host.AppendEventAsync("library.item_added");
        }

        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal("working", host.Statuses.Get(plugin.Manifest.Id).Describe().State);
    }

    [Fact]
    public async Task FiveFailuresInARow_PauseTheSubscription_TheCardSaysSo_AndSwitchingOffAndOnResumesIt()
    {
        var plugin = new HostServicesTestPlugin();
        var subscriber = plugin.Subscribe("library.item_added");
        var fail = true;
        subscriber.Handler = (_, _, _) => fail ? throw new InvalidOperationException("Broken") : Task.CompletedTask;
        var host = NewHost(plugins: plugin);
        host.Enable();
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        for (var i = 0; i < 7; i++)
        {
            await host.AppendEventAsync("library.item_added");
        }

        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(5, subscriber.Received.Count);
        var description = host.Statuses.Get(plugin.Manifest.Id).Describe();
        Assert.Equal("needs_attention", description.State);
        Assert.Equal("Stopped receiving events after 5 failures", description.Problem);
        Assert.Equal("paused", description.EventSubscription);

        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal(5, subscriber.Received.Count);

        fail = false;
        host.Disable();
        host.Enable();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        var fresh = await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal(fresh, subscriber.Received[^1].Sequence);
        Assert.Equal(6, subscriber.Received.Count);
        Assert.Equal("working", host.Statuses.Get(plugin.Manifest.Id).Describe().State);
    }

    [Fact]
    public async Task ASubscriberWithoutAnyCoveringPermission_ReceivesNothing()
    {
        var plugin = new HostServicesTestPlugin(permissions: [PluginPermissionIds.PluginRecords]);
        var subscriber = plugin.Subscribe("library.item_added");
        var host = NewHost(plugins: plugin);
        host.Enable();
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Empty(subscriber.Received);
        Assert.Null(host.Statuses.Get(plugin.Manifest.Id).Describe().EventSubscription);
    }

    [Fact]
    public async Task AHandlerGetsAContextThatCanStoreRecords()
    {
        var plugin = new HostServicesTestPlugin();
        var subscriber = plugin.Subscribe("library.item_added");
        subscriber.Handler = (evt, context, token) => context.Records.PutAsync("seen", evt.EventId, "{}", token);
        var host = NewHost(plugins: plugin);
        host.Enable();
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        await host.AppendEventAsync("library.item_added");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        var stored = await host.RecordStoreFor(plugin.Manifest.Id).ListAsync("seen");
        Assert.Single(stored);
    }

    [Fact]
    public void ASubscriptionToAnEventTheLibraryDoesNotHave_IsALoadError()
    {
        var plugin = new HostServicesTestPlugin();
        plugin.Subscribe("library.item_added", "library.item_teleported");
        var host = NewHost(plugins: plugin);

        var registration = host.Catalog.Get(plugin.Manifest.Id)!;

        Assert.Equal("Plugin subscribes to the unknown event type 'library.item_teleported'.", registration.LoadError);
    }

    [Fact]
    public void AnEventTypeAddedToTheLibrarysOneList_IsKnownToPluginSubscriptions()
    {
        // Webhooks and plugin subscriptions read the same registry, so there is only one list to extend.
        var registry = new MediaEngine.Api.Services.Events.ApplicationEventRegistry(new MediaEngine.Domain.Authorization.PermissionRegistry());
        Assert.All(registry.GetAll(), definition =>
            Assert.True(MediaEngine.Api.Services.Events.ApplicationEventRegistry.IsKnownType(definition.EventType)));
        Assert.True(MediaEngine.Api.Services.Events.ApplicationEventRegistry.IsWatchActivity("playback.completed"));
        Assert.False(MediaEngine.Api.Services.Events.ApplicationEventRegistry.IsWatchActivity("library.item_added"));
    }

    [Fact]
    public async Task TwoPlugins_OneSwitchedOnWhileTheOtherIsSwitchedOff_RepeatedlyInOnePass_NeitherCorruptsTheOthersPlace()
    {
        // Each plugin's pass runs on its own task while the loop forgets switched-off plugins; their shared bookkeeping must be thread-safe.
        var steady = new HostServicesTestPlugin("test.steady", name: "Steady");
        var steadySubscriber = steady.Subscribe("library.item_added");
        var flipping = new HostServicesTestPlugin("test.flipping", name: "Flipping");
        flipping.Subscribe("library.item_added");
        var host = NewHost(plugins: [steady, flipping]);
        host.Enable(steady.Manifest.Id);
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        const int rounds = 120;
        var sent = new List<long>();
        for (var round = 0; round < rounds; round++)
        {
            if (round % 2 == 0)
            {
                host.Enable(flipping.Manifest.Id);
            }
            else
            {
                host.Disable(flipping.Manifest.Id);
            }

            sent.Add(await host.AppendEventAsync("library.item_added"));
            await dispatcher.DispatchOnceAsync(CancellationToken.None);
        }

        // The steady plugin never missed an event and never saw one twice, however often the other one flipped.
        Assert.Equal(sent, steadySubscriber.Received.Select(received => received.Sequence).ToList());
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }
}
