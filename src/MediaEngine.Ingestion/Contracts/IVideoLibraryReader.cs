using MediaEngine.Processors.Models;

namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Reads a Videos library: gives each file its place (standalone, series, section, episode) and how it was identified,
/// from the folder layout, sidecar files and the file name, with no outside lookup.
/// </summary>
public interface IVideoLibraryReader
{
    /// <summary>Adds the folder-derived claims to a processed video and marks its type as Videos.</summary>
    ProcessorResult Apply(ProcessorResult result, string libraryFolder);

    /// <summary>
    /// After a video is registered, gives its series and section their titles so the shelf and series page can show them.
    /// </summary>
    Task RecordPlacementAsync(Guid assetId, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct = default);
}
