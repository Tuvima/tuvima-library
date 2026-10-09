using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<SetupLocaleDto?> GetSetupLocaleAsync(string? setupSession, CancellationToken ct = default);
    Task<SetupLocaleDto?> SaveSetupLocaleAsync(SetupLocaleDto locale, string? setupSession, CancellationToken ct = default);
    Task<SetupStatusDto?> GetSetupStatusAsync(CancellationToken ct = default);
    /// <summary>
    /// Starts setup. <paramref name="request"/> carries the Dashboard's own ingress classification and, when the
    /// visitor is not on this computer, the setup code they typed.
    /// </summary>
    Task<SetupBeginOutcome> BeginSetupAsync(SetupBeginRequest request, CancellationToken ct = default);
    Task<SetupPreflightDto?> RunSetupPreflightAsync(string? setupSession, CancellationToken ct = default);
    Task<SetupAdministratorResponse?> CreateSetupAdministratorAsync(SetupAdministratorRequest request, string setupSession, CancellationToken ct = default);
    Task<SetupMediaLocationsDto?> ValidateSetupMediaLocationsAsync(string? setupSession, CancellationToken ct = default);
    Task<LibrariesConfigurationDto?> GetSetupLibrariesAsync(string? setupSession, CancellationToken ct = default);
    Task<LibrariesConfigurationDto?> UpdateSetupLibrariesAsync(UpdateLibrariesRequest request, string? setupSession, CancellationToken ct = default);
    Task<IReadOnlyList<ServerStorageLocationDto>> GetSetupServerFolderRootsAsync(string? setupSession, CancellationToken ct = default);
    Task<BrowseServerFoldersResultDto?> BrowseSetupServerFoldersAsync(BrowseServerFoldersRequest request, string? setupSession, CancellationToken ct = default);
    Task<ServerFolderValidationResultDto?> ValidateSetupServerFolderAsync(ValidateServerFolderRequest request, string? setupSession, CancellationToken ct = default);
    Task<ProviderCredentialOperationResultDto?> TestSetupProviderCredentialsAsync(string name, ProviderCredentialWriteRequest request, string? setupSession, CancellationToken ct = default);
    Task<ProviderCredentialOperationResultDto?> SaveSetupProviderCredentialsAsync(string name, ProviderCredentialWriteRequest request, string? setupSession, CancellationToken ct = default);
    Task<SetupStatusDto?> DecideSetupStepAsync(string stepKey, string status, string? detail, string? setupSession, CancellationToken ct = default);
    Task<SetupBackupInspectionDto?> UploadSetupBackupAsync(Stream stream, string fileName, string setupSession, CancellationToken ct = default);
    Task<SetupRestoreConfirmationDto?> ConfirmSetupRestoreAsync(Guid operationId, string setupSession, CancellationToken ct = default);
    Task<SetupReadinessDto?> GetSetupReadinessAsync(string? setupSession, CancellationToken ct = default);
    Task<SetupStatusDto?> CompleteSetupAsync(string? setupSession, CancellationToken ct = default);
}

/// <summary>What the Engine said to a request to start setup.</summary>
/// <param name="Started">The new setup session, or null when setup did not start.</param>
/// <param name="Refusal">Why the Engine refused (code needed, code wrong, or not on the home network), when it did.</param>
public sealed record SetupBeginOutcome(SetupStartResponse? Started, SetupBeginRefusalDto? Refusal);
