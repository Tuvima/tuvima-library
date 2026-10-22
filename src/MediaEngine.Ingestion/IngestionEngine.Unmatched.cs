using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Services;

namespace MediaEngine.Ingestion;

public sealed partial class IngestionEngine
{
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
