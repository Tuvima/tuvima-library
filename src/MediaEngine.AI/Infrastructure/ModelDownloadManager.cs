using System.Security.Cryptography;
using MediaEngine.AI.Configuration;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Infrastructure;

/// <summary>Owns verified downloads keyed by physical model artifact.</summary>
public sealed class ModelDownloadManager : IModelDownloadManager, IAsyncDisposable, IDisposable
{
    private static readonly StringComparer ArtifactComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly ModelInventory _inventory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<ModelDownloadManager> _logger;
    private readonly int _minimumFreeDiskMb;
    private readonly AiSettings _liveSettings;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Dictionary<string, DownloadOperation> _activeDownloads = new(ArtifactComparer);
    private readonly Dictionary<string, ModelDownloadResult> _completedDownloads = new(ArtifactComparer);
    private readonly Dictionary<string, (long Downloaded, long Total)> _progress = new(ArtifactComparer);
    private readonly object _lock = new();
    private bool _disposed;

    public ModelDownloadManager(
        AiSettings settings,
        ModelInventory inventory,
        IHttpClientFactory httpClientFactory,
        IEventPublisher eventPublisher,
        ILogger<ModelDownloadManager> logger)
    {
        var snapshot = AiRuntimeSettingsSnapshot.Create(settings);
        _minimumFreeDiskMb = snapshot.MinimumFreeDiskMB;
        _liveSettings = settings;
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartDownloadAsync(AiModelRole role, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        var definition = _inventory.GetDefinition(role);
        if (definition.DownloadUri is null)
        {
            throw new InvalidOperationException($"No download URL is configured for model role {role}.");
        }

        var artifact = _inventory.GetArtifactKey(role);
        var sharedRoles = _inventory.GetRolesSharingArtifact(role);
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_activeDownloads.ContainsKey(artifact))
            {
                return Task.CompletedTask;
            }

            if (sharedRoles.Any(sharedRole => _inventory.GetState(sharedRole) == AiModelState.Loaded))
            {
                throw new InvalidOperationException(
                    $"Cannot replace model artifact {Path.GetFileName(artifact)} while it is loaded.");
            }

            var previousStates = sharedRoles.ToDictionary(
                sharedRole => sharedRole,
                _inventory.GetState);
            var operation = new DownloadOperation(
                role,
                sharedRoles,
                previousStates,
                CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token));
            _activeDownloads[artifact] = operation;
            _completedDownloads.Remove(artifact);
            _inventory.SetArtifactState(role, AiModelState.Downloading);
            operation.Execution = RunDownloadAsync(artifact, definition, operation);
        }

        return Task.CompletedTask;
    }

    public async Task<ModelDownloadResult> WaitForCompletionAsync(
        AiModelRole role,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var artifact = _inventory.GetArtifactKey(role);
        Task<ModelDownloadResult>? completion;
        ModelDownloadResult? completed;
        lock (_lock)
        {
            completion = _activeDownloads.GetValueOrDefault(artifact)?.Completion.Task;
            _completedDownloads.TryGetValue(artifact, out completed);
        }

        if (completion is not null)
        {
            var result = await completion.WaitAsync(ct).ConfigureAwait(false);
            return result with { Role = role };
        }

        if (completed is not null)
        {
            return completed with { Role = role };
        }

        var state = _inventory.GetState(role);
        return state is AiModelState.Ready or AiModelState.Loaded
            ? new(role, ModelDownloadOutcome.AlreadyAvailable)
            : new(role, ModelDownloadOutcome.NotStarted, "No download has been started for this artifact.");
    }

    public async Task CancelDownloadAsync(AiModelRole role, CancellationToken ct = default)
    {
        var artifact = _inventory.GetArtifactKey(role);
        DownloadOperation? operation;
        lock (_lock)
        {
            _activeDownloads.TryGetValue(artifact, out operation);
        }

        if (operation is null)
        {
            return;
        }

        await operation.RequestCancellationAsync().ConfigureAwait(false);
        await operation.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public AiModelStatus GetStatus(AiModelRole role)
    {
        var definition = _inventory.GetDefinition(role);
        var artifact = _inventory.GetArtifactKey(role);
        lock (_lock)
        {
            var progress = _progress.GetValueOrDefault(artifact);
            return new AiModelStatus
            {
                Role = role,
                ModelType = role == AiModelRole.Audio ? AiModelType.Audio : AiModelType.Text,
                State = _inventory.GetState(role),
                ModelFile = definition.File,
                SizeMB = definition.SizeMB,
                DownloadProgressPercent = progress.Total > 0
                    ? (int)Math.Clamp(progress.Downloaded * 100 / progress.Total, 0, 100)
                    : 0,
                BytesDownloaded = progress.Downloaded,
                TotalBytes = progress.Total,
            };
        }
    }

    public IReadOnlyList<AiModelStatus> GetAllStatuses() =>
        (_liveSettings.AudioPackEnabled
            ? new[] { AiModelRole.TextQuality, AiModelRole.Audio }
            : new[] { AiModelRole.TextQuality })
        .Select(GetStatus)
        .ToList();

    public bool AreAllModelsReady() => _inventory.AreAllReady();

    public async Task DeleteModelAsync(AiModelRole role, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await CancelDownloadAsync(role, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var artifact = _inventory.GetArtifactKey(role);
        var sharedRoles = _inventory.GetRolesSharingArtifact(role);
        if (sharedRoles.Any(sharedRole => _inventory.GetState(sharedRole) == AiModelState.Loaded))
        {
            throw new InvalidOperationException(
                $"Cannot delete model artifact {Path.GetFileName(artifact)} while it is loaded.");
        }

        if (File.Exists(artifact))
        {
            using var lease = SharedModelArtifact.AcquireWrite(artifact);
            SharedModelArtifact.RequireManaged(artifact);
            File.Delete(artifact);
            TryDelete(SharedModelArtifact.OwnershipPath(artifact));
        }

        lock (_lock)
        {
            _completedDownloads.Remove(artifact);
            _progress.Remove(artifact);
        }

        _inventory.RefreshArtifact(role);
    }

    private async Task RunDownloadAsync(
        string artifact,
        AiModelRuntimeDefinition definition,
        DownloadOperation operation)
    {
        ModelDownloadResult result;
        try
        {
            await DownloadCoreAsync(artifact, definition, operation, operation.Cancellation.Token)
                .ConfigureAwait(false);
            result = new(operation.RequestedRole, ModelDownloadOutcome.Succeeded);
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            RestorePreviousStates(operation);
            result = new(operation.RequestedRole, ModelDownloadOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for model artifact {Artifact}", artifact);
            if (operation.PreviousStates.Values.Any(
                    state => state is AiModelState.Ready or AiModelState.Loaded))
            {
                RestorePreviousStates(operation);
            }
            else
            {
                _inventory.SetArtifactState(operation.RequestedRole, AiModelState.Error);
            }

            result = new(operation.RequestedRole, ModelDownloadOutcome.Failed, ex.Message);
        }
        finally
        {
            if (operation.StagingPath is not null)
            {
                TryDelete(operation.StagingPath);
            }
        }

        lock (_lock)
        {
            _completedDownloads[artifact] = result;
            if (_activeDownloads.GetValueOrDefault(artifact) == operation)
            {
                _activeDownloads.Remove(artifact);
            }

            _progress.Remove(artifact);
        }

        operation.Completion.TrySetResult(result);
        operation.Cancellation.Dispose();
    }

    private async Task DownloadCoreAsync(
        string artifact,
        AiModelRuntimeDefinition definition,
        DownloadOperation operation,
        CancellationToken ct)
    {
        using var lease = SharedModelArtifact.AcquireWrite(artifact);
        _inventory.RefreshArtifact(operation.RequestedRole);
        if (_inventory.GetState(operation.RequestedRole) == AiModelState.Ready)
        {
            return; // Another process, or a prior invocation, already installed this artifact.
        }
        if (File.Exists(artifact))
        {
            SharedModelArtifact.RequireManaged(artifact);
        }
        _inventory.SetArtifactState(operation.RequestedRole, AiModelState.Downloading);
        var directory = Path.GetDirectoryName(artifact)
            ?? throw new InvalidOperationException("Model path has no parent directory.");
        Directory.CreateDirectory(directory);
        var estimatedBytes = checked(definition.SizeMB * 1024L * 1024L);
        EnsureDiskCapacity(directory, estimatedBytes);

        var client = _httpClientFactory.CreateClient("ModelDownload");
        using var response = await client.GetAsync(
            definition.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? estimatedBytes;
        if (totalBytes <= 0)
        {
            throw new InvalidOperationException(
                $"Download for {operation.RequestedRole} did not provide a valid size.");
        }

        EnsureDiskCapacity(directory, totalBytes);
        lock (_lock)
        {
            _progress[artifact] = (0, totalBytes);
        }

        var tempPath = SharedModelArtifact.StagingPath(artifact);
        operation.StagingPath = tempPath;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        var lastProgressReport = DateTimeOffset.MinValue;
        while (true)
        {
            var bytesRead = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            downloaded = checked(downloaded + bytesRead);
            lock (_lock)
            {
                _progress[artifact] = (downloaded, totalBytes);
            }

            if (DateTimeOffset.UtcNow - lastProgressReport >= TimeSpan.FromMilliseconds(500))
            {
                lastProgressReport = DateTimeOffset.UtcNow;
                await PublishProgressAsync(operation.SharedRoles, downloaded, totalBytes, ct)
                    .ConfigureAwait(false);
            }
        }

        if (response.Content.Headers.ContentLength.HasValue && downloaded != totalBytes)
        {
            throw new IOException(
                $"Incomplete model download: expected {totalBytes} bytes, received {downloaded}.");
        }

        await fileStream.FlushAsync(ct).ConfigureAwait(false);
        fileStream.Position = 0;
        if (!string.IsNullOrWhiteSpace(definition.Sha256))
        {
            var actual = await SHA256.HashDataAsync(fileStream, ct).ConfigureAwait(false);
            var expected = Convert.FromHexString(definition.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new InvalidDataException(
                    $"SHA-256 mismatch for model artifact {Path.GetFileName(artifact)}.");
            }
        }
        else
        {
            _logger.LogWarning(
                "Model artifact {Artifact} has no SHA-256 configured; transport integrity relies on HTTPS",
                artifact);
        }

        await fileStream.DisposeAsync().ConfigureAwait(false);
        File.Move(tempPath, artifact, overwrite: true);
        SharedModelArtifact.RecordOwnership(artifact);
        _inventory.RefreshArtifact(operation.RequestedRole);
        await PublishStateChangedAsync(operation.SharedRoles).ConfigureAwait(false);
    }

    private void RestorePreviousStates(DownloadOperation operation)
    {
        foreach (var (role, state) in operation.PreviousStates)
        {
            _inventory.SetState(role, state);
        }
    }

    private void EnsureDiskCapacity(string directory, long downloadBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory))
            ?? throw new InvalidOperationException("Could not resolve the model volume.");
        var available = new DriveInfo(root).AvailableFreeSpace;
        var reserve = checked(_minimumFreeDiskMb * 1024L * 1024L);
        if (downloadBytes > available - Math.Min(available, reserve))
        {
            throw new IOException(
                $"Insufficient disk space for model download: {available} bytes available; " +
                $"{downloadBytes} bytes required plus {reserve} bytes reserved.");
        }
    }

    private async Task PublishProgressAsync(
        IReadOnlyList<AiModelRole> roles,
        long downloaded,
        long total,
        CancellationToken ct)
    {
        foreach (var role in roles)
        {
            try
            {
                await _eventPublisher.PublishAsync(
                    SignalREvents.ModelDownloadProgress,
                    new ModelDownloadProgressEvent(
                        role.ToString(),
                        (int)Math.Clamp(downloaded * 100 / total, 0, 100),
                        downloaded,
                        total),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not publish model download progress for {Role}", role);
            }
        }
    }

    private async Task PublishStateChangedAsync(IReadOnlyList<AiModelRole> roles)
    {
        foreach (var role in roles)
        {
            try
            {
                await _eventPublisher.PublishAsync(
                    SignalREvents.ModelStateChanged,
                    new ModelStateChangedEvent(
                        role.ToString(),
                        AiModelState.Downloading.ToString(),
                        AiModelState.Ready.ToString()),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not publish downloaded model state for {Role}", role);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the next download uses FileMode.Create and replaces it.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        List<DownloadOperation> operations;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            operations = _activeDownloads.Values.ToList();
        }

        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        foreach (var operation in operations)
        {
            await operation.RequestCancellationAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(operations.Select(operation => operation.Execution)).ConfigureAwait(false);
        _shutdownCts.Dispose();
    }

    private sealed class DownloadOperation(
        AiModelRole requestedRole,
        IReadOnlyList<AiModelRole> sharedRoles,
        IReadOnlyDictionary<AiModelRole, AiModelState> previousStates,
        CancellationTokenSource cancellation)
    {
        public AiModelRole RequestedRole { get; } = requestedRole;
        public IReadOnlyList<AiModelRole> SharedRoles { get; } = sharedRoles;
        public IReadOnlyDictionary<AiModelRole, AiModelState> PreviousStates { get; } = previousStates;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<ModelDownloadResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Execution { get; set; } = Task.CompletedTask;
        public string? StagingPath { get; set; }

        public async ValueTask RequestCancellationAsync()
        {
            try
            {
                await Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race and already released the operation token.
            }
        }
    }
}
