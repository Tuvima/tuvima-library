using System.Text.Json;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using ContractLibraryPreferences = MediaEngine.Contracts.Settings.LibraryPreferencesSettings;
using ContractPipelineConfiguration = MediaEngine.Contracts.Settings.PipelineConfiguration;
using ContractTranscodingSettings = MediaEngine.Contracts.Settings.TranscodingSettings;
using StorageLibraryPreferences = MediaEngine.Domain.Configuration.LibraryPreferencesSettings;
using StoragePipelineConfiguration = MediaEngine.Domain.Configuration.PipelineConfiguration;
using StorageTranscodingSettings = MediaEngine.Domain.Configuration.TranscodingSettings;

namespace MediaEngine.Api.Endpoints;

internal static class SettingsContractMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static MediaTypeConfigurationDto ToContract(MediaTypeConfiguration value) =>
        Convert<MediaTypeConfigurationDto>(value);

    internal static MediaTypeConfiguration ToStorage(MediaTypeConfigurationDto value) =>
        Convert<MediaTypeConfiguration>(value);

    internal static MediaTypeDefinition ToStorage(MediaTypeDefinitionDto value) =>
        Convert<MediaTypeDefinition>(value);

    internal static HydrationSettingsDto ToContract(HydrationSettings value) =>
        Convert<HydrationSettingsDto>(value);

    internal static HydrationSettings ToStorage(HydrationSettingsDto value) =>
        Convert<HydrationSettings>(value);

    internal static ContractTranscodingSettings ToContract(StorageTranscodingSettings value) =>
        Convert<ContractTranscodingSettings>(value);

    internal static StorageTranscodingSettings ToStorage(ContractTranscodingSettings value) =>
        Convert<StorageTranscodingSettings>(value);

    internal static ContractPipelineConfiguration ToContract(StoragePipelineConfiguration value) =>
        Convert<ContractPipelineConfiguration>(value);

    internal static StoragePipelineConfiguration ToStorage(ContractPipelineConfiguration value) =>
        Convert<StoragePipelineConfiguration>(value);

    internal static ContractLibraryPreferences ToContract(StorageLibraryPreferences value) =>
        Convert<ContractLibraryPreferences>(value);

    internal static StorageLibraryPreferences ToStorage(ContractLibraryPreferences value) =>
        Convert<StorageLibraryPreferences>(value);

    internal static UIGlobalSettingsDto ToContract(UIGlobalSettings value) =>
        Convert<UIGlobalSettingsDto>(value);

    internal static UIGlobalSettings ToStorage(UIGlobalSettingsDto value) =>
        Convert<UIGlobalSettings>(value);

    internal static UIDeviceProfileDto ToContract(UIDeviceProfile value) =>
        Convert<UIDeviceProfileDto>(value);

    internal static UIDeviceProfile ToStorage(UIDeviceProfileDto value) =>
        Convert<UIDeviceProfile>(value);

    internal static UIProfileSettingsDto ToContract(UIProfileSettings value) =>
        Convert<UIProfileSettingsDto>(value);

    internal static UIProfileSettings ToStorage(UIProfileSettingsDto value) =>
        Convert<UIProfileSettings>(value);

    internal static ResolvedUISettingsDto ToContract(ResolvedUISettings value) =>
        Convert<ResolvedUISettingsDto>(value);

    /// <param name="value">The libraries configuration.</param>
    /// <param name="kinds">Where the import folder's media types are read from (the kinds list); without it the kinds that ship are used.</param>
    internal static LibrariesConfigurationDto ToContract(LibrariesConfiguration value, IConfigurationLoader? kinds = null)
    {
        var result = Convert<LibrariesConfigurationDto>(value);
        var status = ImportFolderRules.Evaluate(value, kinds is null ? null : ImportFolderRules.TypesFrom(kinds.LoadLibraryKinds().Kinds));
        result.ImportFolderStatus = new ImportFolderStatusDto
        {
            Configured = status.Configured,
            Path = status.Path,
            Ready = status.Ready,
            MissingTypes = [.. status.MissingTypes],
            Destinations = [.. status.Destinations.Select(destination => new ImportFolderDestinationDto
            {
                MediaType = destination.MediaType,
                LibraryId = destination.LibraryId,
                DestinationSourceId = destination.SourceId,
                Path = destination.Path,
            })],
        };
        return result;
    }

    internal static LibrariesConfiguration ToStorage(UpdateLibrariesRequest value)
    {
        // The Unmatched count is read-only: a saved list may carry the count it was read with, which is never stored. It is zeroed (and so
        // left out of the JSON) before the conversion, or the storage model would see it as a setting it does not know.
        foreach (var library in value.Libraries)
        {
            library.UnmatchedCount = 0;
            library.ReviewCount = 0;
            ClearFirstChecks(library);
        }

        var result = Convert<LibrariesConfiguration>(value);
        // Probe details belong to the response, never to persisted configuration.
        result.StorageLocations = value.StorageLocations.Select(location => new ServerStorageLocationConfig
        {
            Id = location.Id,
            Label = location.Label,
            Path = location.Path,
            AllowWrite = location.AllowWrite,
        }).ToList();
        result.Libraries ??= [];
        result.ViewStorage ??= new ViewStorageConfig();
        result.PersonalLibraryPolicy ??= new PersonalLibraryPolicyConfig();
        result.FileHandling ??= new FileHandlingConfig();
        foreach (var library in result.Libraries)
        {
            library.MediaTypes ??= [];
            library.Sources ??= [];
            library.AcceptedIntakeModes ??= [];
            library.OrganizationPolicy ??= new LibraryOrganizationPolicyConfig();
            library.FileHandling ??= new LibraryFileHandlingConfig();
        }

        // Access follows management mode and each library gets its destination, whoever sent the change.
        return LibrariesConfigurationNormalizer.Normalize(result);
    }

    /// <summary>The first check shown on a source is read-only, like the Unmatched count: it is never stored, and never counts as an edit.</summary>
    internal static void ClearFirstChecks(LibraryFolderDto library)
    {
        // A saved list may carry null where a list belongs; the normalising step after the conversion supplies the empty list.
        foreach (var source in library.Sources ?? [])
        {
            source.FirstCheck = null;
        }
    }

    private static TTarget Convert<TTarget>(object source) =>
        JsonSerializer.Deserialize<TTarget>(
            JsonSerializer.Serialize(source, JsonOptions),
            JsonOptions)
        ?? throw new InvalidOperationException(
            $"Could not map settings value to {typeof(TTarget).FullName}.");
}
