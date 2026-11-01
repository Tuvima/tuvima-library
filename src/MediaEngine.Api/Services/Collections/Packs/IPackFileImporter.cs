using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Aggregates;

namespace MediaEngine.Api.Services.Collections.Packs;

/// <summary>
/// The seam the shared Import (Collections and lists spec D17) plugs the "pack file" kind into: recognise a file by its
/// text, show a preview in the pack's own words, and create what it describes. The shared recogniser lists importers
/// of this kind and asks each one whether it <see cref="Recognises"/> the text.
/// </summary>
public interface IPackFileImporter
{
    /// <summary>The kind's id in the shared Import: <c>pack-file</c>.</summary>
    string Kind { get; }

    /// <summary>True when the text announces itself as a pack file.</summary>
    bool Recognises(string content);

    /// <summary>What the file would create in this Library, counted for the profile asking. Throws <see cref="CollectionPackException"/> when it is not a pack file.</summary>
    Task<CollectionPackSummaryDto> PreviewAsync(string content, Guid? viewerProfileId, CancellationToken ct);

    /// <summary>Creates the collections of the file in this Library.</summary>
    Task<CollectionPackCreateResponse> ImportAsync(
        string content,
        CollectionPackCreateRequest ownership,
        Profile? activeProfile,
        Guid? viewerProfileId,
        CancellationToken ct);
}
