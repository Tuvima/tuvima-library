using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Services;
using MediaEngine.Processors.Models;

namespace MediaEngine.Ingestion;

public sealed partial class IngestionEngine
{
    /// <summary>
    /// The normal YouTube lookup for a file whose name or <c>.info.json</c> carries a YouTube ID, under the gates ingestion uses: the
    /// library asks YouTube (the YouTube kind, never a plain kind), and the Vault and adult-content gate allows it. Only the ID leaves the
    /// machine. Returns the result with the lookup merged in, or unchanged when any gate says no or the lookup finds nothing.
    /// </summary>
    private async Task<ProcessorResult> MergeYoutubeLookupAsync(ProcessorResult result, LibraryFolderEntry? library, CancellationToken ct)
    {
        if (_videoLookup is null
            || _videoReader is null
            || library?.AsksYoutube != true
            || (_audienceGate is not null && !await _audienceGate.MayAskAboutLibraryAsync("youtube", library.Id, ct).ConfigureAwait(false))
            || result.Claims.FirstOrDefault(claim => claim.Key == MetadataFieldConstants.YoutubeVideoId)?.Value is not { } youtubeId
            || await _videoLookup.LookupAsync(youtubeId, ct).ConfigureAwait(false) is not { } online)
        {
            return result;
        }

        return _videoReader.Merge(result, online);
    }

    /// <summary>
    /// A file in a YouTube library with no YouTube ID, in its name or its <c>.info.json</c>, cannot be matched (Library setup spec D18, D34):
    /// it stays exactly where it is, is marked Unmatched with reason <c>no_youtube_id</c>, and is never looked up. A file that has an ID is
    /// not marked here. A library that is not the YouTube kind never marks anything: a plain kind is never Unmatched (D38).
    /// </summary>
    private async Task MarkYoutubeFileWithoutIdUnmatchedAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        if (_unmatchedMarker is null
            || context.IsVault
            || context.ResolvedMediaType != MediaType.Videos
            || context.Library is not { LookupMode: LibraryLookupMode.YouTube, Id: { Length: > 0 } libraryId }
            || context.ProcessorResult is not { } result
            || ResolveVideosRoot(context.Library, context.Candidate.Path) is null
            || result.Claims.Any(claim => claim.Key == MetadataFieldConstants.YoutubeVideoId && !string.IsNullOrWhiteSpace(claim.Value)))
        {
            return;
        }

        await _unmatchedMarker.MarkNoYoutubeIdAsync(context.AssetId, libraryId, context.Candidate.Path, ct).ConfigureAwait(false);
    }
}
