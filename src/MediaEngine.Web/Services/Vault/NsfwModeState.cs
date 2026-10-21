using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Vault;

/// <summary>
/// The signed-in person's Vault mode for this device session (Videos spec D23, D36), read from the Engine's status route. This is
/// the one place the Dashboard decides whether anything about the Vault is drawn: until the Engine has answered with a status,
/// and for any profile it answers "not found" to (Off, Kids, content-limited, an app), <see cref="IsAvailable"/> is false and every
/// Vault surface renders nothing. A Kids profile never even asks. Locking fails closed: if the Engine cannot be reached the
/// Dashboard still hides the Vault at once.
/// </summary>
public sealed class NsfwModeState(IEngineApiClient api, IVaultPreferenceStore store, ILogger<NsfwModeState> logger, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(25);

    /// <summary>How often an unlocked Dashboard asks the Engine whether it is still unlocked, so an idle lock clears the screen.</summary>
    internal static readonly TimeSpan LockPollEvery = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DateTimeOffset _lastKeepAlive = DateTimeOffset.MinValue;
    private bool _coversBlurred = true;
    private bool _disposed;
    private ITimer? _lockPoll;
    private int _polling;

    /// <summary>Raised after the status, the viewing options or the cover toggle change.</summary>
    public event Action? Changed;

    /// <summary>The profile the status belongs to.</summary>
    public Guid? ProfileId { get; private set; }

    /// <summary>True once the Engine has answered for the current profile (with a status or with "not found"). A page waits for this before deciding it is not allowed.</summary>
    public bool Resolved { get; private set; }

    /// <summary>What the Engine said, or null when this profile cannot use Vault mode (or the Engine has not answered yet).</summary>
    public NsfwStatusDto? Status { get; private set; }

    /// <summary>True only when the Engine said this profile can use Vault mode. Nothing about the Vault is drawn otherwise.</summary>
    public bool IsAvailable => Status is not null;

    /// <summary>True when this device session has Vault mode on right now.</summary>
    public bool Unlocked => Status is { Unlocked: true };

    /// <summary>True when switching Vault mode on asks for the person's profile PIN ("Ask for my PIN to open Vault mode" is on).</summary>
    public bool NeedsPin => Status is { Mode: "pin" };

    /// <summary>True when the profile has a PIN (one PIN per profile; Vault mode uses the same one).</summary>
    public bool HasPin => Status is { HasPin: true };

    /// <summary>The person's viewing options; the defaults until the Engine has said they can use Vault mode.</summary>
    public VaultViewPreferences Preferences { get; private set; } = VaultViewPreferences.Default;

    /// <summary>True when the optional Vault mode check shows in the top menu (only ever for a profile that can use Vault mode).</summary>
    public bool ShowQuickCheck => IsAvailable && Preferences.ShowQuickCheck;

    /// <summary>The Blur toggle at the top of the Vault and of its search results. Every view starts blurred after the area locks and unlocks again.</summary>
    public bool CoversBlurred => _coversBlurred;

    /// <summary>What covers look like right now: hidden if the person chose Hide, otherwise blurred or shown by the toggle.</summary>
    public VaultCoverMode EffectiveCovers => Preferences.Covers == VaultCoverMode.Hide
        ? VaultCoverMode.Hide
        : _coversBlurred ? VaultCoverMode.Blur : VaultCoverMode.Show;

    /// <summary>Asks the Engine for this profile's status. A Kids profile is never asked: it can never use Vault mode.</summary>
    public async Task RefreshAsync(Guid? profileId, bool isKids, CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (profileId != ProfileId)
            {
                // A different person: nothing of the last profile's Vault mode carries over.
                Status = null;
                Resolved = false;
                Preferences = VaultViewPreferences.Default;
                _coversBlurred = true;
            }

            ProfileId = profileId;
            if (profileId is not { } id || isKids)
            {
                Clear();
                Resolved = profileId is not null;
                RaiseChanged();
                return;
            }

            var call = await api.GetNsfwStatusAsync(ct);
            if (!call.Succeeded || call.Value is null)
            {
                Clear();
                Resolved = true;
                RaiseChanged();
                return;
            }

            Resolved = true;

            var wasUnlocked = Unlocked;
            Status = call.Value;
            if (Preferences == VaultViewPreferences.Default)
            {
                Preferences = await store.LoadAsync(id);
            }

            if (wasUnlocked != Unlocked)
            {
                _coversBlurred = true;
            }

            RaiseChanged();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Loads the status once for a profile; later calls for the same profile do nothing.</summary>
    public async Task EnsureLoadedAsync(Guid profileId, bool isKids, CancellationToken ct = default)
    {
        if (ProfileId == profileId && (Status is not null || isKids))
        {
            return;
        }

        await RefreshAsync(profileId, isKids, ct);
    }

    /// <summary>Switches Vault mode on for this device session. A wrong PIN comes back as an outcome and changes nothing.</summary>
    public async Task<VaultCall<NsfwStatusDto>> UnlockAsync(string? pin, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return VaultCall<NsfwStatusDto>.Of(VaultOutcome.NotAvailable);
        }

        var call = await api.UnlockNsfwAsync(pin, ct);
        if (call.Succeeded && call.Value is { Unlocked: true } status)
        {
            Status = status;
            _coversBlurred = true;
            _lastKeepAlive = _time.GetUtcNow();
            RaiseChanged();
        }
        else if (call.Outcome == VaultOutcome.NotAvailable)
        {
            Clear();
        }

        return call;
    }

    /// <summary>
    /// Saves the person's own "Turn Vault mode off after being idle for" choice (5, 15, 30, 60 minutes, or 0 for Never) and takes the
    /// Engine's answer as the new status, so the screen and the Engine's idle lock agree.
    /// </summary>
    public async Task<VaultCall<NsfwStatusDto>> SetIdleAsync(int minutes, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return VaultCall<NsfwStatusDto>.Of(VaultOutcome.NotAvailable);
        }

        var call = await api.SetNsfwIdleAsync(minutes, ct);
        if (call.Succeeded && call.Value is { } status)
        {
            Status = status;
            RaiseChanged();
        }

        return call;
    }

    /// <summary>Switches Vault mode off at once. The Dashboard hides the Vault even if the Engine cannot be reached.</summary>
    public async Task LockAsync(CancellationToken ct = default)
    {
        var wasUnlocked = Unlocked;
        if (Status is { } status)
        {
            Status = new NsfwStatusDto { Mode = status.Mode, Unlocked = false, HasPin = status.HasPin, IdleLockMinutes = status.IdleLockMinutes };
            _coversBlurred = true;
            RaiseChanged();
        }

        if (wasUnlocked && !(await api.LockNsfwAsync(ct)).Succeeded)
        {
            logger.LogDebug("The Engine could not be told to lock Vault mode; the Dashboard has hidden it anyway.");
        }
    }

    /// <summary>Restarts the Engine's idle timer for real use. Throttled; if the Engine says the session has locked, the Dashboard locks too.</summary>
    public async Task KeepAliveAsync(CancellationToken ct = default)
    {
        if (!Unlocked || _time.GetUtcNow() - _lastKeepAlive < KeepAliveEvery)
        {
            return;
        }

        _lastKeepAlive = _time.GetUtcNow();
        var call = await api.KeepNsfwAliveAsync(ct);
        if (call.Outcome == VaultOutcome.NotAvailable)
        {
            await RefreshAsync(ProfileId, isKids: false, ct);
        }
    }

    public void SetCoversBlurred(bool blurred)
    {
        if (_coversBlurred == blurred)
        {
            return;
        }

        _coversBlurred = blurred;
        RaiseChanged();
    }

    /// <summary>Changes and remembers the viewing options for this profile on this device.</summary>
    public async Task UpdatePreferencesAsync(Func<VaultViewPreferences, VaultViewPreferences> change)
    {
        if (ProfileId is not { } id || !IsAvailable)
        {
            return;
        }

        Preferences = change(Preferences);
        RaiseChanged();
        await store.SaveAsync(id, Preferences);
    }

    private void RaiseChanged()
    {
        SyncLockPoll();
        Changed?.Invoke();
    }

    // The Engine locks a session on its own (idle limit, another profile, an administrator). While this device is unlocked it asks
    // now and then, so the Dashboard clears the Vault from the screen soon after, without waiting for the next click.
    private void SyncLockPoll()
    {
        if (Unlocked && !_disposed)
        {
            _lockPoll ??= _time.CreateTimer(_ => _ = PollLockAsync(), null, LockPollEvery, LockPollEvery);
        }
        else if (_lockPoll is not null)
        {
            _lockPoll.Dispose();
            _lockPoll = null;
        }
    }

    private async Task PollLockAsync()
    {
        if (!Unlocked || _disposed || Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            await RefreshAsync(ProfileId, isKids: false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogDebug(exception, "Could not check whether Vault mode is still on.");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void Clear()
    {
        var changed = Status is not null;
        Status = null;
        _coversBlurred = true;
        if (changed)
        {
            RaiseChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lockPoll?.Dispose();
        _lockPoll = null;
        // When the app closes Vault mode switches itself off (D23c). Best effort: the Engine's idle limit is the backstop.
        if (Unlocked)
        {
            try
            {
                await api.LockNsfwAsync();
            }
            catch (Exception exception) when (exception is HttpRequestException or ObjectDisposedException or OperationCanceledException)
            {
                logger.LogDebug(exception, "Could not lock Vault mode while the session was closing.");
            }
        }

        Changed = null;
        _refreshGate.Dispose();
    }
}
