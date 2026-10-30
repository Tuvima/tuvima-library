using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Api.Services.Vault;

/// <summary>How a request about a Vault library ended. Every message is a plain sentence that never names a folder, a path or a title.</summary>
public enum VaultSiblingStatus
{
    Ok,
    NotFound,
    Invalid,
    Conflict,
}

/// <summary>The result of a Vault library action; <see cref="Library"/> is set when it succeeded.</summary>
public sealed record VaultSiblingOutcome(VaultSiblingStatus Status, string? Message = null, LibraryFolderConfig? Library = null);

/// <summary>
/// The Vault library that sits beside the ordinary library of a content type (Library pages D10, D11; Vault sections D26a, D34): "Private
/// Movies" beside "Movies". A Vault library is stored as an entry in <c>libraries.json</c> with <c>access</c> set to <c>vault</c> and a row in
/// the Vault's own <c>vault_libraries</c> table, which stays the one place that says "this is the Vault". It is never an account grant, never a
/// library grant and never listed in a profile picker. Creating one, or adding a folder to one, needs only a signed-in server administrator
/// (the caller checks that; no PIN, D10). Each content type has at most one Vault library. Its folders are added as-is (read-only): a folder
/// Library manages and a move into it belong to the Vault sections work (WP2, WP3). Nothing here logs or returns a path, a folder name or a title.
/// </summary>
public sealed class VaultSiblingLibraryService(
    IVaultLibraryRepository vaultLibraries,
    IConfigurationLoader configuration,
    VaultFolderAttachmentService attachment,
    LibraryKindListService kinds,
    TimeProvider clock,
    ILogger<VaultSiblingLibraryService> logger)
{
    public const int MaxNameLength = 80;
    public const string KindMissingMessage = "Choose a content type for the Vault library.";
    public const string KindNotOfferedMessage = "That content type can't have a Vault library.";
    public const string NameMissingMessage = "Give the library a name.";
    public const string NameTooLongMessage = "Use 80 characters or fewer for the name.";
    public const string NameTakenMessage = "You already have a library with that name here. Choose a different name.";
    public const string AlreadyExistsMessage = "There is already a Vault library for this content type. Add the folder to it instead.";
    public const string LookupNotOfferedMessage = "This library has no online lookup to switch.";

    /// <summary>The content type that is only ever offered with the Vault tick (Library pages D12, D21).</summary>
    public const string AdultContentKindKey = "adult_content";

    /// <summary>The content types whose Vault libraries have an online lookup switch of their own: those that look details up at all, apart from the adult kind (its own source is Stash-box) and YouTube (its Lookup tab holds the optional personal key instead).</summary>
    public static bool OffersOnlineLookup(LibraryKindEntry kind) =>
        kind.LookupProviders.Count > 0
        && !string.Equals(kind.Kind.Key, AdultContentKindKey, StringComparison.Ordinal)
        && !string.Equals(kind.Kind.Key, "youtube", StringComparison.Ordinal);

    /// <summary>The name a Vault library starts with when it is made for a folder added as a Vault folder.</summary>
    public static string DefaultName(string kindLabel) => $"Private {kindLabel}";

    /// <summary>Makes the Vault library of a content type with its first folder. Refused when the type already has one.</summary>
    public async Task<VaultSiblingOutcome> CreateAsync(
        Guid adminProfileId, string? kindKey, string? name, string? folderPath, CancellationToken ct, bool managed = false)
    {
        if (ResolveKind(kindKey) is not { } kind)
        {
            return new VaultSiblingOutcome(VaultSiblingStatus.Invalid, string.IsNullOrWhiteSpace(kindKey) ? KindMissingMessage : KindNotOfferedMessage);
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return new VaultSiblingOutcome(VaultSiblingStatus.Invalid, NameMissingMessage);
        }

        if (trimmed.Length > MaxNameLength)
        {
            return new VaultSiblingOutcome(VaultSiblingStatus.Invalid, NameTooLongMessage);
        }

        var vaultIds = await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        var existing = VaultEntries(configuration.LoadLibraries(), vaultIds).ToList();
        if (existing.Any(entry => KindOf(entry, kind.Kind.Key)))
        {
            return new VaultSiblingOutcome(VaultSiblingStatus.Conflict, AlreadyExistsMessage);
        }

        if (existing.Any(entry => string.Equals(entry.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return new VaultSiblingOutcome(VaultSiblingStatus.Conflict, NameTakenMessage);
        }

        var now = clock.GetUtcNow();
        var entryId = Guid.NewGuid().ToString("D");
        // The Vault list comes first: by the time libraries.json is saved and the first scan starts, every file is registered as Vault.
        await vaultLibraries.AddAsync(
            new VaultLibrary(entryId, VaultLookupSources.None, null, now, adminProfileId, now, now), ct).ConfigureAwait(false);

        VaultFolderAttachmentOutcome outcome;
        try
        {
            outcome = await attachment.AttachAsAdministratorAsync(
                entryId, folderPath, NewEntry(entryId, trimmed, kind.Kind),
                settings => TakenInside(settings, kind.Kind.Key, trimmed), managed, ct).ConfigureAwait(false);
        }
        catch
        {
            await DropRowAsync(entryId).ConfigureAwait(false);
            throw;
        }

        if (outcome.Status != VaultFolderAttachmentStatus.Attached)
        {
            await DropRowAsync(entryId).ConfigureAwait(false);
            return Refused(outcome);
        }

        return new VaultSiblingOutcome(VaultSiblingStatus.Ok, Library: FindEntry(entryId));
    }

    /// <summary>
    /// Adds a folder as a Vault folder of a content type: to the type's Vault library, or, when it has none yet, to a new one named for the
    /// type. This is what "Add as a Vault folder" on Add folder does.
    /// </summary>
    public async Task<VaultSiblingOutcome> AddFolderAsync(Guid adminProfileId, string? kindKey, string? folderPath, CancellationToken ct, bool managed = false)
    {
        if (ResolveKind(kindKey) is not { } kind)
        {
            return new VaultSiblingOutcome(VaultSiblingStatus.Invalid, string.IsNullOrWhiteSpace(kindKey) ? KindMissingMessage : KindNotOfferedMessage);
        }

        var vaultIds = await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        var held = VaultEntries(configuration.LoadLibraries(), vaultIds).FirstOrDefault(entry => KindOf(entry, kind.Kind.Key));
        if (held is null)
        {
            return await CreateAsync(adminProfileId, kind.Kind.Key, DefaultName(kind.Kind.Label), folderPath, ct, managed).ConfigureAwait(false);
        }

        var outcome = await attachment.AttachAsAdministratorAsync(held.Id, folderPath, null, null, managed, ct).ConfigureAwait(false);
        return outcome.Status == VaultFolderAttachmentStatus.Attached
            ? new VaultSiblingOutcome(VaultSiblingStatus.Ok, Library: FindEntry(held.Id))
            : Refused(outcome);
    }

    /// <summary>The Vault libraries of the content types, for a caller who may see the Vault (the caller checks). A Vault entry made before content types existed is not listed.</summary>
    public async Task<IReadOnlyList<LibraryFolderConfig>> ListAsync(CancellationToken ct)
    {
        var vaultIds = await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        return [.. VaultEntries(configuration.LoadLibraries(), vaultIds)];
    }

    /// <summary>The libraries as the Dashboard reads them.</summary>
    public IReadOnlyList<LibraryFolderDto> ToContract(IReadOnlyList<LibraryFolderConfig> libraries) =>
        SettingsContractMapper.ToContract(new LibrariesConfiguration { Libraries = [.. libraries] }, configuration).Libraries;

    /// <summary>Turns "Look up details online" on or off for one Vault library of an ordinary content type. Off by default.</summary>
    public async Task<VaultSiblingOutcome> SetOnlineLookupAsync(string libraryId, bool enabled, CancellationToken ct)
    {
        var vaultIds = await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        await LibraryMutationEndpoints.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = configuration.LoadLibraries();
            var entry = VaultEntries(settings, vaultIds).FirstOrDefault(candidate => string.Equals(candidate.Id, libraryId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return new VaultSiblingOutcome(VaultSiblingStatus.NotFound, "Not found.");
            }

            var kind = kinds.GetAll().FirstOrDefault(candidate => string.Equals(candidate.Kind.Key, entry.KindKey, StringComparison.Ordinal));
            if (kind is null || !OffersOnlineLookup(kind))
            {
                return new VaultSiblingOutcome(VaultSiblingStatus.Invalid, LookupNotOfferedMessage);
            }

            entry.MetadataPolicy = enabled ? LibraryMetadataPolicies.Enriched : LibraryMetadataPolicies.LocalOnly;
            if (JsonConfigValidator.Validate(settings, "libraries.json").Count > 0)
            {
                return new VaultSiblingOutcome(VaultSiblingStatus.Invalid, LookupNotOfferedMessage);
            }

            configuration.SaveLibraries(settings);
            return new VaultSiblingOutcome(VaultSiblingStatus.Ok, Library: entry);
        }
        finally
        {
            LibraryMutationEndpoints.WriteGate.Release();
        }
    }

    /// <summary>What "Move to Vault" would find for a content type (Library pages D11). Reads only; nothing is created or moved.</summary>
    public async Task<VaultMoveDestinationDto> DestinationAsync(string kindKey, CancellationToken ct)
    {
        var vaultIds = await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        var entry = VaultEntries(configuration.LoadLibraries(), vaultIds).FirstOrDefault(candidate => KindOf(candidate, kindKey));
        if (entry is null || !Guid.TryParse(entry.Id, out var id))
        {
            return new VaultMoveDestinationDto(kindKey, VaultMoveDestinationStates.None, null, null, 0);
        }

        var managed = entry.CataloguedSources.Count(source => source.AllowsFileMutation);
        return new VaultMoveDestinationDto(
            kindKey, managed > 0 ? VaultMoveDestinationStates.Ready : VaultMoveDestinationStates.NeedsManagedFolder, id, entry.Name, managed);
    }

    private LibraryKindEntry? ResolveKind(string? kindKey)
    {
        if (string.IsNullOrWhiteSpace(kindKey))
        {
            return null;
        }

        var kind = kinds.GetAll().FirstOrDefault(candidate => string.Equals(candidate.Kind.Key, kindKey.Trim(), StringComparison.Ordinal));
        // The View kinds keep their own rules (Library pages D1), and a kind with no format has no files of its own to protect.
        return kind is { Kind: { Available: true, Format: not null } } && kind.Kind.Area is LibraryAreas.Watch or LibraryAreas.Listen or LibraryAreas.Read
            ? kind
            : null;
    }

    private static IEnumerable<LibraryFolderConfig> VaultEntries(LibrariesConfiguration settings, IReadOnlySet<string> vaultIds) =>
        settings.Libraries.Where(entry => vaultIds.Contains(entry.Id) && !string.IsNullOrEmpty(entry.KindKey));

    private static bool KindOf(LibraryFolderConfig entry, string kindKey) =>
        string.Equals(entry.KindKey, kindKey, StringComparison.Ordinal);

    // Checked again under the settings write gate, so two requests at once cannot make two Vault libraries of one type.
    private static string? TakenInside(LibrariesConfiguration settings, string kindKey, string name)
    {
        var vaultEntries = settings.Libraries.Where(entry => entry.IsVault).ToList();
        if (vaultEntries.Any(entry => KindOf(entry, kindKey)))
        {
            return AlreadyExistsMessage;
        }

        return vaultEntries.Any(entry => string.Equals(entry.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) ? NameTakenMessage : null;
    }

    private LibraryFolderConfig? FindEntry(string libraryId) =>
        configuration.LoadLibraries().Libraries.FirstOrDefault(entry => string.Equals(entry.Id, libraryId, StringComparison.OrdinalIgnoreCase));

    private async Task DropRowAsync(string libraryId)
    {
        try
        {
            await vaultLibraries.RemoveAsync(libraryId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Items already sit in the library, so the Vault mark must stay (it is refused to protect them); the half-made library is left for the Vault pages.
            logger.LogDebug("A half-made library was left in place because it already holds items.");
        }
    }

    private static VaultSiblingOutcome Refused(VaultFolderAttachmentOutcome outcome) => outcome.Status switch
    {
        VaultFolderAttachmentStatus.FolderNotFound => new VaultSiblingOutcome(VaultSiblingStatus.Invalid, outcome.Message ?? VaultFolderAttachmentService.FolderNotFoundMessage),
        VaultFolderAttachmentStatus.NotAllowed => new VaultSiblingOutcome(
            outcome.Message is AlreadyExistsMessage or NameTakenMessage ? VaultSiblingStatus.Conflict : VaultSiblingStatus.Invalid,
            outcome.Message ?? VaultFolderAttachmentService.FolderNotAllowedMessage),
        _ => new VaultSiblingOutcome(VaultSiblingStatus.NotFound, "Not found."),
    };

    private static LibraryFolderConfig NewEntry(string libraryId, string name, LibraryKindDefinition kind) => new()
    {
        Id = libraryId,
        Name = name,
        Category = kind.Label,
        Kind = LibraryKinds.Catalogued,
        KindKey = kind.Key,
        Area = kind.Area,
        Presentation = LibraryPresentations.Catalogue,
        // Nothing is looked up online until an administrator switches that on for this library (Vault sections D34).
        MetadataPolicy = LibraryMetadataPolicies.LocalOnly,
        MediaTypes = [kind.Format!],
        YoutubeLookup = false,
        RecycleBin = string.Equals(kind.Format, nameof(MediaEngine.Domain.Enums.MediaType.Videos), StringComparison.Ordinal),
        IsVault = true,
        AcceptedIntakeModes = [LibraryIntakeModes.IncomingFolder],
        // A Vault library's files follow the Vault's own rules: names are kept and tags are never written.
        OrganizationPolicy = new LibraryOrganizationPolicyConfig { Mode = LibraryOrganizationModes.KeepOriginalNames, WriteTags = false },
    };
}
