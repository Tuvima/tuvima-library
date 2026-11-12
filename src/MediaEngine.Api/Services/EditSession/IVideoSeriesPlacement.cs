namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// What a series choice does to the video's file (universal editor v6, D24).
/// </summary>
/// <param name="Managed">True when the file sits in a Managed, writable folder, so it follows its series.</param>
/// <param name="AssetId">The video's file.</param>
/// <param name="TargetFolder">The folder the file moves to; null when it does not move.</param>
/// <param name="FromPath">Where the file is now, when it moves.</param>
/// <param name="ToPath">Where it will be, when it moves.</param>
/// <param name="Reason">A plain line saying why the file stays or cannot move; null when it moves or needs no move.</param>
/// <param name="Blocks">True when the reason stops the change from being saved (the file name is taken at the destination).</param>
public sealed record VideoSeriesFilePlan(
    bool Managed, Guid AssetId, string? TargetFolder, string? FromPath, string? ToPath, string? Reason, bool Blocks)
{
    public static VideoSeriesFilePlan Stays(Guid assetId, bool managed = false, string? reason = null) =>
        new(managed, assetId, null, null, null, reason, false);
}

/// <summary>
/// Works out where a video's file goes when it changes series: in a Managed folder the file moves into the series' folder (a new series
/// gets a folder named for it, "on its own" is the library folder's top level); anywhere else it stays and the choice lives in Library only.
/// Reads only. The preview the Dashboard asks for and the check and apply of the series change use this one answer.
/// </summary>
public interface IVideoSeriesPlacement
{
    /// <summary>
    /// The plan for the video <paramref name="entityId"/> (a video or its file) joining the series <paramref name="targetId"/>, starting
    /// the series named by <paramref name="token"/>, or standing alone. Null when the id is not a video.
    /// </summary>
    Task<VideoSeriesFilePlan?> PlanAsync(Guid entityId, string? targetId, string? token, CancellationToken ct = default);
}
