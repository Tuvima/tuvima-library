using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Settings;

/// <summary>
/// Validates the configured catalogued-library sources through the same approved-root
/// boundary used by the interactive server folder picker.
/// </summary>
public sealed class SetupMediaLocationValidationService(
    IConfigurationLoader configuration,
    ServerFolderBrowserService folderBrowser)
{
    public SetupMediaLocationsDto Validate()
    {
        var libraries = configuration.LoadLibraries().Libraries
            .Where(library => library.Kind == LibraryKinds.Catalogued)
            .ToList();
        var sources = libraries.SelectMany(library => library.Sources).ToList();
        if (sources.Count == 0)
        {
            return new SetupMediaLocationsDto(
                false,
                0,
                0,
                "Add at least one readable media location before completing setup.");
        }

        var readable = 0;
        foreach (var library in libraries)
        {
            foreach (var source in library.Sources)
            {
                ServerFolderValidationResultDto validation;
                try
                {
                    validation = folderBrowser.Validate(new ValidateServerFolderRequest
                    {
                        ManualPath = source.Path,
                        CurrentSourceId = source.Id,
                        SelectionMode = source.ManagementMode == LibrarySourceManagementModes.ManagedByTuvima
                            ? ServerFolderSelectionModes.ManagedLibrary
                            : ServerFolderSelectionModes.ExistingLibrary,
                    });
                }
                catch (ServerFolderAccessException exception)
                {
                    return Failure(sources.Count, readable, source.Path, exception.Message);
                }

                if (validation.HasRead)
                {
                    readable++;
                }

                if (!validation.CanSelect)
                {
                    var reason = validation.Issues.FirstOrDefault(issue =>
                        string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase))?.Message
                        ?? "This folder does not meet the selected access requirements.";
                    return Failure(sources.Count, readable, source.Path, reason);
                }
            }

            var primaryError = ValidatePrimaryDestination(library);
            if (primaryError is not null)
            {
                var firstManagedPath = library.Sources.FirstOrDefault(source =>
                    source.ManagementMode == LibrarySourceManagementModes.ManagedByTuvima)?.Path;
                return Failure(sources.Count, readable, firstManagedPath ?? library.Name, primaryError);
            }
        }

        var noun = sources.Count == 1 ? "location" : "locations";
        return new SetupMediaLocationsDto(
            true,
            sources.Count,
            readable,
            $"All {sources.Count} configured media {noun} passed server access and destination validation.");
    }

    private static string? ValidatePrimaryDestination(LibraryFolderConfig library)
    {
        var hasManaged = library.Sources.Any(source => source.ManagementMode == LibrarySourceManagementModes.ManagedByTuvima);
        if (!hasManaged)
        {
            return string.IsNullOrWhiteSpace(library.PrimaryDestinationSourceId)
                ? null
                : "A library with only as-is folders cannot have a destination, because Library never adds files to an as-is folder.";
        }

        var destination = library.PrimaryDestination;
        if (destination is null)
        {
            return "A library with folders Library manages needs one of them as its destination for new media.";
        }

        return destination.AccessMode == LibrarySourceAccessModes.Writable
            ? null
            : "The destination must be a managed, writable folder.";
    }

    private static SetupMediaLocationsDto Failure(
        int configured,
        int readable,
        string path,
        string reason) => new(
            false,
            configured,
            readable,
            $"Media location '{path}' needs attention: {reason}");
}
