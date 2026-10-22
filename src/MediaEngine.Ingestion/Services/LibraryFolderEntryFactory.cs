using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Services;

/// <summary>Projects a configured library into the runtime entry the ingestion pipeline works with.</summary>
public static class LibraryFolderEntryFactory
{
    /// <summary>Builds the runtime entry against the library kinds that ship (see the overload that takes a kinds list).</summary>
    public static LibraryFolderEntry Create(LibraryFolderConfig library) => Create(library, null);

    /// <summary>
    /// Builds the runtime entry. <paramref name="kinds"/> is the library kinds list the library's <c>kind_key</c> is read against
    /// (the kinds that ship when left out): the kind decides the entry's lookup, whatever the library's own fields say.
    /// </summary>
    public static LibraryFolderEntry Create(LibraryFolderConfig library, IReadOnlyList<LibraryKindDefinition>? kinds)
    {
        ArgumentNullException.ThrowIfNull(library);
        var lookup = LibraryKindLookupPolicy.For(library, kinds);

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
            KindKey = library.KindKey,
            LookupMode = lookup.Mode,
            MetadataPolicy = lookup.MetadataPolicy,
            YoutubeLookup = lookup.YoutubeLookup,
            RecycleBin = library.RecycleBin,
            Sources = library.Sources.Select(source => new LibrarySourceEntry
            {
                Id = source.Id,
                Path = source.Path,
                ManagementMode = source.ManagementMode,
                AccessMode = source.AccessMode,
                IncludeSubdirectories = source.IncludeSubdirectories,
                ConversionState = source.ConversionState,
                MergeState = source.MergeState,
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
