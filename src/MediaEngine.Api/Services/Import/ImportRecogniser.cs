using MediaEngine.Contracts.Import;

namespace MediaEngine.Api.Services.Import;

/// <summary>
/// The one place a file that comes into Library is recognised (Collections and lists spec D17). It asks each registered
/// <see cref="IImportKindHandler"/>, in priority order, whether the file is its kind, and returns that kind's preview. When the
/// same kind is registered twice the later registration wins, which is how a packet that owns a kind replaces a placeholder.
/// Nothing here opens a network connection: the file is already in memory and stays on this machine.
/// </summary>
public sealed class ImportRecogniser
{
    private readonly IReadOnlyList<IImportKindHandler> _handlers;

    public ImportRecogniser(IEnumerable<IImportKindHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers
            .GroupBy(handler => handler.Kind, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(handler => handler.Priority)
            .ToList();
    }

    /// <summary>The kinds this Engine can recognise, in detection order.</summary>
    public IReadOnlyList<string> Kinds => _handlers.Select(handler => handler.Kind).ToList();

    /// <summary>Recognises a file and returns its preview. A file that is no known kind returns an unrecognised preview.</summary>
    public async Task<ImportPreviewDto> RecogniseAsync(ImportSource source, ImportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var handler in _handlers)
        {
            if (handler.Detect(source))
            {
                return await handler.PreviewAsync(source, context, ct).ConfigureAwait(false);
            }
        }

        return new ImportPreviewDto
        {
            Kind = ImportKinds.Unknown,
            KindLabel = "Unknown file",
            Recognised = false,
            Message = Collections.ListImport.ListFileNotUnderstoodException.StandardMessage,
        };
    }

    /// <summary>Applies a file as the kind the person was shown. The file is recognised again, so the kind cannot be forged.</summary>
    public async Task<ImportApplyResultDto> ApplyAsync(string kind, ImportSource source, ImportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        var handler = _handlers.FirstOrDefault(candidate => string.Equals(candidate.Kind, kind, StringComparison.Ordinal));
        if (handler is null || !handler.Detect(source))
        {
            return new ImportApplyResultDto(false, "This file is not the kind of file that was previewed.");
        }

        return await handler.ApplyAsync(source, context, ct).ConfigureAwait(false);
    }
}
