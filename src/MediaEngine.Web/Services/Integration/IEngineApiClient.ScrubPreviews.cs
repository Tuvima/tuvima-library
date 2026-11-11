using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>Whether scrub previews are made, the space they use and whether FFmpeg is there to make them.</summary>
    Task<ScrubPreviewStatusDto?> GetScrubPreviewStatusAsync(CancellationToken ct = default);

    /// <summary>Turns scrub previews on or off. Off removes every preview that was made.</summary>
    Task<ScrubPreviewStatusDto?> SetScrubPreviewsEnabledAsync(bool enabled, CancellationToken ct = default);
}
