using System.Text;
using MediaEngine.AI.EngineRoom;
using MediaEngine.Api.Services.LocalAssets.Filing;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using SkiaSharp;

namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>What happened when one file was read with text recognition.</summary>
public enum DocumentOcrStatus
{
    /// <summary>Text was recognised and stored.</summary>
    Read,

    /// <summary>The file was read but held no recognisable text; nothing was changed.</summary>
    NoText,

    /// <summary>The item or its file no longer exists.</summary>
    Missing,

    /// <summary>Recognition is switched off or the helper is not installed; the job can wait.</summary>
    Unavailable,

    /// <summary>The file could not be read. Any earlier text was kept.</summary>
    Failed,
}

public sealed record DocumentOcrOutcome(DocumentOcrStatus Status, string Summary);

/// <summary>
/// Reads one scanned PDF or photo with text recognition and stores the text through
/// <see cref="ILocalAssetRepository.SetDocumentTextAsync"/>. PDF pages that already have a text layer are
/// kept as they are; only thin pages are drawn and recognised. Originals are never changed, a failed
/// re-read keeps the earlier good text, and no recognised text is ever written to a log.
/// </summary>
public sealed class DocumentOcrService(
    ILocalAssetRepository repository,
    IConfigurationLoader configuration,
    IOcrService ocr,
    IAiEngineRoom engineRoom,
    ILogger<DocumentOcrService> logger,
    DocumentRulesQueue? rulesQueue = null)
{
    // Every file is read by the hosted text-reading worker from the durable queue, so it is Library background work.
    private static readonly AiCaller Caller = AiCaller.LibraryBackground(AiCallerIds.DocumentTextReading);

    public async Task<DocumentOcrOutcome> ReadItemAsync(
        Guid itemId,
        Func<int, CancellationToken, Task>? reportProgress,
        CancellationToken ct)
    {
        var settings = configuration.LoadOcr();
        if (!settings.Enabled)
        {
            return new DocumentOcrOutcome(DocumentOcrStatus.Unavailable, "Text recognition is switched off.");
        }

        var capability = await ocr.GetCapabilityAsync(ct).ConfigureAwait(false);
        if (!capability.IsAvailable)
        {
            return new DocumentOcrOutcome(DocumentOcrStatus.Unavailable, capability.Reason ?? "Text recognition is not installed.");
        }

        var location = repository.ResolveContent(itemId);
        if (location is null || !File.Exists(location.FilePath))
        {
            return new DocumentOcrOutcome(DocumentOcrStatus.Missing, "The file is no longer on disk.");
        }

        var fileName = Path.GetFileName(location.FilePath);
        var extension = Path.GetExtension(location.FilePath);
        var options = new OcrRunOptions(
            ChooseLanguages(settings, capability),
            LowPriority: OcrEffort.Normalize(settings.Effort) == OcrEffort.Low,
            TimeSpan.FromSeconds(Math.Max(1, settings.PerFileTimeoutSeconds)));

        var workFolder = Path.Combine(ocr.TemporaryRoot, Guid.NewGuid().ToString("N"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.PerFileTimeoutSeconds)));
        try
        {
            Directory.CreateDirectory(workFolder);
            ReadResult? result;
            if (OcrFormats.IsPdf(extension))
            {
                result = await ReadPdfAsync(location.FilePath, fileName, settings, options, workFolder, reportProgress, timeout.Token, ct)
                    .ConfigureAwait(false);
            }
            else if (OcrFormats.IsImage(extension))
            {
                result = await ReadImageAsync(location.FilePath, fileName, settings, options, workFolder, timeout.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                return new DocumentOcrOutcome(DocumentOcrStatus.NoText, "This file type is not read with text recognition.");
            }

            if (result is null || string.IsNullOrWhiteSpace(result.Text))
            {
                return new DocumentOcrOutcome(DocumentOcrStatus.NoText, "No text could be recognised in this file.");
            }

            var written = await repository.SetDocumentTextAsync(
                itemId,
                new LocalAssetDocumentTextWrite(
                    result.Text,
                    result.TextSource,
                    Truncated: result.Partial,
                    Encrypted: false,
                    PageCount: result.PageCount,
                    OcrAverageConfidence: result.AverageConfidence,
                    OcrPages: result.PagesRecognised),
                ct).ConfigureAwait(false);
            if (!written)
            {
                return new DocumentOcrOutcome(DocumentOcrStatus.Missing, "The item no longer has an index entry.");
            }

            if (rulesQueue is not null && repository.IsDocument(itemId))
            {
                // The recognised words replace whatever was there, so rules and suggestions are worked out again.
                await rulesQueue.TryQueueAfterTextAsync(itemId, result.Text, ct).ConfigureAwait(false);
            }

            logger.LogDebug(
                "Recognised {Characters} characters over {Pages} pages in {FileName} (source {Source}, partial {Partial}).",
                result.Text.Length, result.PagesRecognised, fileName, result.TextSource, result.Partial);
            return new DocumentOcrOutcome(
                DocumentOcrStatus.Read,
                $"Recognised {result.Text.Length} characters on {result.PagesRecognised} page(s)" + (result.Partial ? " (partly read)." : "."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Text recognition for {FileName} took longer than the allowed time; the earlier text was kept.", fileName);
            return new DocumentOcrOutcome(DocumentOcrStatus.Failed, "Reading took longer than the allowed time.");
        }
        catch (Exception ex)
        {
            // One bad file must not stop the rest of the batch; the earlier text is kept and the failure is recorded.
            logger.LogWarning(ex, "Text recognition failed for {FileName}.", fileName);
            return new DocumentOcrOutcome(DocumentOcrStatus.Failed, ex.GetType().Name);
        }
        finally
        {
            DeleteFolder(workFolder);
        }
    }

    private sealed record ReadResult(
        string Text,
        string TextSource,
        bool Partial,
        int? PageCount,
        double? AverageConfidence,
        int PagesRecognised);

    private async Task<ReadResult?> ReadImageAsync(
        string path,
        string fileName,
        OcrSettings settings,
        OcrRunOptions options,
        string workFolder,
        CancellationToken token)
    {
        using var decoded = OcrImagePreparer.Decode(path);
        if (decoded is null)
        {
            logger.LogInformation("The image {FileName} could not be decoded for text recognition.", fileName);
            return null;
        }

        if (Math.Min(decoded.Width, decoded.Height) < Math.Max(1, settings.MinImageSidePixels))
        {
            return null;
        }

        var best = await RecognisePreparedAsync(decoded, 0, settings, options, workFolder, "image", token).ConfigureAwait(false);
        if (best.AverageConfidence is null or < 1 || best.AverageConfidence < settings.RotationRetryConfidence)
        {
            var turn = await DetectTurnAsync(decoded, settings, options, workFolder, token).ConfigureAwait(false);
            if (turn is not (null or 0))
            {
                var retry = await RecognisePreparedAsync(decoded, turn.Value, settings, options, workFolder, "turned", token).ConfigureAwait(false);
                if ((retry.AverageConfidence ?? 0) > (best.AverageConfidence ?? 0))
                {
                    best = retry;
                }
            }
        }

        // Ordinary photos give back stray marks; only confident, word-like text is kept.
        var believable = best.WordCount >= Math.Max(1, settings.MinImageWords)
            && (best.AverageConfidence ?? 0) >= settings.MinImageConfidence;
        return string.IsNullOrWhiteSpace(best.Text) || !believable
            ? null
            : new ReadResult(best.Text, LocalAssetTextSources.Ocr, Partial: false, PageCount: null, best.AverageConfidence, PagesRecognised: 1);
    }

    private async Task<ReadResult?> ReadPdfAsync(
        string path,
        string fileName,
        OcrSettings settings,
        OcrRunOptions options,
        string workFolder,
        Func<int, CancellationToken, Task>? reportProgress,
        CancellationToken token,
        CancellationToken callerToken)
    {
        var policy = configuration.LoadLibraries().PersonalLibraryPolicy;
        var maxCharacters = Math.Max(1, policy.DocumentTextMaxCharacters);
        var layers = await Task.Run(
            () => PdfPageSelection.ReadTextLayers(path, Math.Max(1, policy.DocumentTextMaxPdfPages), token), token).ConfigureAwait(false);
        if (layers.Encrypted)
        {
            return null;
        }

        var threshold = Math.Max(1, settings.TextLayerMinCharacters);
        var needing = PdfPageSelection.PagesNeedingOcr(layers.Pages, threshold);
        if (needing.Count == 0)
        {
            return null;
        }

        var pageCap = Math.Max(1, settings.MaxPagesPerFile);
        var partial = needing.Count > pageCap || layers.PageCount > layers.Pages.Count;
        if (needing.Count > pageCap)
        {
            logger.LogWarning(
                "The PDF {FileName} has {Pages} pages to recognise; only the first {Cap} are read and the file is marked partly read.",
                fileName, needing.Count, pageCap);
            needing = needing.Take(pageCap).ToList();
        }

        var recognised = new Dictionary<int, string>();
        double confidenceTotal = 0;
        var confidencePages = 0;
        var done = 0;
        var failedPages = 0;
        try
        {
            foreach (var (pageIndex, bitmap) in PdfPageRenderer.RenderPages(
                path, needing.Select(number => number - 1).ToList(), settings.RenderDpi, settings.MaxRenderSidePixels))
            {
                using (bitmap)
                {
                    token.ThrowIfCancellationRequested();
                    OcrRecognition page;
                    try
                    {
                        page = await RecognisePreparedAsync(bitmap, 0, settings, options, workFolder, $"p{pageIndex + 1}", token)
                            .ConfigureAwait(false);
                        if (page.AverageConfidence is null or < 1 || page.AverageConfidence < settings.RotationRetryConfidence)
                        {
                            var turn = await DetectTurnAsync(bitmap, settings, options, workFolder, token).ConfigureAwait(false);
                            if (turn is not (null or 0))
                            {
                                var retry = await RecognisePreparedAsync(bitmap, turn.Value, settings, options, workFolder, $"p{pageIndex + 1}t", token)
                                    .ConfigureAwait(false);
                                if ((retry.AverageConfidence ?? 0) > (page.AverageConfidence ?? 0))
                                {
                                    page = retry;
                                }
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // One unreadable page must not discard the pages already read; the file is marked partly read.
                        failedPages++;
                        partial = true;
                        logger.LogWarning("Page {Page} of {FileName} could not be recognised and was skipped.", pageIndex + 1, fileName);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(page.Text))
                    {
                        recognised[pageIndex + 1] = page.Text;
                        if (page.AverageConfidence is { } confidence)
                        {
                            confidenceTotal += confidence;
                            confidencePages++;
                        }
                    }

                    done++;
                    if (reportProgress is not null)
                    {
                        await reportProgress(done * 100 / needing.Count, callerToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !callerToken.IsCancellationRequested)
        {
            // The per-file time limit fired: keep the pages read so far and mark the file as partly read.
            partial = true;
            logger.LogWarning("Text recognition for {FileName} ran out of time after {Pages} pages; the pages read so far are kept.", fileName, done);
        }

        if (recognised.Count == 0)
        {
            // Every page failing is a failed read (earlier text is kept); pages that simply held no words are not.
            return failedPages > 0 ? throw new InvalidOperationException("No page could be recognised.") : null;
        }

        var builder = new StringBuilder();
        var embeddedPages = 0;
        foreach (var page in layers.Pages)
        {
            string? text = recognised.TryGetValue(page.Number, out var ocrText) ? ocrText : null;
            if (text is null && page.NonWhitespaceCharacters >= threshold)
            {
                text = page.Text;
                embeddedPages++;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (builder.Length + text.Length + 1 > maxCharacters)
            {
                builder.Append(text, 0, Math.Max(0, maxCharacters - builder.Length));
                partial = true;
                break;
            }

            builder.Append(text.TrimEnd()).Append('\n');
        }

        return new ReadResult(
            builder.ToString(),
            embeddedPages > 0 ? LocalAssetTextSources.Mixed : LocalAssetTextSources.Ocr,
            partial,
            layers.PageCount,
            confidencePages == 0 ? null : Math.Round(confidenceTotal / confidencePages, 1),
            recognised.Count);
    }

    private async Task<OcrRecognition> RecognisePreparedAsync(
        SKBitmap source,
        int turnDegrees,
        OcrSettings settings,
        OcrRunOptions options,
        string workFolder,
        string label,
        CancellationToken token)
    {
        using var prepared = OcrImagePreparer.Prepare(source, turnDegrees, settings.MaxRenderSidePixels);
        var imagePath = Path.Combine(workFolder, $"{label}-{Guid.NewGuid():N}.png");
        OcrImagePreparer.SavePng(prepared, imagePath);
        try
        {
            // The engine room queues the read, leaves a usage record and keeps the exceptions callers already handle.
            var read = await engineRoom
                .ReadTextFromImageOrThrowAsync(
                    Caller,
                    new AiImageTextRequest(imagePath, options.Languages, options.LowPriority, options.Timeout),
                    token)
                .ConfigureAwait(false);
            return new OcrRecognition(read.Text, read.AverageConfidence, read.WordCount);
        }
        finally
        {
            DeleteFile(imagePath);
        }
    }

    private async Task<int?> DetectTurnAsync(
        SKBitmap source,
        OcrSettings settings,
        OcrRunOptions options,
        string workFolder,
        CancellationToken token)
    {
        using var prepared = OcrImagePreparer.Prepare(source, 0, settings.MaxRenderSidePixels);
        var imagePath = Path.Combine(workFolder, $"osd-{Guid.NewGuid():N}.png");
        OcrImagePreparer.SavePng(prepared, imagePath);
        try
        {
            return await ocr.DetectRotationAsync(imagePath, options, token).ConfigureAwait(false);
        }
        finally
        {
            DeleteFile(imagePath);
        }
    }

    /// <summary>Removes page images left behind by an Engine crash or stop; they come from private documents.</summary>
    public void CleanTemporaryFolders()
    {
        try
        {
            if (Directory.Exists(ocr.TemporaryRoot))
            {
                Directory.Delete(ocr.TemporaryRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not clear leftover text recognition scratch files.");
        }
    }

    private static IReadOnlyList<string> ChooseLanguages(OcrSettings settings, OcrCapability capability)
    {
        var installed = capability.InstalledLanguages;
        var chosen = settings.Languages
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Where(code => installed.Contains(code, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (chosen.Count > 0)
        {
            return chosen;
        }

        return installed.Contains("eng", StringComparer.OrdinalIgnoreCase) ? ["eng"] : [installed[0]];
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The whole per-file scratch folder is removed when the file is done.
        }
    }

    private void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the temporary text recognition folder {Folder}.", folder);
        }
    }
}
