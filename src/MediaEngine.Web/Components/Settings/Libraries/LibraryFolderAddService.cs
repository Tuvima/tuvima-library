using System.Text.Json;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Settings;

/// <summary>How a new folder is used: read-only (nothing changes), merged into a managed folder once, or managed by Library.</summary>
public enum AddFolderHandling
{
    ReadOnly,
    Merge,
    Managed,
}

/// <summary>
/// Everything needed to add one folder: the library type, the validated folder, how it is handled, the Videos YouTube switch
/// (null leaves the library's setting alone) and whether new media should go to this folder (only an empty, managed folder asks).
/// </summary>
public sealed record AddFolderPlan(string TypeKey, ServerFolderValidationResultDto Folder, AddFolderHandling Handling, bool? YoutubeLookup, bool MarkNewMediaHere);

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
public sealed record AddFolderResult(LibrariesConfigurationDto? Configuration, LibrarySourceDto? Source, LibraryTypeOption? Type, bool Managed, string? Notice, string? Error)
{
    public bool Saved => Configuration is not null;
}

/// <summary>What happened after a follow-up question (organise, merge): whether a job was queued and any notice to show.</summary>
public sealed record FolderFollowUp(bool Started, string? Notice);

/// <summary>
/// The one "add a folder" operation shared by the Add folder wizard and the folders panel: re-check the folder for the chosen
/// handling, save it into the type's library, move the "New media goes here" marker when asked, and run the Convert and Merge
/// follow-ups. It holds no state of its own; the caller owns the configuration it passes in.
/// </summary>
public sealed class LibraryFolderAddService(IEngineApiClient apiClient, IAppDialogService dialogs, IAppToastService toasts)
{
    /// <summary>A standard type, or an "other-{libraryId}" library, by key; null when the configuration has no such type.</summary>
    public static LibraryTypeOption? ResolveType(LibrariesConfigurationDto? configuration, string? key) =>
        LibraryTypeCatalog.Find(key)
        ?? LibraryRollup.OtherLibraries(configuration).FirstOrDefault(type => string.Equals(type.Key, key, StringComparison.OrdinalIgnoreCase));

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
    public async Task<AddFolderResult> AddAsync(AddFolderPlan plan, LibrariesConfigurationDto current, FolderAddScope scope)
    {
        var type = ResolveType(current, plan.TypeKey);
        if (type is null)
        {
            return new AddFolderResult(null, null, null, false, null, "That library type does not exist.");
        }

        var selected = plan.Folder;
        var managed = plan.Handling == AddFolderHandling.Managed;
        var mergeAfter = plan.Handling == AddFolderHandling.Merge;
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
        var library = Clone(LibraryRollup.LibraryFor(baseline, type)) ?? NewLibrary(type);
        if (IsVideos(type) && plan.YoutubeLookup is { } youtubeLookup)
        {
            library.YoutubeLookup = youtubeLookup;
        }
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
            return new AddFolderResult(null, source, type, managed, notice, apiClient.LastError ?? "That change could not be saved. Check the folder and try again.");
        }

        toasts.Add(mergeAfter ? "Folder added as read-only. Choose where its files should go." : "Folder added.", AppSeverity.Success);
        var result = new AddFolderResult(saved, source, type, managed, notice, null);

        // "Should new media go here?" answered Yes: when another folder of the type already holds the marker, move it.
        if (plan.MarkNewMediaHere && managed)
        {
            var savedLibrary = LibraryRollup.LibraryFor(saved, type);
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

    private static AppDialogOptions SheetOptions() => new()
    {
        MaxWidth = AppMaxWidth.Medium,
        FullWidth = true,
        CloseButton = false,
        NoHeader = true,
        CloseOnEscapeKey = true,
        Sheet = AppDialogSheetMode.PhoneAndTablet,
    };

    private static bool IsVideos(LibraryTypeOption type) => type.MediaTypes.Contains("Videos", StringComparer.OrdinalIgnoreCase);

    private static bool IsManaged(LibrarySourceDto source) => source.ManagementMode == "managed_by_tuvima";

    private static T? Clone<T>(T? value) where T : class => value is null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));

    private static LibraryFolderDto NewLibrary(LibraryTypeOption option) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = option.Label,
        Category = option.Label,
        Kind = "catalogued",
        Area = option.Area,
        Presentation = "catalogue",
        MetadataPolicy = option.MediaTypes[0] == "Videos" ? "local_only" : "enriched",
        RecycleBin = option.MediaTypes[0] == "Videos",
        MediaTypes = [option.MediaTypes[0]],
        Visibility = "household",
        AcceptedIntakeModes = ["incoming_folder"],
        OrganizationPolicy = new LibraryOrganizationPolicyDto { Mode = "tuvima_standard", WriteTags = true },
    };
}
