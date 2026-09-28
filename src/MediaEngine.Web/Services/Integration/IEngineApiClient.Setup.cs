using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<SetupLocaleDto?> GetSetupLocaleAsync(string? setupSession, CancellationToken ct = default);
    Task<SetupLocaleDto?> SaveSetupLocaleAsync(SetupLocaleDto locale, string? setupSession, CancellationToken ct = default);
    Task<SetupStatusDto?> GetSetupStatusAsync(CancellationToken ct = default);
    Task<SetupStartResponse?> BeginSetupAsync(CancellationToken ct = default);
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
