using System.Globalization;
using System.IO.Compression;
using System.Net.Mail;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// Export and import of people (backup and restore spec D19, users spec D9 and D16). A file carries what is needed to sign in as each
/// person it holds (their email and password hash, and their two-step secret only when they export themselves; a file made for someone
/// else leaves it out), so a person may export only their own profile, a household administrator only their own household, only the
/// server administrator anyone, and only the server administrator may import. Application and service callers are refused, and the routes
/// that call this ask the person to confirm it is them first. An import never carries a role or library access, and Read, Watch,
/// Listen and View only when the administrator keeps them. Nothing here logs an email, a profile name or any secret.
/// </summary>
public sealed class UserTransferService(
    PersonalStateBackupRepository export,
    UserImportRepository import,
    IBackupPortableValueCodec codec,
    UserImportStagingArea staging,
    IAccountRepository accounts,
    IHouseholdRepository households,
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

    /// <summary>What an administrator is told when the user to export is not in this Library.</summary>
    public const string UserNotFoundMessage = "That user isn't in this Library.";

    /// <summary>What an administrator is told when the household to export is not in this Library (or is not theirs).</summary>
    public const string HouseholdNotFoundMessage = "That household isn't in this Library.";

    /// <summary>What an administrator is told when the file could not be made readable or protected on this computer.</summary>
    public const string ComputerProblemMessage = "The file couldn't be made on this computer.";

    /// <summary>What an administrator is told when the kept file is gone (it waits for an hour).</summary>
    public const string ExpiredMessage = "This file is no longer kept. Choose it again.";

    /// <summary>What an administrator is told when the import request has no choices in it.</summary>
    public const string ChoicesMissingMessage = "Choose where to put them first.";

    /// <summary>The sentence for a refused import, in plain words.</summary>
    public static string SentenceFor(UserImportRefusal refusal, string? email) => refusal switch
    {
        UserImportRefusal.EmailInUse => $"Someone in this Library already signs in with {email}. Nothing was imported.",
        UserImportRefusal.OutsideSignInInUse => "This person's outside sign-in is already linked to someone here. Nothing was imported.",
        UserImportRefusal.TwoStepCannotMove => "This person's two-step sign-in can't be moved to this Library. Nothing was imported.",
        UserImportRefusal.HouseholdChoiceNeeded => "Choose a household to put them in, or give a new household a name. Nothing was imported.",
        UserImportRefusal.HouseholdNotFound => "That household isn't in this Library. Nothing was imported.",
        UserImportRefusal.HouseholdFull => "A household can have up to 8 people, and this would be more. Nothing was imported.",
        UserImportRefusal.AdministratorChoiceNeeded =>
            "A new household needs someone to look after it: choose a person from the file who keeps a sign-in and isn't a Kids profile. Nothing was imported.",
        UserImportRefusal.SecureAdministratorsFirst => AddHouseholdBlockedMessage + " Nothing was imported.",
        UserImportRefusal.InvalidChoice => "One of the choices doesn't match this file. Nothing was imported.",
        UserImportRefusal.EmailMissing => "Every sign-in that is imported needs an email. Nothing was imported.",
        _ => "This user is already in this Library. Nothing was imported.",
    };

    /// <summary>Why a second household can't be added yet (users spec D10).</summary>
    public const string AddHouseholdBlockedMessage =
        "Set an email and password for your own sign-in first. A Library with several households must not be open to anyone who reaches it.";

    /// <summary>What a sign-in that can't move is told, whatever email it gets.</summary>
    private static string ProblemSentenceFor(UserImportRefusal problem) => problem switch
    {
        UserImportRefusal.TwoStepCannotMove => "Their two-step sign-in can't be moved to this Library. They can still come without a sign-in.",
        UserImportRefusal.OutsideSignInInUse => "Their outside sign-in is already linked to someone here. They can still come without a sign-in.",
        _ => "Their passkey is already registered in this Library. They can still come without a sign-in.",
    };

    /// <inheritdoc/>
    public async Task<UserExportDownload> ExportOwnAsync(RequestAuthority actor, CancellationToken ct)
    {
        if (actor.PrincipalKind != PrincipalKind.Human || actor.AccountId is not { } accountId)
        {
            throw new UnauthorizedAccessException(PersonRequiredMessage);
        }

        return await ExportAccountCoreAsync(actor, accountId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<UserExportDownload> ExportAsync(RequestAuthority actor, Guid accountId, CancellationToken ct)
    {
        await RequireAdministratorAsync(actor, ct).ConfigureAwait(false);
        return await ExportAccountCoreAsync(actor, accountId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<UserExportDownload> ExportHouseholdAsync(RequestAuthority actor, Guid householdId, CancellationToken ct)
    {
        if (actor.PrincipalKind != PrincipalKind.Human || actor.AccountId is not { } actorAccount)
        {
            throw new UnauthorizedAccessException(AdministratorRequiredMessage);
        }

        // The server administrator may export any household; a household administrator only their own. Someone who is neither is told no;
        // a household administrator who names another household, or one that does not exist, is told it isn't there (ids cannot be probed).
        var isServerAdministrator = (await decisions.EvaluateAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed;
        if (!isServerAdministrator)
        {
            if (!(await decisions.EvaluateHouseholdAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
            {
                throw new UnauthorizedAccessException(AdministratorRequiredMessage);
            }

            if (actor.ActiveProfileHouseholdId != householdId)
            {
                throw new KeyNotFoundException(HouseholdNotFoundMessage);
            }
        }

        if (await households.GetByIdAsync(householdId, ct).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException(HouseholdNotFoundMessage);
        }

        // Only the exporting person's own two-step secret stays in the file; everyone else sets theirs up again.
        var scope = BackupScope.ForHousehold(householdId, actorAccount);
        return await ExportCoreAsync(
            actor, scope, "household.exported", "household", householdId, UserExportSummary.HouseholdScope, "tuvima-household", ct).ConfigureAwait(false);
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
            var inspection = import.Inspect(checkedFile.Summary);
            staging.Keep(operationId, administrator, path, originalFileName);
            return Preview(operationId, checkedFile, inspection);
        }
        catch
        {
            staging.DeleteFile(path);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<UserImportResultDto> ImportAsync(RequestAuthority actor, Guid operationId, UserImportRequestDto request, CancellationToken ct)
    {
        var administrator = await RequireAdministratorAsync(actor, ct).ConfigureAwait(false);
        var kept = staging.Find(operationId, administrator) ?? throw new KeyNotFoundException(ExpiredMessage);
        var choices = ToChoices(request ?? throw new ArgumentException(ChoicesMissingMessage));

        // Read again from the kept file, so what is imported is what is on disk now and has passed every check.
        var checkedFile = RestoreArchiveValidator.ValidateUserExport(kept.Path);
        UserImportResult result;
        try
        {
            result = import.Import(checkedFile.Export, kept.OriginalName, checkedFile.CreatedAt, codec, clock.GetUtcNow(), choices, ct);
        }
        catch (UserImportRefusedException ex)
        {
            // The file stays kept, so the administrator can change a choice (another email, another household) and try again.
            throw new UserTransferRefusedException(SentenceFor(ex.Refusal, ex.Email), ex);
        }

        staging.Discard(operationId);
        await audit.WriteAsync(
            new AuthorizationAuditEvent(
                "account.imported",
                clock.GetUtcNow(),
                actor.AccountId,
                actor.ActiveProfileId,
                actor.ApplicationId,
                "household",
                result.HouseholdId.ToString("D", CultureInfo.InvariantCulture),
                new Dictionary<string, string?>
                {
                    ["changed"] = "true",
                    ["profiles"] = result.Profiles.Count.ToString(CultureInfo.InvariantCulture),
                    ["created_household"] = result.CreatedHousehold ? "true" : "false",
                    ["kept_access"] = choices.KeepAccess ? "true" : "false",
                }),
            ct).ConfigureAwait(false);
        return new UserImportResultDto(
            result.HouseholdId,
            result.HouseholdName,
            result.CreatedHousehold,
            result.Profiles.Select(profile => new UserImportedProfileDto(profile.ProfileId, profile.Name, profile.Email, profile.IsHouseholdAdministrator)).ToList(),
            result.WaitingItems);
    }

    /// <summary>The request as the data store's choices. Every email the administrator typed is checked here; nothing else is trusted to the data store.</summary>
    private static UserImportChoices ToChoices(UserImportRequestDto request)
    {
        string? newName = null;
        if (!string.IsNullOrWhiteSpace(request.NewHouseholdName))
        {
            newName = request.NewHouseholdName.Trim();
            if (newName.Length > HouseholdManagementService.MaximumNameLength)
            {
                throw new ArgumentException($"A household name must be {HouseholdManagementService.MaximumNameLength} characters or fewer.");
            }
        }

        var emails = new Dictionary<Guid, string>();
        foreach (var (signIn, email) in request.Emails ?? new Dictionary<Guid, string>())
        {
            emails[signIn] = NormalizeEmail(email);
        }

        return new UserImportChoices(
            request.TargetHouseholdId,
            newName,
            request.NewHouseholdAdministratorProfileId,
            request.KeepAccess,
            emails,
            request.WithoutSignIn?.Distinct().ToList());
    }

    private static string NormalizeEmail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 254)
        {
            throw new ArgumentException("Enter a valid email address.");
        }

        try
        {
            var address = new MailAddress(raw.Trim());
            return string.Equals(address.Address, raw.Trim(), StringComparison.Ordinal) ? address.Address : throw new ArgumentException("Enter a valid email address.");
        }
        catch (FormatException)
        {
            throw new ArgumentException("Enter a valid email address.");
        }
    }

    private async Task<UserExportDownload> ExportAccountCoreAsync(RequestAuthority actor, Guid accountId, CancellationToken ct)
    {
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException(UserNotFoundMessage);
        if (string.IsNullOrWhiteSpace(account.Email))
        {
            throw new UserTransferRefusedException(NoEmailMessage);
        }

        // Someone exporting another person never carries that person's two-step secret; a person's own file does.
        // Decided by whose account is exported, not by which route was used.
        var scope = BackupScope.ForAccount(accountId, leaveOutTwoStep: actor.AccountId != accountId);
        return await ExportCoreAsync(actor, scope, "account.exported", "account", accountId, UserExportSummary.ProfileScope, "tuvima-user", ct).ConfigureAwait(false);
    }

    private async Task<UserExportDownload> ExportCoreAsync(
        RequestAuthority actor,
        BackupScope scope,
        string auditEvent,
        string subjectType,
        Guid subjectId,
        string manifestScope,
        string filePrefix,
        CancellationToken ct)
    {
        // The export reads one consistent snapshot of its own, so nothing is locked and the Library keeps working.
        var content = await Task.Run(() => export.Export(scope, ct), ct).ConfigureAwait(false);
        var signIns = content.Tables.FirstOrDefault(table => string.Equals(table.Table, "accounts", StringComparison.OrdinalIgnoreCase))?.Rows.Count ?? 0;
        var ownSecretKept = scope.KeepTwoStepForAccountId is not null
            && content.Tables.Any(table => string.Equals(table.Table, UserExportScope.TwoStepTable, StringComparison.OrdinalIgnoreCase) && table.Rows.Count > 0);
        var twoStepLeftOut = scope.LeaveOutTwoStep && signIns > (ownSecretKept ? 1 : 0);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            BackupArchiveFormat.Write(archive, content);
            BackupManifestWriter.WriteUser(archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize), content.Contents, manifestScope, signIns, twoStepLeftOut);
        }

        await audit.WriteAsync(
            new AuthorizationAuditEvent(
                auditEvent,
                clock.GetUtcNow(),
                actor.AccountId,
                actor.ActiveProfileId,
                actor.ApplicationId,
                subjectType,
                subjectId.ToString("D", CultureInfo.InvariantCulture),
                new Dictionary<string, string?> { ["changed"] = "true" }),
            ct).ConfigureAwait(false);
        return new UserExportDownload(
            $"{filePrefix}-{clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip",
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

    /// <summary>
    /// The preview: who is in the file, what is already known about each sign-in here, and the choices the administrator has. The file's
    /// household name is only a suggestion for a new household.
    /// </summary>
    private static UserImportPreviewDto Preview(Guid operationId, ValidatedUserExport checkedFile, UserImportInspection inspection)
    {
        var summary = checkedFile.Summary;
        var status = inspection.SignIns.ToDictionary(item => item.SignInId);
        var profiles = summary.Profiles
            .Select(profile => new UserImportProfileDto(
                profile.Name,
                profile.Role,
                profile.ContentLimit,
                profile.AllowsUnrated,
                profile.Id,
                profile.SignInId,
                profile.Access))
            .ToList();
        var signIns = summary.SignIns
            .Select(signIn => new UserImportSignInDto(
                signIn.Id,
                signIn.Email,
                signIn.ProfileId,
                status[signIn.Id].EmailInUse,
                status[signIn.Id].Problem == UserImportRefusal.None ? null : ProblemSentenceFor(status[signIn.Id].Problem)))
            .ToList();
        return new UserImportPreviewDto(
            operationId,
            summary.Scope,
            summary.HouseholdName,
            profiles,
            signIns,
            inspection.Households.Select(household => new UserImportHouseholdDto(household.Id, household.Name, household.Profiles)).ToList(),
            inspection.CanAddHousehold,
            inspection.CanAddHousehold ? null : AddHouseholdBlockedMessage,
            MediaEngine.Domain.Entities.Household.MaximumProfiles,
            summary.PersonalItems,
            null,
            checkedFile.TwoStepLeftOut);
    }
}
