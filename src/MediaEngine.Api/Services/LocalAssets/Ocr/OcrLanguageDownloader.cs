using System.Text.RegularExpressions;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Ocr;

public enum OcrLanguageDownloadStatus
{
    Installed,
    UnknownLanguage,
    Failed,
}

public sealed record OcrLanguageDownloadResult(OcrLanguageDownloadStatus Status, string Message);

/// <summary>
/// Downloads one extra language for text recognition when the person asks for it in Settings. This is the
/// only network use of text recognition: nothing is ever downloaded on its own and no document content is sent.
/// </summary>
public sealed partial class OcrLanguageDownloader(
    IHttpClientFactory httpClientFactory,
    IConfigurationLoader configuration,
    IOcrService ocr,
    ILogger<OcrLanguageDownloader> logger)
{
    public const string HttpClientName = "ocr_language_data";

    private const long MinimumBytes = 50 * 1024;
    private const long MaximumBytes = 250L * 1024 * 1024;

    public async Task<OcrLanguageDownloadResult> DownloadAsync(string code, CancellationToken ct)
    {
        var settings = configuration.LoadOcr();
        var option = settings.AvailableLanguages.FirstOrDefault(language =>
            string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase));
        if (option is null || !SafeCode().IsMatch(option.Code))
        {
            return new OcrLanguageDownloadResult(OcrLanguageDownloadStatus.UnknownLanguage, "That language is not in the list.");
        }

        var url = settings.LanguageDownloadUrl.Replace("{code}", option.Code, StringComparison.Ordinal);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new OcrLanguageDownloadResult(OcrLanguageDownloadStatus.Failed, "The language download address in settings is not valid.");
        }

        Directory.CreateDirectory(ocr.LanguageDataDirectory);
        var target = Path.Combine(ocr.LanguageDataDirectory, option.Code + ".traineddata");
        var partial = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Downloading language data for {Language} returned {Status}.", option.Code, (int)response.StatusCode);
                return new OcrLanguageDownloadResult(OcrLanguageDownloadStatus.Failed, "The language could not be downloaded.");
            }

            if (response.Content.Headers.ContentLength is > MaximumBytes)
            {
                return new OcrLanguageDownloadResult(OcrLanguageDownloadStatus.Failed, "The language file is larger than expected.");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaximumBytes)
                    {
                        return new OcrLanguageDownloadResult(OcrLanguageDownloadStatus.Failed, "The language file is larger than expected.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            if (new FileInfo(partial).Length < MinimumBytes)
            {
                return new OcrLanguageDownloadResult(OcrLanguageDownloadStatus.Failed, "The downloaded language file looks incomplete.");
            }

            File.Move(partial, target, overwrite: true);
            ocr.Invalidate();
            logger.LogInformation("Installed text recognition language data for {Language}.", option.Code);
            return new OcrLanguageDownloadResult(OcrLanguageDownloadStatus.Installed, $"{option.Name} is installed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Downloading language data for {Language} failed.", option.Code);
            return new OcrLanguageDownloadResult(OcrLanguageDownloadStatus.Failed, "The language could not be downloaded. Check the connection and try again.");
        }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
                // A leftover .part file is harmless; the next download uses its own unique name.
            }
        }
    }

    [GeneratedRegex("^[a-z]{3}(_[A-Za-z]{2,10})?$")]
    private static partial Regex SafeCode();
}
