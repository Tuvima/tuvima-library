using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>Text recognition settings, installed languages and the reading queue (file names only).</summary>
    Task<OcrStatusDto?> GetOcrStatusAsync(CancellationToken ct = default);

    Task<OcrStatusDto?> SaveOcrSettingsAsync(OcrSettingsDto settings, CancellationToken ct = default);

    /// <summary>Downloads one extra recognition language. The only network use of text recognition.</summary>
    Task<OcrMessageDto?> DownloadOcrLanguageAsync(string code, CancellationToken ct = default);

    /// <summary>Queues every PDF and photo without text to be read again, in batches.</summary>
    Task<OcrMessageDto?> RereadAllDocumentsAsync(CancellationToken ct = default);
}
