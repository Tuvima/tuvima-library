using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<OcrStatusDto?> GetOcrStatusAsync(CancellationToken ct = default) =>
        GetAsync<OcrStatusDto>("GET /settings/ocr", "/settings/ocr", ct: ct);

    public Task<OcrStatusDto?> SaveOcrSettingsAsync(OcrSettingsDto settings, CancellationToken ct = default) =>
        PutAsync<OcrSettingsDto, OcrStatusDto>("PUT /settings/ocr", "/settings/ocr", settings, ct: ct);

    public Task<OcrMessageDto?> DownloadOcrLanguageAsync(string code, CancellationToken ct = default) =>
        PostAsync<object?, OcrMessageDto>(
            "POST /settings/ocr/languages", $"/settings/ocr/languages/{Uri.EscapeDataString(code)}", null, ct: ct);

    public Task<OcrMessageDto?> RereadAllDocumentsAsync(CancellationToken ct = default) =>
        PostAsync<object?, OcrMessageDto>("POST /settings/ocr/reread", "/settings/ocr/reread", null, ct: ct);
}
