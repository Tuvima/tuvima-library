using System.Text.Json.Serialization;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Contracts.Setup;

public static class SetupWorkflow
{
    public const int CurrentVersion = 1;
    public static readonly string[] StepKeys =
    [
        "preflight", "administrator", "media-locations",
        "providers", "readiness",
    ];
}

public sealed record SetupStepStatusDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("repair_target")] string? RepairTarget,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt);

public sealed record SetupStatusDto(
    [property: JsonPropertyName("workflow_version")] int WorkflowVersion,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("current_step")] string CurrentStep,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("requires_setup_start")] bool RequiresSetupStart,
    [property: JsonPropertyName("requires_authentication")] bool RequiresAuthentication,
    [property: JsonPropertyName("administrator_configured")] bool AdministratorConfigured,
    [property: JsonPropertyName("steps")] IReadOnlyList<SetupStepStatusDto> Steps);

/// <summary>
/// Starts first-run setup. <c>original_client_ingress</c> is the Dashboard's own classification of the visitor
/// (never a browser-supplied value); a missing or unknown value is treated as <c>remote</c>.
/// <c>setup_code</c> is the code printed by <c>tuvima-admin setup code</c>; it is not needed from this computer.
/// </summary>
public sealed class SetupBeginRequest
{
    [JsonPropertyName("setup_code")] public string? SetupCode { get; init; }
    [JsonPropertyName("original_client_ingress")] public string OriginalClientIngress { get; init; } = ClientIngressValues.Remote;
}

/// <summary>Why the Engine refused to start setup (HTTP 403 body of <c>POST /setup/v1/begin</c>).</summary>
public sealed record SetupBeginRefusalDto(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("message")] string Message);

public static class SetupBeginRefusalReasons
{
    /// <summary>The visitor is on the home network and has not supplied a setup code.</summary>
    public const string CodeRequired = "setup_code_required";

    /// <summary>A code was supplied but is wrong, expired, used, or replaced.</summary>
    public const string CodeInvalid = "setup_code_invalid";

    /// <summary>The visitor is outside the home network; setup is never done over the internet.</summary>
    public const string RemoteRefused = "setup_remote_refused";
}

public sealed record SetupStartResponse(
    [property: JsonPropertyName("setup_session_token")] string SetupSessionToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("status")] SetupStatusDto Status);

public sealed record SetupPathCheckDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("path_source")] string PathSource,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("readable")] bool Readable,
    [property: JsonPropertyName("writable")] bool Writable,
    [property: JsonPropertyName("free_bytes")] long? FreeBytes,
    [property: JsonPropertyName("detail")] string Detail);

public sealed record SetupPreflightDto(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("running_in_container")] bool RunningInContainer,
    [property: JsonPropertyName("container_explanation")] string ContainerExplanation,
    [property: JsonPropertyName("runtime_identifier")] string RuntimeIdentifier,
    [property: JsonPropertyName("os_architecture")] string OsArchitecture,
    [property: JsonPropertyName("process_architecture")] string ProcessArchitecture,
    [property: JsonPropertyName("ffmpeg_status")] string FfmpegStatus,
    [property: JsonPropertyName("checks")] IReadOnlyList<SetupPathCheckDto> Checks);

public sealed class SetupAdministratorRequest
{
    [JsonPropertyName("pin")] public string? Pin { get; init; }
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    [JsonPropertyName("password")] public string Password { get; init; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = "Administrator";
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = string.Empty;
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;
}

public sealed record SetupAdministratorResponse(
    [property: JsonPropertyName("created")] bool Created,
    [property: JsonPropertyName("recovery_codes")] IReadOnlyList<string> RecoveryCodes);

public sealed record SetupMediaLocationsDto(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("configured")] int Configured,
    [property: JsonPropertyName("readable")] int Readable,
    [property: JsonPropertyName("detail")] string Detail);

public sealed class SetupStepDecisionRequest
{
    [JsonPropertyName("status")] public string Status { get; init; } = "passed";
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

public sealed record SetupBackupInspectionDto(
    [property: JsonPropertyName("operation_id")] Guid OperationId,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("manifest_version")] string ManifestVersion,
    [property: JsonPropertyName("database_epoch")] string DatabaseEpoch,
    [property: JsonPropertyName("compressed_bytes")] long CompressedBytes,
    [property: JsonPropertyName("uncompressed_bytes")] long UncompressedBytes,
    [property: JsonPropertyName("entry_count")] int EntryCount,
    [property: JsonPropertyName("configuration_files")] int ConfigurationFiles,
    [property: JsonPropertyName("warning")] string Warning);

public sealed record SetupRestoreConfirmationDto(
    [property: JsonPropertyName("scheduled")] bool Scheduled,
    [property: JsonPropertyName("restart_required")] bool RestartRequired,
    [property: JsonPropertyName("message")] string Message);

public sealed record SetupCapabilityDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("repair_target")] string? RepairTarget);

public sealed record SetupReadinessDto(
    [property: JsonPropertyName("can_complete")] bool CanComplete,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<SetupCapabilityDto> Capabilities);
