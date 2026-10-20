using MediaEngine.AI.Configuration;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Infrastructure;

/// <summary>
/// Owns the single-model runtime and coordinates unloads with active inference.
/// </summary>
public sealed class ModelLifecycleManager :
    IModelLifecycleManager,
    IModelRuntimeCoordinator,
    IAsyncDisposable,
    IDisposable
{
    private static readonly TimeSpan MaximumIdleCheckInterval = TimeSpan.FromSeconds(30);

    private readonly AiSettings _settings;
    private readonly AiRuntimeSettingsSnapshot _runtimeSettings;
    private readonly ModelInventory _inventory;
    private readonly IAiRuntimeStatus _runtimeStatus;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<ModelLifecycleManager> _logger;
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _disposerLock = new();
    private readonly List<Func<AiModelRole, CancellationToken, ValueTask>> _modelDisposers = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _idleMonitorTask;

    private AiModelRole? _currentRole;
    private FileStream? _artifactLease;
    private int _currentMemoryMB;
    private DateTimeOffset _lastAccessTime = DateTimeOffset.UtcNow;
    private int _disposeState;

    public ModelLifecycleManager(
        AiSettings settings,
        ModelInventory inventory,
        IAiRuntimeStatus runtimeStatus,
        IEventPublisher eventPublisher,
        ILogger<ModelLifecycleManager> logger)
    {
        _settings = settings;
        _runtimeSettings = AiRuntimeSettingsSnapshot.Create(settings);
        _inventory = inventory;
        _runtimeStatus = runtimeStatus;
        _eventPublisher = eventPublisher;
        _logger = logger;
        _idleMonitorTask = MonitorIdleUnloadAsync(_shutdown.Token);
    }

    public AiModelRole? CurrentlyLoadedRole
    {
        get
        {
            lock (_stateLock)
            {
                return _currentRole;
            }
        }
    }

    public int CurrentMemoryUsageMB
    {
        get
        {
            lock (_stateLock)
            {
                return _currentMemoryMB;
            }
        }
    }

    public async Task LoadModelAsync(AiModelRole role, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _runtimeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadModelCoreAsync(role, ct).ConfigureAwait(false);
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    public async Task UnloadCurrentAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _runtimeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await UnloadCurrentCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    public async Task<bool> EnsureLoadedAsync(AiModelRole role, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _runtimeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await TryEnsureLoadedCoreAsync(role, ct).ConfigureAwait(false);
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireInferenceLeaseAsync(
        AiModelRole role,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _runtimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!await TryEnsureLoadedCoreAsync(role, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Model {role} is not available for inference.");
            }

            Touch();
            return new InferenceLease(this);
        }
        catch
        {
            _runtimeGate.Release();
            throw;
        }
    }

    public IDisposable RegisterModelDisposer(
        Func<AiModelRole, CancellationToken, ValueTask> disposer)
    {
        ArgumentNullException.ThrowIfNull(disposer);
        ThrowIfDisposed();

        lock (_disposerLock)
        {
            _modelDisposers.Add(disposer);
        }

        return new DisposerRegistration(this, disposer);
    }

    public AiHealthStatus GetHealthStatus()
    {
        var profile = _settings.HardwareProfile;
        var gpuAvailable = profile.BenchmarkedAt.HasValue
            && !string.Equals(profile.Backend, "cpu", StringComparison.OrdinalIgnoreCase);

        return new AiHealthStatus
        {
            Models = _inventory.GetAllStatuses(),
            MemoryUsedMB = CurrentMemoryUsageMB,
            MemoryLimitMB = 3000,
            GpuAvailable = gpuAvailable,
            MemoryProfile = "conservative",
            // Ready means text features can run now: the text model file is in place and the native runtime loads.
            // The optional audio pack never decides this.
            IsReady = _inventory.IsTextModelReady() && _runtimeStatus.TextRuntimeLoads,
        };
    }

    private async Task<bool> TryEnsureLoadedCoreAsync(AiModelRole role, CancellationToken ct)
    {
        if (CurrentlyLoadedRole == role && _inventory.GetState(role) == AiModelState.Loaded)
        {
            Touch();
            return true;
        }

        var state = _inventory.GetState(role);
        if (state is not AiModelState.Ready and not AiModelState.Loaded)
        {
            _logger.LogWarning("Cannot ensure model {Role}: state is {State}", role, state);
            return false;
        }

        try
        {
            await LoadModelCoreAsync(role, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load model {Role}", role);
            return false;
        }
    }

    private async Task LoadModelCoreAsync(AiModelRole role, CancellationToken ct)
    {
        if (CurrentlyLoadedRole == role)
        {
            Touch();
            return;
        }

        if (CurrentlyLoadedRole.HasValue)
        {
            await UnloadCurrentCoreAsync(ct).ConfigureAwait(false);
        }

        var state = _inventory.GetState(role);
        if (state != AiModelState.Ready)
        {
            throw new InvalidOperationException(
                $"Cannot load model {role}: state is {state} (must be Ready).");
        }

        var definition = _inventory.GetDefinition(role);
        var modelPath = _inventory.GetModelPath(role);
        _logger.LogInformation("Loading model {Role} from {Path}", role, modelPath);

        _artifactLease = SharedModelArtifact.AcquireRead(modelPath);
        _inventory.SetState(role, AiModelState.Loaded);
        lock (_stateLock)
        {
            _currentRole = role;
            _currentMemoryMB = definition.SizeMB;
            _lastAccessTime = DateTimeOffset.UtcNow;
        }

        await PublishStateChangeAsync(
            role,
            AiModelState.Ready,
            AiModelState.Loaded,
            ct).ConfigureAwait(false);
    }

    private async Task UnloadCurrentCoreAsync(CancellationToken ct)
    {
        var role = CurrentlyLoadedRole;
        if (!role.HasValue)
        {
            return;
        }

        _logger.LogInformation("Unloading model {Role}", role.Value);
        foreach (var disposer in GetModelDisposers())
        {
            await disposer(role.Value, ct).ConfigureAwait(false);
        }

        _artifactLease?.Dispose();
        _artifactLease = null;
        _inventory.SetState(role.Value, AiModelState.Ready);
        lock (_stateLock)
        {
            _currentRole = null;
            _currentMemoryMB = 0;
        }

        await PublishStateChangeAsync(
            role.Value,
            AiModelState.Loaded,
            AiModelState.Ready,
            ct).ConfigureAwait(false);
    }

    private Func<AiModelRole, CancellationToken, ValueTask>[] GetModelDisposers()
    {
        lock (_disposerLock)
        {
            return [.. _modelDisposers];
        }
    }

    private async Task PublishStateChangeAsync(
        AiModelRole role,
        AiModelState oldState,
        AiModelState newState,
        CancellationToken ct)
    {
        try
        {
            await _eventPublisher.PublishAsync(
                SignalREvents.ModelStateChanged,
                new ModelStateChangedEvent(
                    role.ToString(),
                    oldState.ToString(),
                    newState.ToString()),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish model state change for {Role}", role);
        }
    }

    private async Task MonitorIdleUnloadAsync(CancellationToken ct)
    {
        var idleTimeout = TimeSpan.FromSeconds(_runtimeSettings.IdleUnloadSeconds);
        var checkInterval = idleTimeout < MaximumIdleCheckInterval
            ? idleTimeout
            : MaximumIdleCheckInterval;

        if (checkInterval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(checkInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!CurrentlyLoadedRole.HasValue || DateTimeOffset.UtcNow - LastAccessTime < idleTimeout)
                {
                    continue;
                }

                await _runtimeGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (CurrentlyLoadedRole.HasValue && DateTimeOffset.UtcNow - LastAccessTime >= idleTimeout)
                    {
                        await UnloadCurrentCoreAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _runtimeGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI idle-unload monitor stopped unexpectedly");
        }
    }

    private DateTimeOffset LastAccessTime
    {
        get
        {
            lock (_stateLock)
            {
                return _lastAccessTime;
            }
        }
    }

    private void Touch()
    {
        lock (_stateLock)
        {
            _lastAccessTime = DateTimeOffset.UtcNow;
        }
    }

    private void ReleaseInferenceLease()
    {
        Touch();
        _runtimeGate.Release();
    }

    private void RemoveDisposer(Func<AiModelRole, CancellationToken, ValueTask> disposer)
    {
        lock (_disposerLock)
        {
            _modelDisposers.Remove(disposer);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _idleMonitorTask.ConfigureAwait(false);

        await _runtimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnloadCurrentCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _runtimeGate.Release();
        }

        _shutdown.Dispose();
        _runtimeGate.Dispose();
    }

    private sealed class InferenceLease(ModelLifecycleManager owner) : IAsyncDisposable
    {
        private ModelLifecycleManager? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseInferenceLease();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposerRegistration(
        ModelLifecycleManager owner,
        Func<AiModelRole, CancellationToken, ValueTask> disposer) : IDisposable
    {
        private ModelLifecycleManager? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.RemoveDisposer(disposer);
        }
    }
}
