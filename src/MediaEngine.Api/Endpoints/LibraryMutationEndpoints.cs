using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Api.Endpoints;

internal static class LibraryMutationEndpoints
{
    internal static readonly SemaphoreSlim WriteGate = new(1, 1);

    internal static void MapLibraryMutations(this RouteGroupBuilder group)
    {
        group.MapGet("/libraries/view-summary", async (IViewPersonalSpaceRepository spaces, CancellationToken ct) =>
        {
            var result = await spaces.GetInventoryAsync(ct);
            return Results.Ok(new ViewStorageSummaryDto(result.Sources, result.Items));
        }).WithName("GetViewStorageSummary").Produces<ViewStorageSummaryDto>().RequireAdmin();

        group.MapPost("/libraries/mutations", async (LibraryMutationRequest request,
            IConfigurationLoader configuration, ServerFolderBrowserService folders,
            IFileOrganizer organizer, ViewStorageService storage, IProfileRepository profiles,
            IViewProfileRepository policies, CancellationToken ct) =>
        {
            await WriteGate.WaitAsync(ct);
            try
            {
                var current = configuration.LoadLibraries();
                var dto = SettingsContractMapper.ToContract(current);
                var conflict = ApplyEdit(dto, request);
                if (conflict is not null)
                {
                    return ApiErrors.Conflict(conflict);
                }

                var proposed = SettingsContractMapper.ToStorage(new UpdateLibrariesRequest
                {
                    SchemaVersion = dto.SchemaVersion,
                    Libraries = dto.Libraries,
                    StorageLocations = dto.StorageLocations,
                    ViewStorage = dto.ViewStorage,
                    PersonalLibraryPolicy = dto.PersonalLibraryPolicy,
                    FileHandling = dto.FileHandling,
                });
                var error = SettingsEndpoints.ValidateViewStorage(proposed)
                    ?? SettingsEndpoints.ValidateViewRootChange(current, proposed)
                    ?? LibraryTypeRules.ValidateOneLibraryPerType(current, proposed)
                    ?? ImportFolderValidation.Validate(proposed);
                if (error is not null)
                {
                    return ApiErrors.BadRequest(error);
                }

                var errors = JsonConfigValidator.Validate(proposed, "libraries.json");
                if (errors.Count > 0)
                {
                    return ApiErrors.BadRequest(string.Join(" ", errors));
                }

                // Validate all logical conflicts, but probe only paths affected by this edit.
                var changed = request.Library;
                if (changed is not null)
                {
                    if (changed.OrganizationPolicy.Mode == "custom"
                        && organizer.ValidateTemplate(changed.OrganizationPolicy.CustomTemplate ?? "", out var templateError) is null)
                    {
                        return ApiErrors.BadRequest(templateError ?? "Invalid template.");
                    }

                    foreach (var source in changed.Sources.Where(source =>
                        !Same(source, request.ExpectedLibrary?.Sources.FirstOrDefault(x => x.Id == source.Id))))
                    {
                        var result = folders.Validate(new ValidateServerFolderRequest
                        {
                            ManualPath = source.Path,
                            CurrentSourceId = source.Id,
                            SelectionMode = source.ManagementMode == "managed_by_tuvima"
                                ? ServerFolderSelectionModes.ManagedLibrary : ServerFolderSelectionModes.ExistingLibrary,
                        }, proposed);
                        if (!result.CanSelect)
                        {
                            return ApiErrors.BadRequest(result.Issues.First(x => x.Severity == "error").Message);
                        }
                    }
                }
                else
                {
                    var result = folders.Validate(new ValidateServerFolderRequest
                    {
                        StorageLocationId = proposed.ViewStorage.StorageLocationId,
                        RelativePath = proposed.ViewStorage.RelativeRoot,
                        SelectionMode = ServerFolderSelectionModes.PersonalSpaceManaged,
                    }, proposed);
                    if (!result.CanSelect)
                    {
                        return ApiErrors.BadRequest(result.Issues.First(x => x.Severity == "error").Message);
                    }
                }
                configuration.SaveLibraries(proposed);
                if (request.ViewStorage is not null)
                {
                    foreach (var profile in await profiles.GetAllAsync(ct))
                    {
                        if ((await policies.GetPolicyAsync(profile.Id, ct)).ViewEnabled)
                        {
                            await storage.EnsurePersonalSpaceAsync(profile.Id, ct);
                        }
                    }
                }

                return Results.Ok(SettingsContractMapper.ToContract(proposed));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or ServerFolderAccessException)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
            finally { WriteGate.Release(); }
        }).WithName("MutateLibrarySettings").Produces<LibrariesConfigurationDto>().RequireAdmin();
    }

    internal static string? ApplyEdit(LibrariesConfigurationDto dto, LibraryMutationRequest request)
    {
        if ((request.Library is null) == (request.ViewStorage is null))
        {
            throw new ArgumentException("Choose exactly one library or View root to edit.");
        }

        if (request.Library is { } edited)
        {
            var index = dto.Libraries.FindIndex(x => x.Id == edited.Id);
            var existing = index < 0 ? null : dto.Libraries[index];
            if (!Same(existing, request.ExpectedLibrary))
            {
                return "This library changed in another session. Reload before editing again.";
            }

            if (index < 0)
            {
                dto.Libraries.Add(edited);
            }
            else
            {
                dto.Libraries[index] = edited;
            }
        }
        else
        {
            if (!Same(dto.ViewStorage, request.ExpectedViewStorage))
            {
                return "View storage changed in another session. Reload before editing again.";
            }

            dto.ViewStorage.StorageLocationId = request.ViewStorage!.StorageLocationId;
            dto.ViewStorage.RelativeRoot = request.ViewStorage.RelativeRoot;
        }
        return null;
    }

    internal static bool Same<T>(T? left, T? right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
