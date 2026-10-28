using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Api.Services.Vault;

/// <summary>How adding a folder to a Vault library ended.</summary>
public enum VaultFolderAttachmentStatus
{
    /// <summary>The folder is part of the Vault library and is being scanned as Vault.</summary>
    Attached,

    /// <summary>There is no such Vault library, so the caller answers "not found".</summary>
    LibraryNotFound,

    /// <summary>The folder is not on the server (or cannot be reached), so nothing was added.</summary>
    FolderNotFound,

    /// <summary>The folder cannot be used (outside the approved storage, inside another library's folder, and so on), so nothing was added.</summary>
    NotAllowed,
}

/// <summary>The result of <see cref="VaultFolderAttachmentService.AttachAsync"/>. <see cref="Message"/> is a plain sentence that never names a path.</summary>
public sealed record VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus Status, string? Message = null, VaultLibrary? Library = null);

/// <summary>
/// Puts a folder into a Vault library (Videos spec D45, D17, D33). A Vault library is an ordinary entry in <c>libraries.json</c>
/// (that is where its folders live, and what the ingestion pipeline and <c>VaultPathClassifier</c> read) whose id is also in the
/// <c>vault_libraries</c> list, which stays the one place that says "this is the Vault". The folder is added as-is (read-only: the
/// original files are never moved, renamed or tagged). Saving <c>libraries.json</c> is what starts the watcher and the first scan,
/// and by then the library is already in the Vault list, so every file is registered as Vault from the start.
/// Like every other action that opens the Vault up it asks for the administrator's own profile PIN at this moment.
/// Nothing here logs a path or a PIN.
/// </summary>
public sealed class VaultFolderAttachmentService(
    VaultAdministrationService administration,
    IVaultLibraryRepository libraries,
    IConfigurationLoader configuration,
    ServerFolderBrowserService folders,
    ILogger<VaultFolderAttachmentService> logger)
{
    public const string FolderNotFoundMessage = "That folder wasn't found on the server, so nothing was added.";
    public const string FolderNotAllowedMessage = "That folder can't be used for the Vault, so nothing was added.";
    public const string FolderHoldsOrdinaryFilesMessage =
        "That folder already holds files that are in your normal libraries, so nothing was added. Put the Vault videos in a folder of their own first.";
    public const string FolderHoldsLinksMessage =
        "That folder holds a shortcut or link that points somewhere else, so nothing was added. Remove the link, or choose a folder with only real files in it.";
    public const string FolderManagedNeedsWriteMessage =
        "Library can't write to that folder, so it can't be a managed Vault folder. Choose a folder Library may change, or add it as read-only.";

    /// <summary>Name of the settings entry that holds the Vault's folders. Never shown in a library list.</summary>
    public const string LibraryName = "Vault";

    public async Task<VaultFolderAttachmentOutcome> AttachAsync(
        Guid adminProfileId, string libraryId, string? folderPath, string? adminPin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || await libraries.GetAsync(libraryId, ct).ConfigureAwait(false) is null)
        {
            return new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.LibraryNotFound);
        }

        // The PIN comes before anything is learned about the folder, so a wrong PIN cannot be used to probe the server's disks.
        await administration.RequireAdminPinAsync(adminProfileId, adminPin, ct).ConfigureAwait(false);
        return await AttachCoreAsync(libraryId, folderPath, newEntry: null, preCheck: null, managed: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a folder to a Vault library for a signed-in server administrator, with no PIN (Library pages D10: creating a Vault library or
    /// folder needs only the administrator; a PIN guards viewing and the Read-only override). The caller has already checked that the request
    /// is an administrator's. The same folder rules apply as for <see cref="AttachAsync"/>.
    /// </summary>
    internal Task<VaultFolderAttachmentOutcome> AttachAsAdministratorAsync(
        string libraryId, string? folderPath, LibraryFolderConfig? newEntry, Func<LibrariesConfiguration, string?>? preCheck, bool managed, CancellationToken ct) =>
        AttachCoreAsync(libraryId, folderPath, newEntry, preCheck, managed, ct);

    private async Task<VaultFolderAttachmentOutcome> AttachCoreAsync(
        string libraryId, string? folderPath, LibraryFolderConfig? newEntry, Func<LibrariesConfiguration, string?>? preCheck, bool managed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || await libraries.GetAsync(libraryId, ct).ConfigureAwait(false) is not { } vaultLibrary)
        {
            return new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.LibraryNotFound);
        }

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.FolderNotFound, FolderNotFoundMessage);
        }

        await LibraryMutationEndpoints.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = configuration.LoadLibraries();
            if (preCheck?.Invoke(settings) is { } refusal)
            {
                return new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.NotAllowed, refusal);
            }

            var check = CheckFolder(folderPath, settings, managed);
            if (check.Outcome is not null)
            {
                return check.Outcome;
            }

            // Vault and ordinary files are kept in separate folders: adding a folder that already holds ordinary files would turn them into Vault files.
            if (await libraries.HoldsOrdinaryFilesAsync(check.Path!, ct).ConfigureAwait(false))
            {
                return new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.NotAllowed, FolderHoldsOrdinaryFilesMessage);
            }

            // A link inside the folder could point into a normal library and pull its files into the Vault (or the other way round).
            if (VaultFolderLinkCheck.HoldsLink(check.Path!))
            {
                return new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.NotAllowed, FolderHoldsLinksMessage);
            }

            var library = settings.Libraries.FirstOrDefault(entry => string.Equals(entry.Id, libraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null)
            {
                library = newEntry ?? NewVaultEntry(libraryId);
                settings.Libraries.Add(library);
            }

            library.Sources.Add(new LibrarySourceConfig
            {
                Id = Guid.NewGuid().ToString("D"),
                Path = check.Path!,
                // As-is unless the administrator chose a Managed Vault folder, the only kind Move to Vault may write to (spec D33, D38).
                ManagementMode = managed ? LibrarySourceManagementModes.ManagedByTuvima : LibrarySourceManagementModes.ExistingLibrary,
                AccessMode = managed ? LibrarySourceAccessModes.Writable : LibrarySourceAccessModes.ReadOnly,
                SourceType = LibrarySourceTypes.LocalFolder,
                IncludeSubdirectories = true,
            });

            if (JsonConfigValidator.Validate(settings, "libraries.json").Count > 0)
            {
                return new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.NotAllowed, FolderNotAllowedMessage);
            }

            // The logging and activity writers must know the folder before the first scan names a file in it.
            VaultLocationGuard.Add(check.Path!);

            // Saving starts the watcher and the first scan; the library is already in the Vault list.
            configuration.SaveLibraries(settings);
        }
        finally
        {
            LibraryMutationEndpoints.WriteGate.Release();
        }

        logger.LogInformation("A folder was added to the Vault.");
        return new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.Attached, Library: vaultLibrary);
    }

    private (string? Path, VaultFolderAttachmentOutcome? Outcome) CheckFolder(string folderPath, LibrariesConfiguration settings, bool managed)
    {
        ServerFolderValidationResultDto result;
        try
        {
            result = folders.Validate(
                new ValidateServerFolderRequest
                {
                    ManualPath = folderPath.Trim(),
                    SelectionMode = managed ? ServerFolderSelectionModes.ManagedLibrary : ServerFolderSelectionModes.ExistingLibrary,
                },
                settings);
        }
        catch (ServerFolderAccessException)
        {
            return (null, new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.NotAllowed, FolderNotAllowedMessage));
        }

        if (result.Issues.Any(issue => issue.Code == "not_found") || (result.CanSelect && !result.Exists))
        {
            return (null, new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.FolderNotFound, FolderNotFoundMessage));
        }

        if (managed && result.Issues.Any(issue => issue.Code is "storage_location_read_only" or "write_required"))
        {
            return (null, new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.NotAllowed, FolderManagedNeedsWriteMessage));
        }

        return result.CanSelect && !string.IsNullOrWhiteSpace(result.Path)
            ? (result.Path, null)
            : (null, new VaultFolderAttachmentOutcome(VaultFolderAttachmentStatus.NotAllowed, FolderNotAllowedMessage));
    }

    private static LibraryFolderConfig NewVaultEntry(string libraryId) => new()
    {
        Id = libraryId,
        Name = LibraryName,
        Category = "Videos",
        Kind = LibraryKinds.Catalogued,
        Area = LibraryAreas.Watch,
        Presentation = LibraryPresentations.Catalogue,
        // Vault lookups come from the Vault's own source (spec D26); nothing else is asked about these files.
        MetadataPolicy = LibraryMetadataPolicies.LocalOnly,
        MediaTypes = ["Videos"],
        YoutubeLookup = false,
        // Deleting from disk moves to the recycle bin, as for every Videos library (spec D14).
        RecycleBin = true,
        AcceptedIntakeModes = [LibraryIntakeModes.IncomingFolder],
    };
}
