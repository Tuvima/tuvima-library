using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Endpoints;

/// <summary>Administrator settings for reading scanned documents and photos of paper.</summary>
public static class OcrSettingsEndpoints
{
    public static IEndpointRouteBuilder MapOcrSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings/ocr").WithTags("Settings");

        group.MapGet("/", async (
            IConfigurationLoader configuration,
            IOcrService ocr,
            DocumentOcrQueue queue,
            IMediaOperationRepository operations,
            CancellationToken ct) =>
            Results.Ok(await BuildStatusAsync(configuration, ocr, queue, operations, ct).ConfigureAwait(false)))
            .WithName("GetOcrSettings")
            .WithSummary("Returns text recognition settings, language data and the reading queue.")
            .Produces<OcrStatusDto>(StatusCodes.Status200OK)
            .RequireEffectiveAdministrator();

        group.MapPut("/", async (
            OcrSettingsDto request,
            IConfigurationLoader configuration,
            IOcrService ocr,
            DocumentOcrQueue queue,
            IMediaOperationRepository operations,
            CancellationToken ct) =>
        {
            var settings = configuration.LoadOcr();
            settings.Enabled = request.Enabled;
            settings.Effort = OcrEffort.Normalize(request.Effort);
            var known = settings.AvailableLanguages.Select(language => language.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var languages = request.Languages
                .Where(code => !string.IsNullOrWhiteSpace(code) && known.Contains(code.Trim()))
                .Select(code => code.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();
            settings.Languages = languages.Count == 0 ? ["eng"] : languages;
            configuration.SaveOcr(settings);
            ocr.Invalidate();
            return Results.Ok(await BuildStatusAsync(configuration, ocr, queue, operations, ct).ConfigureAwait(false));
        })
        .WithName("UpdateOcrSettings")
        .WithSummary("Saves text recognition on or off, effort and the languages to read with.")
        .Produces<OcrStatusDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        group.MapPost("/languages/{code}", async (
            string code,
            OcrLanguageDownloader downloader,
            CancellationToken ct) =>
        {
            var result = await downloader.DownloadAsync(code, ct).ConfigureAwait(false);
            return result.Status switch
            {
                OcrLanguageDownloadStatus.Installed => Results.Ok(new OcrMessageDto { Message = result.Message }),
                OcrLanguageDownloadStatus.UnknownLanguage => ApiErrors.NotFound(result.Message),
                _ => ApiErrors.Problem(StatusCodes.Status502BadGateway, "Language download failed", result.Message),
            };
        })
        .WithName("DownloadOcrLanguage")
        .WithSummary("Downloads the language data for one extra text recognition language. The only network use of text recognition.")
        .Produces<OcrMessageDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .RequireEffectiveAdministrator();

        group.MapPost("/reread", async (DocumentOcrQueue queue, CancellationToken ct) =>
            await queue.StartRereadAsync(ct).ConfigureAwait(false)
                ? Results.Accepted(uri: (string?)null, value: new OcrMessageDto { Message = "Documents are being queued to be read again." })
                : ApiErrors.Conflict("Reading is already being queued, or text recognition is not ready."))
            .WithName("RereadAllDocuments")
            .WithSummary("Queues every PDF and photo without text to be read again, in batches.")
            .Produces<OcrMessageDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireEffectiveAdministrator();

        return app;
    }

    internal static async Task<OcrStatusDto> BuildStatusAsync(
        IConfigurationLoader configuration,
        IOcrService ocr,
        DocumentOcrQueue queue,
        IMediaOperationRepository operations,
        CancellationToken ct)
    {
        var settings = configuration.LoadOcr();
        var capability = await ocr.GetCapabilityAsync(ct).ConfigureAwait(false);
        var installed = capability.InstalledLanguages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = settings.Languages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = (await operations.GetQueueAsync(DocumentOcrQueue.QueueName, 1000, ct).ConfigureAwait(false))
            .Where(operation => operation.Status is MediaOperationStatus.Queued or MediaOperationStatus.Leased
                or MediaOperationStatus.Running or MediaOperationStatus.RetryWaiting)
            .ToList();
        var current = active.FirstOrDefault(operation => operation.Status is MediaOperationStatus.Leased or MediaOperationStatus.Running);
        var (rereadRunning, rereadQueued) = queue.RereadProgress;
        return new OcrStatusDto
        {
            Settings = new OcrSettingsDto
            {
                Enabled = settings.Enabled,
                Effort = OcrEffort.Normalize(settings.Effort),
                Languages = settings.Languages.ToList(),
            },
            Installed = capability.IsAvailable,
            Message = capability.IsAvailable ? null : capability.Reason,
            Version = capability.Version,
            Languages = settings.AvailableLanguages.Select(language => new OcrLanguageStatusDto
            {
                Code = language.Code,
                Name = language.Name,
                Installed = installed.Contains(language.Code),
                Selected = selected.Contains(language.Code),
            }).ToList(),
            QueueLength = active.Count,
            // File names only: the queue never carries document text.
            CurrentFileName = current?.SourcePath is { Length: > 0 } path ? Path.GetFileName(path) : null,
            RereadRunning = rereadRunning,
            RereadQueued = rereadQueued,
        };
    }
}
