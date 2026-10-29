using System.Text.Json;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Settings;

/// <summary>How a new folder is used: read-only (nothing changes) or managed by Library.</summary>
public enum AddFolderHandling
{
    ReadOnly,
    Managed,
}

/// <summary>
/// Everything needed to add one folder: the library type, the validated folder, how it is handled and whether new media should go
/// to this folder (only an empty, managed folder asks). What the library may look up is decided by its kind, not asked here.
/// A kind that allows several named libraries (the plain kinds) also says which one: <paramref name="LibraryId"/> is an existing library
/// of the kind, <paramref name="NewLibraryName"/> makes a new library with that name; with neither, the kind's first library is used
/// (or a new one named for the kind when it has none yet). <paramref name="CreateNewLibrary"/> is the Create library dialog: it always makes a
/// new library, named <paramref name="NewLibraryName"/> whatever the kind, with the given <paramref name="Access"/> (null keeps the standard one);
/// <paramref name="SuccessMessage"/> replaces the "Folder added." toast.
/// </summary>
public sealed record AddFolderPlan(
    string TypeKey,
    ServerFolderValidationResultDto Folder,
    AddFolderHandling Handling,
    bool MarkNewMediaHere,
    string? LibraryId = null,
    string? NewLibraryName = null,
    bool CreateNewLibrary = false,
    string? Access = null,
    string? SuccessMessage = null);

/// <summary>Which Engine calls to use: the signed-in Settings ones, or the first-run setup ones (which carry the setup session).</summary>
public sealed record FolderAddScope(bool Embedded, string? SetupSession)
{
    public static FolderAddScope Settings { get; } = new(false, null);
}

/// <summary>
/// The outcome of adding a folder. <paramref name="Configuration"/> is the saved configuration (null when the save failed, with
/// <paramref name="Error"/> saying why); <paramref name="Notice"/> is a plain sentence to keep on screen, for example that the
/// folder fell back to read-only.
/// </summary>
public sealed record AddFolderResult(LibrariesConfigurationDto? Configuration, LibrarySourceDto? Source, LibraryTypeOption? Type, bool Managed, string? Notice, string? Error, string? LibraryId = null)
{
    public bool Saved => Configuration is not null;
}

/// <summary>What happened after a follow-up question (organise, merge): whether a job was queued and any notice to show.</summary>
public sealed record FolderFollowUp(bool Started, string? Notice);

/// <summary>
/// The one "add a folder" operation shared by the Create library dialog, a library's page and the folders panel: re-check the folder
/// for the chosen handling, save it into the type's library, move the "New media goes here" marker when asked, and run the Convert
/// and Merge follow-ups. It holds no state of its own; the caller owns the configuration it passes in.
/// </summary>
public sealed class LibraryFolderAddService(IEngineApiClient apiClient, IAppDialogService dialogs, IAppToastService toasts)
{
    /// <summary>An available kind, or an "other-{libraryId}" library, by key; null when there is no such type.</summary>
    public static LibraryTypeOption? ResolveType(LibraryKindCatalog kinds, LibrariesConfigurationDto? configuration, string? key) =>
        kinds.Find(key)
        ?? LibraryRollup.OtherLibraries(kinds, configuration).FirstOrDefault(type => string.Equals(type.Key, key, StringComparison.OrdinalIgnoreCase));

    public Task<ServerFolderValidationResultDto?> ValidateAsync(FolderAddScope scope, string path, string mode, string? sourceId = null)
    {
        var request = new ValidateServerFolderRequest { ManualPath = path, SelectionMode = mode, CurrentSourceId = sourceId };
        return scope.Embedded ? apiClient.ValidateSetupServerFolderAsync(request, scope.SetupSession) : apiClient.ValidateServerFolderAsync(request);
    }

    /// <summary>
    /// Adds the folder. A managed folder is checked for write access first and falls back to read-only with a notice when it
    /// cannot be written. Nothing is asked of the person here; follow-ups are <see cref="OfferOrganiseAsync"/> and
    /// <see cref="MergeIntoAsync"/>, which the caller runs next.
    /// </summary>
    public async Task<AddFolderResult> AddAsync(AddFolderPlan plan, LibraryKindCatalog kinds, LibrariesConfigurationDto current, FolderAddScope scope)
    {
        var type = ResolveType(kinds, current, plan.TypeKey);
        if (type is null)
        {
            return new AddFolderResult(null, null, null, false, null, "That library type does not exist.");
        }

        var selected = plan.Folder;
        var managed = plan.Handling == AddFolderHandling.Managed;
        string? notice = null;
        if (managed)
        {
            var writable = await ValidateAsync(scope, selected.Path, ServerFolderSelectionModes.ManagedLibrary);
            if (writable?.CanSelect == true)
            {
                selected = writable;
            }
            else
            {
                managed = false;
                notice = writable is null
                    ? "Library couldn't check whether it can write to this folder, so it will be used as read-only."
                    : "Library can't write to this folder, so it will be used as read-only.";
            }
        }

        var baseline = Clone(current)!;
        LibraryFolderDto? held;
        if (plan.CreateNewLibrary)
        {
            held = null;
        }
        else if (type.AllowsMultiple && !string.IsNullOrWhiteSpace(plan.LibraryId))
        {
            held = LibraryRollup.LibrariesOf(baseline, type).FirstOrDefault(item => item.Id == plan.LibraryId);
            if (held is null)
            {
                return new AddFolderResult(null, null, type, false, null, "That library no longer exists. Choose another one.");
            }
        }
        else
        {
            held = !type.AllowsMultiple || string.IsNullOrWhiteSpace(plan.NewLibraryName) ? LibraryRollup.LibraryFor(baseline, type) : null;
        }

        var library = Clone(held) ?? NewLibrary(type, type.AllowsMultiple || plan.CreateNewLibrary ? plan.NewLibraryName : null);
        if (plan.CreateNewLibrary && string.Equals(plan.Access, LibraryAccessChoices.Vault, StringComparison.OrdinalIgnoreCase))
        {
            library.IsVault = true;
        }

        ApplyKindLookup(library, type);
        // The Engine makes the first managed folder the library's destination when it saves.
        var source = new LibrarySourceDto
        {
            Id = Guid.NewGuid().ToString("D"),
            Path = selected.Path,
            Notes = Path.GetFileName(selected.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ManagementMode = managed ? "managed_by_tuvima" : "existing_library",
            SourceType = "local_folder",
            IncludeSubdirectories = true,
            AccessMode = managed ? "writable" : "read_only",
        };
        if (string.IsNullOrWhiteSpace(source.Notes))
        {
            source.Notes = selected.Path;
        }

        library.Sources.Add(source);

        var saved = await SaveLibraryAsync(scope, baseline, library);
        if (saved is null)
        {
            return new AddFolderResult(null, source, type, managed, notice, apiClient.LastError ?? "That change could not be saved. Check the folder and try again.", library.Id);
        }

        toasts.Add(plan.SuccessMessage ?? "Folder added.", AppSeverity.Success);
        var result = new AddFolderResult(saved, source, type, managed, notice, null, library.Id);

        // "Should new media go here?" answered Yes: when another folder of the type already holds the marker, move it.
        if (plan.MarkNewMediaHere && managed)
        {
            var savedLibrary = saved.Libraries.FirstOrDefault(item => item.Id == library.Id) ?? LibraryRollup.LibraryFor(saved, type);
            if (savedLibrary is not null && !string.Equals(savedLibrary.PrimaryDestinationSourceId, source.Id, StringComparison.Ordinal))
            {
                var moved = await SaveLibraryAsync(scope, saved, WithDestination(savedLibrary, source.Id));
                if (moved is null)
                {
                    return result with { Notice = apiClient.LastError ?? "That change could not be saved. Check the folder and try again." };
                }

                toasts.Add($"New {type.PluralLabel} will go to {source.DisplayName()}.", AppSeverity.Success);
                result = result with { Configuration = moved };
            }
        }

        return result;
    }

    /// <summary>Saves one library (setup: the whole list with this library replaced; Settings: one library change guarded by the copy the caller read). Null when the Engine refused it.</summary>
    public async Task<LibrariesConfigurationDto?> SaveLibraryAsync(FolderAddScope scope, LibrariesConfigurationDto baseline, LibraryFolderDto library)
    {
        if (scope.Embedded)
        {
            var libraries = baseline.Libraries.Where(item => item.Id != library.Id).ToList();
            libraries.Add(library);
            return await apiClient.UpdateSetupLibrariesAsync(new UpdateLibrariesRequest
            {
                SchemaVersion = baseline.SchemaVersion,
                StorageLocations = baseline.StorageLocations,
                ViewStorage = baseline.ViewStorage,
                Libraries = libraries,
                PersonalLibraryPolicy = baseline.PersonalLibraryPolicy,
                FileHandling = baseline.FileHandling,
            }, scope.SetupSession);
        }

        var existing = baseline.Libraries.FirstOrDefault(item => item.Id == library.Id);
        return await apiClient.MutateLibraryAsync(new LibraryMutationRequest { Library = library, ExpectedLibrary = existing });
    }

    /// <summary>A copy of the library whose "New media goes here" marker sits on <paramref name="sourceId"/>. The marker moves only here, on the person's say.</summary>
    public static LibraryFolderDto WithDestination(LibraryFolderDto library, string sourceId)
    {
        var copy = Clone(library)!;
        copy.PrimaryDestinationSourceId = sourceId;
        return copy;
    }

    // Row menu "Organise files now", and the offer after adding a managed folder that already has files.
    public async Task<FolderFollowUp> OfferOrganiseAsync(FolderAddScope scope, LibrarySourceDto source, bool alreadyManaged, bool quiet)
    {
        if (scope.Embedded)
        {
            return new FolderFollowUp(false, null);
        }

        var quick = await apiClient.PreviewFolderConversionAsync(new FolderConversionPreviewRequest { SourceId = source.Id, Quick = true });
        if (quick is null)
        {
            return new FolderFollowUp(false, quiet ? null : apiClient.LastError ?? "Library could not count the files in this folder.");
        }

        if (quick.TotalFiles == 0)
        {
            if (!quiet)
            {
                toasts.Add("There are no media files in this folder to organise.", AppSeverity.Normal);
            }

            return new FolderFollowUp(false, null);
        }

        return await AskToConvertAsync(source, quick, alreadyManaged) ? await StartConversionAsync(source) : new FolderFollowUp(false, null);
    }

    public async Task<bool> AskToConvertAsync(LibrarySourceDto target, FolderConversionPreviewResponse quick, bool alreadyManaged)
    {
        var parameters = new AppDialogParameters<ConvertFolderDialog>
        {
            { component => component.SourceId, target.Id },
            { component => component.Path, target.Path },
            { component => component.AlreadyManaged, alreadyManaged },
            { component => component.Initial, quick },
        };
        var dialog = await dialogs.ShowAsync<ConvertFolderDialog>(string.Empty, parameters, SheetOptions());
        return await dialog.Result is { Canceled: false, Data: ConvertFolderDialog.Convert };
    }

    // Queues the organising job for a folder, makes it Managed in the same step, and says so.
    public async Task<FolderFollowUp> StartConversionAsync(LibrarySourceDto source)
    {
        var job = await apiClient.ConvertFolderAsync(source.Id);
        if (job is null)
        {
            return new FolderFollowUp(false, apiClient.LastError ?? "Library could not start organising this folder.");
        }

        toasts.Add($"Library is organising {source.DisplayName()}. You can keep using Library meanwhile.", AppSeverity.Success);
        return new FolderFollowUp(true, null);
    }

    // "Merge into": choose the managed folder and Copy or Move, look at a preview, then queue the merge job.
    public async Task<FolderFollowUp> MergeIntoAsync(FolderAddScope scope, LibraryFolderDto library, LibrarySourceDto source)
    {
        if (scope.Embedded)
        {
            return new FolderFollowUp(false, null);
        }

        var targets = LibraryFolderTable.MergeTargets(library, source);
        if (targets.Count == 0)
        {
            return new FolderFollowUp(false, "There is no managed folder to merge into yet. Add a managed folder to this library first.");
        }

        var destination = library.GetPrimaryDestination();
        var options = targets
            .Select(target => new MergeFolderDialog.MergeTargetOption(target.Id, target.DisplayName(), target.Path, ReferenceEquals(target, destination)))
            .ToList();
        var parameters = new AppDialogParameters<MergeFolderDialog>
        {
            { component => component.SourceId, source.Id },
            { component => component.Path, source.Path },
            { component => component.SourceName, source.DisplayName() },
            { component => component.CanMove, IsManaged(source) && source.AccessMode == "writable" },
            { component => component.Targets, options },
        };
        var dialog = await dialogs.ShowAsync<MergeFolderDialog>(string.Empty, parameters, SheetOptions());
        if (await dialog.Result is not { Canceled: false, Data: FolderMergeRequest request })
        {
            return new FolderFollowUp(false, null);
        }

        var job = await apiClient.MergeFolderAsync(request);
        if (job is null)
        {
            return new FolderFollowUp(false, apiClient.LastError ?? "Library could not start merging this folder.");
        }

        var targetName = targets.FirstOrDefault(target => target.Id == request.TargetSourceId)?.DisplayName() ?? "the folder you chose";
        toasts.Add($"Library is merging {source.DisplayName()} into {targetName}. You can keep using Library meanwhile.", AppSeverity.Success);
        return new FolderFollowUp(true, null);
    }

    /// <summary>A library made on its own, with the configuration saved with it.</summary>
    public sealed record CreatedLibrary(LibrariesConfigurationDto Configuration, LibraryFolderDto Library);

    /// <summary>
    /// Makes a library of a kind with no folder yet, through the same save the Create library dialog uses (a new library named for the kind, or by the
    /// person for a kind that holds several). Null when the Engine refused it; <see cref="IEngineApiClient.LastError"/> then says why.
    /// </summary>
    public async Task<CreatedLibrary?> CreateLibraryAsync(FolderAddScope scope, LibraryTypeOption type, string? name, LibrariesConfigurationDto baseline)
    {
        var library = NewLibrary(type, type.AllowsMultiple ? name : null);
        ApplyKindLookup(library, type);
        var saved = await SaveLibraryAsync(scope, baseline, library);
        var held = saved?.Libraries.FirstOrDefault(item => item.Id == library.Id);
        return saved is null || held is null ? null : new CreatedLibrary(saved, held);
    }

    // "Change what this folder is": choose the new kind and library, read the impact, then queue the change. The dialog does the Engine calls so
    // a refusal or a failure stays on screen; the follow-up says whether the host should read the folders again (a job was queued, or a library was made).
    public async Task<FolderFollowUp> ChangeKindAsync(FolderAddScope scope, LibraryKindCatalog kinds, LibrariesConfigurationDto configuration, LibraryFolderDto library, LibrarySourceDto source)
    {
        if (scope.Embedded)
        {
            return new FolderFollowUp(false, null);
        }

        var parameters = new AppDialogParameters<ChangeFolderKindDialog>
        {
            { component => component.Source, source },
            { component => component.Library, library },
            { component => component.Kinds, kinds },
            { component => component.Configuration, configuration },
        };
        var dialog = await dialogs.ShowAsync<ChangeFolderKindDialog>(string.Empty, parameters, SheetOptions());
        if (await dialog.Result is not { Canceled: false, Data: ChangeFolderKindDialog.Outcome outcome })
        {
            return new FolderFollowUp(false, null);
        }

        if (outcome.StartedKindLabel is null)
        {
            // A library was made and left unused; the dialog already said so, so the host only reads the libraries again.
            return new FolderFollowUp(true, null);
        }

        toasts.Add($"Library is changing {source.DisplayName()} to {outcome.StartedKindLabel}. You can keep using Library meanwhile.", AppSeverity.Success);
        return new FolderFollowUp(true, null);
    }

    private static AppDialogOptions SheetOptions() => new()
    {
        MaxWidth = AppMaxWidth.Medium,
        FullWidth = true,
        CloseButton = false,
        NoHeader = true,
        CloseOnEscapeKey = true,
        Sheet = AppDialogSheetMode.PhoneAndTablet,
    };

    private const string YouTubeKindKey = "youtube";

    /// <summary>
    /// What a library may look up follows its kind (Library setup D17, D38): a plain kind (no lookup provider) is local only with the
    /// YouTube lookup off, the YouTube kind is local identity plus the YouTube lookup, and a kind that looks details up keeps the
    /// library's own settings.
    /// </summary>
    private static void ApplyKindLookup(LibraryFolderDto library, LibraryTypeOption type)
    {
        if (string.Equals(type.Key, YouTubeKindKey, StringComparison.Ordinal))
        {
            library.MetadataPolicy = "local_only";
            library.YoutubeLookup = true;
        }
        else if (type.LookupProviders is { Count: 0 })
        {
            library.MetadataPolicy = "local_only";
            library.YoutubeLookup = false;
        }
    }

    private static bool IsManaged(LibrarySourceDto source) => source.ManagementMode == "managed_by_tuvima";

    private static T? Clone<T>(T? value) where T : class => value is null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));

    // A new library is named for its kind, or by the person for a kind that holds several (Other videos: "Lectures").
    private static LibraryFolderDto NewLibrary(LibraryTypeOption option, string? name = null) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = string.IsNullOrWhiteSpace(name) ? option.Label : name.Trim(),
        Category = option.Label,
        Kind = "catalogued",
        KindKey = option.Key,
        Area = option.Area,
        Presentation = "catalogue",
        MetadataPolicy = "enriched",
        RecycleBin = option.MediaTypes[0] == "Videos",
        MediaTypes = [option.MediaTypes[0]],
        AcceptedIntakeModes = ["incoming_folder"],
        OrganizationPolicy = new LibraryOrganizationPolicyDto { Mode = LibraryFileHandlingChoices.PreserveNames, WriteTags = false },
    };
}
