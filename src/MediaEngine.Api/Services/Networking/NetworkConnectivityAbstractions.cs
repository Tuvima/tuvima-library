namespace MediaEngine.Api.Services.Networking;

public enum RouterMappingState
{
    NotAttempted,
    UnsupportedTopology,
    ProtocolUnavailable,
    RouterRefused,
    Active,
    Expired,
    Failed,
}

public sealed record RouterMappingRequest(
    string Description,
    string InternalAddress,
    int InternalPort,
    int ExternalPort,
    TimeSpan LeaseDuration);

public sealed record RouterMappingResult(
    RouterMappingState State,
    string Method,
    string Message,
    int? ExternalPort = null,
    DateTimeOffset? ExpiresAt = null,
    string? PublicAddress = null,
    string? ReasonCode = null);

public interface IRouterPortMapper
{
    string Method { get; }
    int Priority { get; }
    Task<RouterMappingResult> TryCreateAsync(RouterMappingRequest request, CancellationToken ct);
    Task<RouterMappingResult> TryRenewAsync(RouterMappingRequest request, CancellationToken ct);
    Task RemoveOwnedAsync(RouterMappingRequest request, CancellationToken ct);
}

public enum RemoteProviderState
{
    Unconfigured,
    Connecting,
    Connected,
    Degraded,
    Error,
}

public sealed record RemoteProviderSnapshot(
    string Key,
    string DisplayName,
    RemoteProviderState State,
    string? PublicAddress,
    string Message,
    bool SecureHttps = false,
    bool NotInstalled = false);

public interface IRemoteConnectivityProvider
{
    string Key { get; }
    string DisplayName { get; }
    Task<RemoteProviderSnapshot> GetStateAsync(CancellationToken ct);
    Task<RemoteProviderSnapshot> TestAsync(CancellationToken ct);
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}
