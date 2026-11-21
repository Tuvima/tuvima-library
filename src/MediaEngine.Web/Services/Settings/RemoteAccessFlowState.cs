using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Settings;

/// <summary>The screens of Settings &gt; Network &gt; Remote access (spec remote-access, WP2): the start page, the five steps and the "I already have a proxy" fields.</summary>
public enum RemoteAccessScreen
{
    Start,
    Check,
    Choose,
    Passwords,
    Connect,
    Test,

    /// <summary>The existing public-address fields on their own, for someone who already runs a proxy or tunnel.</summary>
    OwnAddress,
}

/// <summary>
/// Where the person is in the guided "away from home" flow, kept for the signed-in session so moving to another Settings page
/// (for example to set a password) and coming back keeps the place. It holds only what the flow needs: the screen, the last
/// check, the way chosen and whether a check is running. The connection mode itself is saved through the Network page's own
/// save, never here.
/// </summary>
public sealed class RemoteAccessFlowState : IDisposable
{
    private readonly IEngineApiClient _api;
    private CancellationTokenSource? _checkCancellation;

    public RemoteAccessFlowState(IEngineApiClient api) => _api = api;

    /// <summary>Raised whenever anything changes, so the flow draws again.</summary>
    public event Action? Changed;

    public RemoteAccessScreen Screen { get; private set; } = RemoteAccessScreen.Start;

    /// <summary>The latest answer from the Engine's check, or null before the first one (or when the Engine could not be asked).</summary>
    public RemoteCheckResultDto? Result { get; private set; }

    /// <summary>True while the Engine's check is being read.</summary>
    public bool Checking { get; private set; }

    /// <summary>True when the last check could not be read at all (the Engine did not answer).</summary>
    public bool CheckUnavailable { get; private set; }

    /// <summary>The way in the person chose: one of <see cref="RemoteCheckSuggestions"/> (never "not now", which leaves the flow).</summary>
    public string? Method { get; private set; }

    /// <summary>How often the Tailscale step looks again while it waits for the person (a test shortens it).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The screens that count as the five numbered steps, in order.</summary>
    public static IReadOnlyList<RemoteAccessScreen> Steps { get; } =
        [RemoteAccessScreen.Check, RemoteAccessScreen.Choose, RemoteAccessScreen.Passwords, RemoteAccessScreen.Connect, RemoteAccessScreen.Test];

    /// <summary>The finding with this key, or null when the check did not report it (only findings that apply are returned).</summary>
    public RemoteCheckFindingDto? Finding(string key) =>
        Result?.Findings.FirstOrDefault(finding => string.Equals(finding.Key, key, StringComparison.Ordinal));

    public bool Has(string key) => Finding(key) is not null;

    /// <summary>The setting administrator has no password yet: Continue on the Passwords step stays off until they do.</summary>
    public bool PasswordsBlockContinue => Has(RemoteCheckFindingKeys.AdminWithoutPassword);

    public void Go(RemoteAccessScreen screen)
    {
        Screen = screen;
        Raise();
    }

    /// <summary>Back to the start page; the last check is kept so coming back is quick.</summary>
    public void Close() => Go(RemoteAccessScreen.Start);

    /// <summary>Starts the flow: opens the Check step and asks the Engine.</summary>
    public async Task BeginAsync()
    {
        Screen = RemoteAccessScreen.Check;
        Raise();
        await CheckAsync();
    }

    /// <summary>Asks the Engine what it finds. A newer ask replaces an older one that is still running.</summary>
    public async Task CheckAsync()
    {
        _checkCancellation?.Cancel();
        _checkCancellation?.Dispose();
        var cancellation = _checkCancellation = new CancellationTokenSource();
        Checking = true;
        CheckUnavailable = false;
        Raise();

        RemoteCheckResultDto? result;
        try
        {
            result = await _api.GetRemoteCheckAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancel was pressed, or a newer check replaced this one; whoever did that already set the state.
            return;
        }

        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        Checking = false;
        CheckUnavailable = result is null;
        if (result is not null)
        {
            Result = result;
        }
        Raise();
    }

    /// <summary>A quiet look again (the Tailscale step does this on a timer): the screen keeps what it shows until the answer arrives.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var result = await _api.GetRemoteCheckAsync(ct);
        if (result is null || ct.IsCancellationRequested)
        {
            return;
        }

        Result = result;
        Raise();
    }

    /// <summary>Stops a running check and returns to the start page.</summary>
    public void CancelCheck()
    {
        _checkCancellation?.Cancel();
        Checking = false;
        Go(RemoteAccessScreen.Start);
    }

    /// <summary>Records the way chosen and moves on to Passwords.</summary>
    public void ChooseMethod(string method)
    {
        Method = method;
        Go(RemoteAccessScreen.Passwords);
    }

    public void Dispose()
    {
        _checkCancellation?.Cancel();
        _checkCancellation?.Dispose();
    }

    private void Raise() => Changed?.Invoke();
}
