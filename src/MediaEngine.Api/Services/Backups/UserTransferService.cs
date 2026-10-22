using System.Globalization;
using System.IO.Compression;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// Export and import of one user (backup and restore spec D19). The file carries what is needed to sign in as that person (their email,
/// password hash and two-step secret), so a person may export only themselves, only an effective administrator may export or import anyone
/// else, application and service callers are refused, and the routes that call this ask the person to confirm it is them first. Access
/// rights are never carried; the receiving administrator chooses them afterwards. Nothing here logs an email, a profile name or any secret.
/// </summary>
public sealed class UserTransferService(
    PersonalStateBackupRepository export,
    UserImportRepository import,
    IBackupPortableValueCodec codec,
    UserImportStagingArea staging,
    IAccountRepository accounts,
    IAccountAccessDecisionService decisions,
    IAuthorizationAuditWriter audit,
    TimeProvider clock) : IUserTransferService
{
    /// <summary>The largest file an administrator can upload for a user import, in bytes.</summary>
    public const long MaxUploadBytes = 256L * 1024 * 1024;

    /// <summary>What an administrator who is not allowed to do this is told.</summary>
    public const string AdministratorRequiredMessage = "Only an administrator can move another person.";

    /// <summary>What a person is told when their sign-in is not a person's own sign-in.</summary>
    public const string PersonRequiredMessage = "Only a signed-in person can export their own data.";

    /// <summary>What an administrator is told when the account has no email, so it could not be found again on import.</summary>
    public const string NoEmailMessage = "This person has no email address, so their data can't be exported.";

    /// <summary>What an administrator is told when the kept file is gone (it waits for an hour).</summary>
    public const string ExpiredMessage = "This file is no longer kept. Choose it again.";

    /// <summary>The sentence for a refused import, in plain words.</summary>
    public static string SentenceFor(UserImportRefusal refusal, string email) => refusal switch
    {
        UserImportRefusal.EmailInUse => $"Someone in this Library already signs in with {email}. Nothing was imported.",
        UserImportRefusal.OutsideSignInInUse => "This person's outside sign-in is already linked to someone here. Nothing was imported.",
        UserImportRefusal.TwoStepCannotMove => "This person's two-step sign-in can't be moved to this Library. Nothing was imported.",
        _ => "This user is already in this Library. Nothing was imported.",
    };

    /// <inheritdoc/>
    public async Task<UserExportDownload> ExportOwnAsync(RequestAuthority actor, CancellationToken ct)
    {
        if (actor.PrincipalKind != PrincipalKind.Human || actor.AccountId is not { } accountId)
        {
            throw new UnauthorizedAccessException(PersonRequiredMessage);
        }

        return await ExportCoreAsync(actor, accountId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<UserExportDownload> ExportAsync(RequestAuthority actor, Guid accountId, CancellationToken ct)
    {
        await RequireAdministratorAsync(actor, ct).ConfigureAwait(false);
        return await ExportCoreAsync(actor, accountId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<UserImportPreviewDto> InspectAsync(RequestAuthority actor, Stream upload, string originalFileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var administrator = await RequireAdministratorAsync(actor, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(originalFileName) || !originalFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(RestoreArchiveValidator.NotAUserExportMessage);
        }

        staging.Sweep();
        var operationId = Guid.NewGuid();
        var path = staging.NewPath(operationId);
        try
        {
            await WriteUploadAsync(upload, path, ct).ConfigureAwait(false);
            var checkedFile = RestoreArchiveValidator.ValidateUserExport(path);
            var refusal = import.Check(checkedFile.Summary);
            if (refusal != UserImportRefusal.None)
            {
                // A file that cannot be imported is not kept.
                staging.DeleteFile(path);
                return Preview(operationId, checkedFile.Summary, SentenceFor(refusal, checkedFile.Summary.Email));
            }

            staging.Keep(operationId, administrator, path, originalFileName);
            return Preview(operationId, checkedFile.Summary, null);
        }
        catch
        {
            staging.DeleteFile(path);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<UserImportResultDto> ImportAsync(RequestAuthority actor, Guid operationId, CancellationToken ct)
    {
        var administrator = await RequireAdministratorAsync(actor, ct).ConfigureAwait(false);
        var kept = staging.Find(operationId, administrator) ?? throw new KeyNotFoundException(ExpiredMessage);
        try
        {
            // Read again from the kept file, so what is imported is what is on disk now and has passed every check.
            var checkedFile = RestoreArchiveValidator.ValidateUserExport(kept.Path);
            UserImportResult result;
            try
            {
                result = import.Import(checkedFile.Export, kept.OriginalName, checkedFile.CreatedAt, codec, clock.GetUtcNow(), ct);
            }
            catch (UserImportRefusedException ex)
            {
                throw new InvalidOperationException(SentenceFor(ex.Refusal, checkedFile.Summary.Email), ex);
            }

            await audit.WriteAsync(
                new AuthorizationAuditEvent(
                    "account.imported",
                    clock.GetUtcNow(),
                    actor.AccountId,
                    actor.ActiveProfileId,
                    actor.ApplicationId,
                    "account",
                    result.AccountId.ToString("D", CultureInfo.InvariantCulture),
                    new Dictionary<string, string?> { ["changed"] = "true" }),
                ct).ConfigureAwait(false);
            return new UserImportResultDto(result.AccountId, result.Email, result.ProfileNames, result.WaitingItems);
        }
        finally
        {
            staging.Discard(operationId);
        }
    }

    private async Task<UserExportDownload> ExportCoreAsync(RequestAuthority actor, Guid accountId, CancellationToken ct)
    {
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Account not found.");
        if (string.IsNullOrWhiteSpace(account.Email))
        {
            throw new InvalidOperationException(NoEmailMessage);
        }

        // The export reads one consistent snapshot of its own, so nothing is locked and the Library keeps working.
        var content = await Task.Run(() => export.Export(BackupScope.ForAccount(accountId), ct), ct).ConfigureAwait(false);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            BackupArchiveFormat.Write(archive, content);
            BackupManifestWriter.WriteUser(archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize), content.Contents, account.Email.Trim());
        }

        await audit.WriteAsync(
            new AuthorizationAuditEvent(
                "account.exported",
                clock.GetUtcNow(),
                actor.AccountId,
                actor.ActiveProfileId,
                actor.ApplicationId,
                "account",
                accountId.ToString("D", CultureInfo.InvariantCulture),
                new Dictionary<string, string?> { ["changed"] = "true" }),
            ct).ConfigureAwait(false);
        return new UserExportDownload(
            $"tuvima-user-{clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip",
            stream.ToArray());
    }

    /// <summary>The administrator's account id when they may move other people; otherwise a refusal.</summary>
    private async Task<Guid> RequireAdministratorAsync(RequestAuthority actor, CancellationToken ct)
    {
        if (actor.PrincipalKind == PrincipalKind.Human
            && actor.AccountId is { } accountId
            && (await decisions.EvaluateAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
        {
            return accountId;
        }

        throw new UnauthorizedAccessException(AdministratorRequiredMessage);
    }

    private static async Task WriteUploadAsync(Stream upload, string path, CancellationToken ct)
    {
        await using var destination = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await upload.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxUploadBytes)
            {
                throw new InvalidDataException(RestoreArchiveValidator.UserExportDamagedMessage);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    private static UserImportPreviewDto Preview(Guid operationId, UserExportSummary summary, string? blockedReason) =>
        new(operationId, summary.Email, summary.HouseholdName, summary.ProfileNames, summary.PersonalItems, blockedReason);
}
