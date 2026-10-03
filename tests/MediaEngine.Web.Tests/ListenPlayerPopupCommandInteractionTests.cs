using Bunit;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace MediaEngine.Web.Tests;

public sealed class ListenPlayerPopupCommandInteractionTests : AsyncBunitContext
{
    private readonly PopupCommandChannel _channel = new();
    private readonly Guid _ownerId = Guid.NewGuid();

    public ListenPlayerPopupCommandInteractionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton(new ThemeService());
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(new ListenPlaybackClientSettings());
        Services.AddSingleton<ActiveProfileSessionService>(provider =>
            new ActiveProfileSessionService(provider.GetRequiredService<IJSRuntime>(), api));
        Services.AddSingleton<IListenPlaybackCommandChannel>(_channel);
    }

    [Fact]
    public async Task PopupPlayUsesCorrelatedOwnerChannelEnvelopeWithFreshCommandIds()
    {
        var cut = await RenderMusicPopupAsync();

        await cut.Find("button.playback-primary-button").ClickAsync();
        await cut.Find("button.playback-primary-button").ClickAsync();

        var commands = Assert.IsType<ListenPlaybackCommandDto[]>(_channel.Commands.ToArray());
        Assert.Equal(2, commands.Length);
        Assert.Equal(new[] { _ownerId, _ownerId }, _channel.OwnerIds);
        Assert.All(commands, command =>
        {
            Assert.Equal("toggle-play", command.Action);
            Assert.Equal(_ownerId, command.RecipientId);
            Assert.NotEqual(Guid.Empty, command.SenderId);
            Assert.NotEqual(Guid.Empty, command.CommandId);
        });
        Assert.Equal(commands[0].SenderId, commands[1].SenderId);
        Assert.NotEqual(commands[0].CommandId, commands[1].CommandId);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "listenPlayback.sendCommand");
    }

    [Fact]
    public async Task PopupShowsRecoverableStatusWhenOwnerReplyDoesNotCorrelate()
    {
        _channel.Reply = command => new ListenPlaybackCommandReplyDto
        {
            CommandId = Guid.NewGuid(),
            RecipientId = command.SenderId,
            Outcome = AudiobookBookmarkOperationOutcomes.Success,
        };
        var cut = await RenderMusicPopupAsync();

        await cut.Find("button.playback-primary-button").ClickAsync();

        cut.WaitForAssertion(() => Assert.Contains("did not confirm this control action", cut.Markup));
        Assert.Single(_channel.Commands);
        Assert.Equal(_ownerId, Assert.Single(_channel.OwnerIds));
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "listenPlayback.sendCommand");
    }

    private async Task<IRenderedComponent<ListenPlayerPopupPage>> RenderMusicPopupAsync()
    {
        var cut = RenderPopup();
        await cut.Instance.HandlePlaybackState(CreateSnapshotStream(new ListenPlaybackSnapshot
        {
            Experience = PlayerExperienceModes.Music,
            Queue =
            [
                new ListenQueueItem
                {
                    WorkId = Guid.NewGuid(),
                    AssetId = Guid.NewGuid(),
                    MediaType = "Music",
                    Title = "A test song",
                    Album = "A test album",
                    Subtitle = "A test artist",
                    StreamUrl = "/stream/test-song",
                    Duration = "3:20",
                },
            ],
            CurrentIndex = 0,
        }));
        return cut;
    }

    private IRenderedComponent<ListenPlayerPopupPage> RenderPopup()
    {
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/listen/player-popup?owner={_ownerId:D}");
        return Render<ListenPlayerPopupPage>();
    }

    [Fact]
    public async Task PopupIgnoresLateOlderStreamAndRetainsStateWhenNewStreamFails()
    {
        var cut = await RenderMusicPopupAsync();
        var releaseOldRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderSnapshot = new ListenPlaybackSnapshot
        {
            Experience = PlayerExperienceModes.Music,
            Queue = [new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Older state" }],
            CurrentIndex = 0,
        };
        var newerSnapshot = new ListenPlaybackSnapshot
        {
            Experience = PlayerExperienceModes.Music,
            Queue = [new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Newer state" }],
            CurrentIndex = 0,
        };
        var oldReference = CreateSnapshotStream(olderSnapshot, releaseOldRead.Task);
        var oldReceive = cut.Instance.HandlePlaybackState(oldReference);
        await oldReference.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cut.Instance.HandlePlaybackState(CreateSnapshotStream(newerSnapshot));
        releaseOldRead.TrySetResult();
        await oldReceive;
        Assert.Contains("Newer state", cut.Markup);
        Assert.DoesNotContain("Older state", cut.Markup);

        await cut.Instance.HandlePlaybackState(new FakeJsStreamReference(Encoding.UTF8.GetBytes("{invalid-json")));
        Assert.Contains("Your current player state is still shown", cut.Markup);
        Assert.Contains("Newer state", cut.Markup);

        JSInterop.Setup<bool>("listenPlayback.hasState").SetResult(true);
        JSInterop.Setup<IJSStreamReference>("listenPlayback.getStateStream")
            .SetResult(CreateSnapshotStream(new ListenPlaybackSnapshot
            {
                Experience = PlayerExperienceModes.Music,
                Queue = [new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Recovered state" }],
                CurrentIndex = 0,
            }));
        await cut.Find("button.listen-popup__retry-state").ClickAsync();
        Assert.Contains("Recovered state", cut.Markup);
        Assert.DoesNotContain("Your current player state is still shown", cut.Markup);

        var disposedCut = await RenderMusicPopupAsync();
        var releaseDisposedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposedReference = CreateSnapshotStream(olderSnapshot, releaseDisposedRead.Task);
        var disposedReceive = disposedCut.Instance.HandlePlaybackState(disposedReference);
        await disposedReference.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disposedCut.Instance.DisposeAsync();
        releaseDisposedRead.TrySetResult();
        await disposedReceive;
        Assert.True(disposedReference.WasDisposed);
    }

    [Fact]
    public async Task PopupInitialStreamCannotOverwriteNewerStateCallback()
    {
        var releaseInitialRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialReference = CreateSnapshotStream(new ListenPlaybackSnapshot
        {
            Experience = PlayerExperienceModes.Music,
            Queue = [new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Initial stored state" }],
            CurrentIndex = 0,
        }, releaseInitialRead.Task);
        JSInterop.Setup<bool>("listenPlayback.hasState").SetResult(true);
        JSInterop.Setup<IJSStreamReference>("listenPlayback.getStateStream").SetResult(initialReference);
        var cut = RenderPopup();
        await initialReference.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cut.Instance.HandlePlaybackState(CreateSnapshotStream(new ListenPlaybackSnapshot
        {
            Experience = PlayerExperienceModes.Music,
            Queue = [new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Live callback state" }],
            CurrentIndex = 0,
        }));
        releaseInitialRead.TrySetResult();
        await initialReference.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cut.WaitForAssertion(() => Assert.Contains("Live callback state", cut.Markup));
        Assert.DoesNotContain("Initial stored state", cut.Markup);
    }

    private sealed class PopupCommandChannel : IListenPlaybackCommandChannel
    {
        public List<ListenPlaybackCommandDto> Commands { get; } = [];
        public List<Guid> OwnerIds { get; } = [];
        public Func<ListenPlaybackCommandDto, ListenPlaybackCommandReplyDto?> Reply { get; set; } = command =>
            new ListenPlaybackCommandReplyDto
            {
                CommandId = command.CommandId,
                RecipientId = command.SenderId,
                Outcome = AudiobookBookmarkOperationOutcomes.Success,
            };

        public Task<ListenPlaybackCommandReplyDto?> SendAsync(Guid ownerRecipientId,
            ListenPlaybackCommandDto command, CancellationToken ct = default)
        {
            OwnerIds.Add(ownerRecipientId);
            Commands.Add(command);
            var reply = Reply(command);
            return Task.FromResult(reply);
        }
    }

    private static FakeJsStreamReference CreateSnapshotStream(ListenPlaybackSnapshot snapshot, Task? readGate = null) =>
        new(JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)), readGate);

    private sealed class FakeJsStreamReference(byte[] payload, Task? readGate = null) : IJSStreamReference
    {
        public long Length => payload.LongLength;
        public bool WasDisposed { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512_000, CancellationToken cancellationToken = default)
        {
            if (payload.LongLength > maxAllowedSize) throw new InvalidDataException("The stream exceeds the maximum size.");
            ReadStarted.TrySetResult();
            if (readGate is not null) await readGate.WaitAsync(cancellationToken);
            return new MemoryStream(payload, writable: false);
        }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
