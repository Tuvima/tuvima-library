using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.Backups;

/// <summary>A user export ready to download: the file's name and its bytes.</summary>
/// <param name="FileName">The suggested file name.</param>
/// <param name="Content">The zip file.</param>
public sealed record UserExportDownload(string FileName, byte[] Content);

/// <summary>
/// Moves people between Libraries (backup and restore spec D19, users spec D9 and D16): export your own profile and its sign-in, a
/// household (for its household administrator), or anyone (for the server administrator), and import such a file as new profiles in a
/// household the administrator chooses. The file carries what is needed to sign in, so every method checks who is asking.
/// </summary>
public interface IUserTransferService
{
    /// <summary>Exports the signed-in person's own profile and the sign-in that opens it. Refuses application and service callers.</summary>
    Task<UserExportDownload> ExportOwnAsync(RequestAuthority actor, CancellationToken ct);

    /// <summary>Exports one user's profile and sign-in. Needs an effective server administrator.</summary>
    Task<UserExportDownload> ExportAsync(RequestAuthority actor, Guid accountId, CancellationToken ct);

    /// <summary>
    /// Exports a whole household: every profile, their sign-ins and their own state. Needs the household's administrator, or an effective
    /// server administrator. Another household and a household that does not exist get the same answer.
    /// </summary>
    Task<UserExportDownload> ExportHouseholdAsync(RequestAuthority actor, Guid householdId, CancellationToken ct);

    /// <summary>
    /// Checks an uploaded user file and keeps it for a short while under an operation id. Writes nothing to the data store. Needs an
    /// effective administrator.
    /// </summary>
    Task<UserImportPreviewDto> InspectAsync(RequestAuthority actor, Stream upload, string originalFileName, CancellationToken ct);

    /// <summary>
    /// Imports a checked file as new profiles in the household the administrator chose, in one step. The kept file is removed once it is
    /// imported; a refused import keeps it so the choices can be changed. Needs an effective administrator.
    /// </summary>
    Task<UserImportResultDto> ImportAsync(RequestAuthority actor, Guid operationId, UserImportRequestDto request, CancellationToken ct);
}
