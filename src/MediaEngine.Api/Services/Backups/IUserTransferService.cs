using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.Backups;

/// <summary>A user export ready to download: the file's name and its bytes.</summary>
/// <param name="FileName">The suggested file name.</param>
/// <param name="Content">The zip file.</param>
public sealed record UserExportDownload(string FileName, byte[] Content);

/// <summary>
/// Moves one user between Libraries (backup and restore spec D19): export the signed-in person, or any user for an administrator, and
/// import such a file as a new user. The file carries what is needed to sign in, so every method checks who is asking.
/// </summary>
public interface IUserTransferService
{
    /// <summary>Exports the signed-in person's own account. Refuses application and service callers.</summary>
    Task<UserExportDownload> ExportOwnAsync(RequestAuthority actor, CancellationToken ct);

    /// <summary>Exports any user. Needs an effective administrator.</summary>
    Task<UserExportDownload> ExportAsync(RequestAuthority actor, Guid accountId, CancellationToken ct);

    /// <summary>
    /// Checks an uploaded user export and keeps it for a short while under an operation id. Writes nothing to the data store. Needs an
    /// effective administrator.
    /// </summary>
    Task<UserImportPreviewDto> InspectAsync(RequestAuthority actor, Stream upload, string originalFileName, CancellationToken ct);

    /// <summary>Imports a checked file as a new user, in one step, and removes the kept file. Needs an effective administrator.</summary>
    Task<UserImportResultDto> ImportAsync(RequestAuthority actor, Guid operationId, CancellationToken ct);
}
