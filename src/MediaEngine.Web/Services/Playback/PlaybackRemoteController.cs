using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Playback;

/// <summary>What <c>playback-remote.js</c> says about an element: whether the browser can prompt, who plays, and the receiver's name if the browser gives one.</summary>
public sealed record PlaybackRemoteDescription(string Support, string Availability, string State, string? DeviceName);

/// <summary>What opening the browser's picker came to: prompted, cancelled (closed quietly), failed, or unsupported.</summary>
public sealed record PlaybackRemotePromptResult(string Outcome, string? Reason = null, bool Swapped = false);

/// <summary>
/// Play on another device (unified player v2 D28): the one place that knows whether a receiver is playing. The persistent bar, the "..." menu and
/// the Playback settings panel read it; <c>playback-remote.js</c> reports into it. The browser keeps the media element as the controller, so Play,
/// seek, Previous, Next and the queue keep working through the same element while a receiver plays.
/// </summary>
public sealed class PlaybackRemoteController : IAsyncDisposable
{
    public const string ModulePath = "./js/playback-remote.js";
    public const string AudioElementId = "listen-audio-engine";
    public const string VideoElementId = "tuvima-video-engine";

    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(2);

    /// <summary>A fetched link is used only while at least this much of its life is left, and never kept longer than <see cref="UnstatedLinkLife"/> when the Engine gave no expiry.</summary>
    private static readonly TimeSpan LinkMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UnstatedLinkLife = TimeSpan.FromMinutes(30);

    private readonly IJSRuntime _js;
    private readonly PlaybackCastLinkService _links;
    private readonly PlaybackSessionController _playback;
    private readonly ILogger<PlaybackRemoteController> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IJSObjectReference? _module;
    private DotNetObjectReference<PlaybackRemoteController>? _self;
    private string? _elementId;
    private Guid? _remoteAssetId;
    private Guid? _followingAssetId;
    private TaskCompletionSource? _disconnected;
    private CachedLink? _cached;
    private bool _disposed;

    private sealed record CachedLink(Guid AssetId, PlaybackCastLink Link, DateTimeOffset UsableUntil);

    public PlaybackRemoteController(IJSRuntime js, PlaybackCastLinkService links, PlaybackSessionController playback, ILogger<PlaybackRemoteController> logger)
    {
        _js = js;
        _links = links;
        _playback = playback;
        _logger = logger;
        _playback.Changed += OnPlaybackChanged;
    }

    public PlaybackRemoteSupport Support { get; private set; }

    public PlaybackRemoteMode Mode { get; private set; }

    /// <summary>The receiver's name when the browser exposes one; most do not, and the Dashboard then says "another device".</summary>
    public string? DeviceName { get; private set; }

    /// <summary>True after the Engine or the browser did not start a session; the row then shows "Can't play on another device yet" until the next try works.</summary>
    public bool LastAttemptFailed { get; private set; }

    /// <summary>True while a receiver plays: the bar adds "Playing on ..." and hides Volume.</summary>
    public bool IsRemote => Mode == PlaybackRemoteMode.Remote;

    public event Action? Changed;

    /// <summary>Asks the element whether the browser can prompt, and starts listening to it. Safe to call again for the same element.</summary>
    public async Task ProbeAsync(string elementId)
    {
        if (_disposed || (_elementId == elementId && Support != PlaybackRemoteSupport.Unknown))
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_disposed || (_elementId == elementId && Support != PlaybackRemoteSupport.Unknown))
            {
                return;
            }

            var module = await ModuleAsync();
            if (_elementId is not null && _elementId != elementId && Mode == PlaybackRemoteMode.Local)
            {
                await module.InvokeVoidAsync("detachFrom", _elementId);
            }

            var description = await module.InvokeAsync<PlaybackRemoteDescription?>("attachTo", elementId, _self);
            _elementId = elementId;
            if (description is not null)
            {
                Apply(description);
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is closing; there is nothing to probe.
        }
        catch (InvalidOperationException ex)
        {
            // Server-side prerendering has no browser yet; the row stays as it is and the probe runs after the first render.
            _logger.LogDebug(ex, "Remote playback was not probed: the browser is not available yet.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gets the cast link for the playing item ahead of the click, when the menu or the Playback settings panel opens, and keeps it for that item until it
    /// is about to expire. The click can then open the browser's picker at once, still inside the person's gesture (Safari drops the gesture while the
    /// Engine is asked). Fetching can make the Engine prepare the stream, which costs the server work, so it is done only when a cast could follow: the browser
    /// can prompt, its availability watch reports a device right now ("available"; not "unknown" and not "none"), and the item is playing and not in the
    /// Vault. In every other case nothing is fetched and the click asks the Engine as before. When the Engine refuses nothing is kept and nothing is
    /// shown: the click then asks again and reports the reason as before.
    /// </summary>
    public async Task PrefetchAsync(string elementId, Guid? assetId, bool isVault)
    {
        if (_disposed || Mode != PlaybackRemoteMode.Local || isVault || assetId is not { } asset || asset == Guid.Empty || FreshLinkFor(asset) is not null)
        {
            return;
        }

        try
        {
            await ProbeAsync(elementId);
            if (Support != PlaybackRemoteSupport.Supported || _module is null)
            {
                return;
            }

            var now = await _module.InvokeAsync<PlaybackRemoteDescription?>("readOn", elementId);
            if (now is not { Support: "supported", Availability: "available" } || Mode != PlaybackRemoteMode.Local || _disposed)
            {
                return;
            }

            var link = await _links.RequestAsync(asset);
            if (link.IsReady && !_disposed)
            {
                _cached = new CachedLink(asset, link, link.ExpiresAt?.Subtract(LinkMargin) ?? DateTimeOffset.UtcNow.Add(UnstatedLinkLife));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JSDisconnectedException or InvalidOperationException)
        {
            _logger.LogInformation(ex, "Play on another device: the cast link could not be fetched ahead of time.");
        }
    }

    private PlaybackCastLink? FreshLinkFor(Guid asset) =>
        _cached is { } cached && cached.AssetId == asset && cached.UsableUntil > DateTimeOffset.UtcNow ? cached.Link : null;

    /// <summary>
    /// The row was pressed. While a receiver plays this brings playback back to the browser. Otherwise it uses the link fetched ahead of time, when there
    /// is a fresh one for this item, so the picker opens with no wait for the Engine; if not it asks the Engine for the cast link first. When the Engine
    /// refuses, the picker is not opened and the reason is logged.
    /// </summary>
    public async Task ActivateAsync(string elementId, Guid? assetId, bool isVault)
    {
        if (Mode != PlaybackRemoteMode.Local)
        {
            await StopAsync();
            return;
        }

        if (isVault || assetId is not { } asset || asset == Guid.Empty)
        {
            _logger.LogInformation("Play on another device was not started: {Reason}.", isVault ? "a Vault item never casts" : "nothing is playing");
            return;
        }

        await ProbeAsync(elementId);
        if (Support == PlaybackRemoteSupport.Unsupported || _module is null)
        {
            return;
        }

        // No await comes before the picker is asked for when a link is ready: the probe above has already finished, and the JavaScript call is issued
        // in this same turn of the click.
        var link = FreshLinkFor(asset) ?? await _links.RequestAsync(asset);
        if (!link.IsReady)
        {
            SetFailed(true);
            return;
        }

        PlaybackRemotePromptResult? result;
        try
        {
            result = await _module.InvokeAsync<PlaybackRemotePromptResult>("openPickerOn", elementId, link.Url);
        }
        catch (JSException ex)
        {
            _logger.LogInformation(ex, "Play on another device was not started: the browser's picker raised an error.");
            SetFailed(true);
            return;
        }

        switch (result?.Outcome)
        {
            case "prompted":
                _cached = null;
                _remoteAssetId = asset;
                SetFailed(false);
                break;
            case "unsupported":
                Support = PlaybackRemoteSupport.Unsupported;
                RaiseChanged();
                break;
            case "failed":
                _cached = null;
                _logger.LogInformation("Play on another device was not started: the browser's picker failed ({Reason}).", result.Reason);
                SetFailed(true);
                break;
            default:
                // Cancelled: the person closed the picker. Nothing to say.
                break;
        }
    }

    /// <summary>Brings playback back to the browser at the same position (the page's own source is put back by the script on disconnect).</summary>
    public async Task StopAsync()
    {
        if (_module is null || _elementId is null || Mode == PlaybackRemoteMode.Local)
        {
            return;
        }

        try
        {
            await _module.InvokeAsync<bool>("stopOn", _elementId);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is closing; the receiver is released with the page.
        }
    }

    /// <summary>
    /// Ends a remote session and waits for the browser to confirm, so a close that follows works on a media element that is the browser's again.
    /// A browser that never confirms is given a moment and then treated as local; the close releases the element anyway.
    /// </summary>
    public async Task EndAsync()
    {
        if (Mode == PlaybackRemoteMode.Local)
        {
            return;
        }

        _disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await StopAsync();
        if (await Task.WhenAny(_disconnected.Task, Task.Delay(EndTimeout)) != _disconnected.Task)
        {
            _logger.LogInformation("The receiver did not confirm that playback ended; the player is closing anyway.");
            await OnRemoteStateChanged("local", null);
        }
    }

    [JSInvokable]
    public Task OnRemoteStateChanged(string state, string? deviceName)
    {
        Mode = state switch
        {
            "connecting" => PlaybackRemoteMode.Connecting,
            "remote" => PlaybackRemoteMode.Remote,
            _ => PlaybackRemoteMode.Local,
        };
        DeviceName = Mode == PlaybackRemoteMode.Local ? null : deviceName;
        if (Mode == PlaybackRemoteMode.Local)
        {
            _remoteAssetId = null;
            _followingAssetId = null;
            _disconnected?.TrySetResult();
        }
        else
        {
            LastAttemptFailed = false;
        }

        RaiseChanged();
        return Task.CompletedTask;
    }

    private void Apply(PlaybackRemoteDescription description)
    {
        Support = description.Support == "supported" ? PlaybackRemoteSupport.Supported : PlaybackRemoteSupport.Unsupported;
        Mode = description.State switch
        {
            "connecting" => PlaybackRemoteMode.Connecting,
            "remote" => PlaybackRemoteMode.Remote,
            _ => PlaybackRemoteMode.Local,
        };
        DeviceName = Mode == PlaybackRemoteMode.Local ? null : description.DeviceName;
        RaiseChanged();
    }

    private async Task<IJSObjectReference> ModuleAsync()
    {
        _self ??= DotNetObjectReference.Create(this);
        return _module ??= await _js.InvokeAsync<IJSObjectReference>("import", ModulePath);
    }

    private void SetFailed(bool failed)
    {
        LastAttemptFailed = failed;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    // The queue moved on while a receiver plays, or the bar was closed: follow the new item with a fresh cast link, or end the session.
    private void OnPlaybackChanged(PlaybackChangeKind kind)
    {
        if (Mode != PlaybackRemoteMode.Remote || _disposed)
        {
            return;
        }

        var item = _playback.CurrentItem;
        if (!_playback.HasQueue || item?.AssetId is not { } asset || item.IsVault)
        {
            _ = RunAsync(StopAsync, "ending the session after the player closed");
        }
        else if (asset != _remoteAssetId && asset != _followingAssetId)
        {
            _followingAssetId = asset;
            _ = RunAsync(() => FollowAsync(asset), "following the next item");
        }
    }

    private async Task FollowAsync(Guid asset)
    {
        var link = await _links.RequestAsync(asset);
        if (!link.IsReady || _module is null || _elementId is null || Mode != PlaybackRemoteMode.Remote)
        {
            await StopAsync();
            return;
        }

        if (await _module.InvokeAsync<bool>("followOn", _elementId, link.Url))
        {
            _remoteAssetId = asset;
        }
    }

    private async Task RunAsync(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogInformation(ex, "Play on another device: {What} did not finish.", what);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playback.Changed -= OnPlaybackChanged;
        try
        {
            if (_module is not null)
            {
                if (_elementId is not null)
                {
                    await _module.InvokeVoidAsync("detachFrom", _elementId);
                }

                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser has already dropped the module.
        }

        _self?.Dispose();
        _gate.Dispose();
    }
}
