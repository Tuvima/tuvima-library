using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Services;

/// <summary>Projects a configured library into the runtime entry the ingestion pipeline works with.</summary>
public static class LibraryFolderEntryFactory
{
    public static LibraryFolderEntry Create(LibraryFolderConfig library)
    {
        ArgumentNullException.ThrowIfNull(library);

        return new LibraryFolderEntry
        {
            Id = library.Id,
            Name = library.Name,
            WriteTags = library.OrganizationPolicy.WriteTags,
            OrganizationMode = library.OrganizationPolicy.Mode,
            CustomOrganizationTemplate = library.OrganizationPolicy.CustomTemplate,
            Kind = library.Kind,
            Area = library.Area,
            Presentation = library.Presentation,
            MetadataPolicy = library.MetadataPolicy,
            Sources = library.Sources.Select(source => new LibrarySourceEntry
            {
                Id = source.Id,
                Path = source.Path,
                ManagementMode = source.ManagementMode,
                AccessMode = source.AccessMode,
                IncludeSubdirectories = source.IncludeSubdirectories,
                ConversionState = source.ConversionState,
            }).ToList(),
            PrimaryDestinationSourceId = library.PrimaryDestinationSourceId,
            MediaTypes = library.MediaTypes
                .Select(MediaTypeParser.Parse)
                .Where(mediaType => mediaType != MediaType.Unknown)
                .ToList(),
            AcceptedIntakeModes = library.AcceptedIntakeModes.ToList(),
        };
    }
}
