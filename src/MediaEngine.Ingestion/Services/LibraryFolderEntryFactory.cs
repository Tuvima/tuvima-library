using System.Collections.Concurrent;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>Projects a configured library into the runtime entry the ingestion pipeline works with.</summary>
public static class LibraryFolderEntryFactory
{
    // One warning per library and override, however often the libraries file is read again. The keys are a library id plus its fixed
    // sentence (two fields at most), so the set can only grow with the number of libraries ever configured: it is naturally bounded.
    private static readonly ConcurrentDictionary<string, byte> WarnedOverrides = new(StringComparer.Ordinal);

    /// <summary>
    /// The runtime entries for every configured library. The kind of a library wins over its own <c>metadata_policy</c> and
    /// <c>youtube_lookup</c>; where they disagree the library still loads, and one warning names the library and what was overridden
    /// (a person's save is what is refused for that, not a file already on disk).
    /// </summary>
    public static List<LibraryFolderEntry> CreateAll(
        IEnumerable<LibraryFolderConfig> libraries,
        IReadOnlyList<LibraryKindDefinition>? kinds,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var entries = new List<LibraryFolderEntry>();
        foreach (var library in libraries)
        {
            if (logger is not null)
            {
                WarnAboutOverrides(library, kinds ?? LibraryKindsConfiguration.BuiltIn(), logger);
            }

            entries.Add(Create(library, kinds));
        }

        return entries;
    }

    private static void WarnAboutOverrides(LibraryFolderConfig library, IReadOnlyList<LibraryKindDefinition> kinds, ILogger logger)
    {
        var problems = LibraryKindRules.LookupContradictions(library, kinds, $"The library \"{library.Name}\"");
        if (problems.Count == 0)
        {
            return;
        }

        var message = string.Join(" ", problems);
        if (WarnedOverrides.TryAdd($"{library.Id}|{message}", 0))
        {
            logger.LogWarning("{Problem} The kind decides, so the library's own setting is ignored; fix it in libraries.json when convenient.", message);
        }
    }

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
                ScanEnabled = source.ScanEnabled,
                DuplicatePolicyOverride = source.FileHandlingOverride?.DuplicatePolicy,
                WriteTagsOverride = source.FileHandlingOverride?.WriteTags,
                ConversionState = source.ConversionState,
                MergeState = source.MergeState,
            }).ToList(),
            PrimaryDestinationSourceId = library.PrimaryDestinationSourceId,
            DuplicatePolicy = library.FileHandling.DuplicatePolicy,
            MediaTypes = library.MediaTypes
                .Select(MediaTypeParser.Parse)
                .Where(mediaType => mediaType != MediaType.Unknown)
                .ToList(),
            AcceptedIntakeModes = library.AcceptedIntakeModes.ToList(),
        };
    }
}
