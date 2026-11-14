namespace MediaEngine.Domain.Entities;

/// <summary>
/// The work that runs after a title is already usable (preview pictures for scrubbing, lyrics, AI summaries and similar), as opposed to the
/// work that adds, identifies and fetches details for a title. One rule for "working" (spec library-activity D29): a title is being worked on
/// only while its files are added, identified or having their details fetched. Follow-up work never keeps a title in Working now, never
/// makes the Activity card say "working", and is reported on its own quiet Background line with a plain name and a count.
/// </summary>
public static class MediaOperationFollowUp
{
    /// <summary>What one kind of follow-up work is called and counted in.</summary>
    /// <param name="Label">The plain name, "Making preview pictures".</param>
    /// <param name="NounOne">What is counted, for exactly one, "video".</param>
    /// <param name="NounMany">What is counted, for any other number, "videos".</param>
    public sealed record Description(string Label, string NounOne, string NounMany);

    private static readonly IReadOnlyDictionary<string, Description> ByType = new Dictionary<string, Description>(StringComparer.OrdinalIgnoreCase)
    {
        [MediaOperationType.PlaybackScrubPreviews] = new("Making preview pictures", "video", "videos"),
        [MediaOperationType.PlaybackBookmarkFrame] = new("Making bookmark pictures", "bookmark", "bookmarks"),
        [MediaOperationType.TextTrackLyrics] = new("Finding lyrics", "song", "songs"),
        [MediaOperationType.TextTrackSubtitles] = new("Finding subtitles", "video", "videos"),
        [MediaOperationType.PluginCommercialSkip] = new("Finding commercial breaks", "video", "videos"),
        [MediaOperationType.PluginPlaybackSegmentDetection] = new("Finding intros and credits", "video", "videos"),
        [MediaOperationType.WritebackMetadata] = new("Saving details to files", "file", "files"),
        [MediaOperationType.AiTldr] = new("Writing summaries", "title", "titles"),
        [MediaOperationType.AiVibeTags] = new("Finding themes", "title", "titles"),
        [MediaOperationType.AiSmartLabels] = new("Sorting labels", "title", "titles"),
        [MediaOperationType.ViewDocumentTextRead] = new("Reading document text", "document", "documents"),
        [MediaOperationType.ViewDocumentOcr] = new("Reading scanned documents", "document", "documents"),
        [MediaOperationType.ViewDocumentApplyRules] = new("Filing documents", "document", "documents"),
    };

    /// <summary>Every operation type that is follow-up work.</summary>
    public static IReadOnlyCollection<string> OperationTypes { get; } = ByType.Keys.ToArray();

    public static bool IsFollowUp(string? operationType) => operationType is not null && ByType.ContainsKey(operationType);

    public static Description? Describe(string? operationType) => operationType is not null && ByType.TryGetValue(operationType, out var found) ? found : null;
}
