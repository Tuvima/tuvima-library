using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<ScrubPreviewStatusDto?> GetScrubPreviewStatusAsync(CancellationToken ct = default) =>
        GetAsync<ScrubPreviewStatusDto>("GET /settings/playback/scrub-previews", "/settings/playback/scrub-previews", ct: ct);

    public Task<ScrubPreviewStatusDto?> SetScrubPreviewsEnabledAsync(bool enabled, CancellationToken ct = default) =>
        PutAsync<UpdateScrubPreviewSettingRequestDto, ScrubPreviewStatusDto>(
            "PUT /settings/playback/scrub-previews", "/settings/playback/scrub-previews",
            new UpdateScrubPreviewSettingRequestDto { Enabled = enabled }, ct: ct);
}
