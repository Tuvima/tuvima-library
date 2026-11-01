using System.Text;
using MediaEngine.Contracts.Import;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.Import;

/// <summary>A file (or pasted text) handed to the shared Import, as bytes the Engine already holds in memory.</summary>
public sealed class ImportSource
{
    private string? _text;

    public ImportSource(string fileName, byte[] bytes)
    {
        FileName = Path.GetFileName(fileName ?? string.Empty);
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
    }

    /// <summary>The file's name only, never a path.</summary>
    public string FileName { get; }

    public byte[] Bytes { get; }

    /// <summary>True when the bytes start like a ZIP archive.</summary>
    public bool IsZip => Bytes.Length >= 4 && Bytes[0] == 0x50 && Bytes[1] == 0x4B;

    /// <summary>The bytes read as UTF-8 text (a byte order mark is dropped). Only meaningful when <see cref="IsZip"/> is false.</summary>
    public string Text
    {
        get
        {
            if (_text is not null)
            {
                return _text;
            }

            var offset = Bytes.Length >= 3 && Bytes[0] == 0xEF && Bytes[1] == 0xBB && Bytes[2] == 0xBF ? 3 : 0;
            return _text = Encoding.UTF8.GetString(Bytes, offset, Bytes.Length - offset);
        }
    }
}

/// <summary>Who is importing and what they may see.</summary>
/// <param name="ActiveProfile">The profile making the request, when there is one.</param>
/// <param name="AllowedWorkIds">The titles that profile may see. Null lets a match consider every title.</param>
public sealed record ImportContext(Profile? ActiveProfile, IReadOnlySet<Guid>? AllowedWorkIds);

/// <summary>
/// One kind of file the shared Import understands (Collections and lists spec D17). The Import recogniser asks each handler in
/// <see cref="Priority"/> order whether it recognises a file (<see cref="Detect"/>), then asks that handler for a preview in
/// the kind's own words and, where the kind can be applied from the dialog on its own, applies it.
///
/// <para>
/// To add a kind: add a constant to <see cref="ImportKinds"/>, write a handler, and register it in
/// <c>AddTuvimaImport</c> (<c>TuvimaImportServiceCollectionExtensions</c>). Registering a second handler for a kind that already
/// has one replaces it, so the collection packs packet fills in the pack file kind by registering its own handler for
/// <see cref="ImportKinds.PackFile"/> after the placeholder.
/// </para>
/// </summary>
public interface IImportKindHandler
{
    /// <summary>The kind this handler reads. See <see cref="ImportKinds"/>.</summary>
    string Kind { get; }

    /// <summary>Detection order, lowest first. The catch-all list handler is last.</summary>
    int Priority { get; }

    /// <summary>A quick check of whether the file is this kind. It must not throw for a file of another kind.</summary>
    bool Detect(ImportSource source);

    /// <summary>The preview frame for a file this handler detected.</summary>
    Task<ImportPreviewDto> PreviewAsync(ImportSource source, ImportContext context, CancellationToken ct);

    /// <summary>Applies the file where the kind can be applied from the dialog on its own; otherwise says where it goes.</summary>
    Task<ImportApplyResultDto> ApplyAsync(ImportSource source, ImportContext context, CancellationToken ct);
}
