using System.Globalization;
using System.Text;
using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// The "Export list (CSV)" file: one row per document the person can see, columns for the organising details and the
/// custom fields, never the words inside the document. Cells that a spreadsheet could read as a formula are defused,
/// and the file starts with a byte-order mark so Excel reads the accents correctly.
/// </summary>
public static class ViewDocumentCsv
{
    /// <summary>The most rows one export writes. A longer list is cut here and the person is told.</summary>
    public const int MaximumRows = 10_000;

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private static readonly string[] LeadingColumns =
        ["Title", "File name", "Document date", "Correspondent", "Type", "Tags", "Archive number"];

    /// <summary>
    /// Cells starting with = + - @ (or a tab or carriage return) would run as a formula in a spreadsheet; a single quote
    /// in front keeps them as plain text. Cells containing a comma, quote or line break are wrapped in quotes.
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(['"', ',', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <param name="fieldValues">The custom field values per document id; a document missing from it has none.</param>
    public static async Task WriteAsync(
        Stream output, IEnumerable<LocalAssetDto> items, IReadOnlyList<ViewDocumentFieldDto> fields,
        IReadOnlyDictionary<Guid, IReadOnlyList<ViewDocumentFieldValueDto>> fieldValues, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, Utf8WithBom, leaveOpen: true) { NewLine = "\r\n" };
        // StreamWriter writes the byte-order mark itself on the first write.
        await writer.WriteLineAsync(string.Join(',', LeadingColumns.Concat(fields.Select(field => field.Name)).Append("Added").Select(Cell))).ConfigureAwait(false);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(Row(item, fields, fieldValues.GetValueOrDefault(item.Id))).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static string Row(
        LocalAssetDto item, IReadOnlyList<ViewDocumentFieldDto> fields, IReadOnlyList<ViewDocumentFieldValueDto>? fieldValues)
    {
        var info = item.DocumentInfo;
        var values = new Dictionary<Guid, ViewDocumentFieldValueDto>();
        foreach (var value in fieldValues ?? info?.FieldValues ?? [])
        {
            values[value.FieldId] = value;
        }

        var cells = new List<string>
        {
            item.Title ?? string.Empty,
            item.FileName,
            info?.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            info?.Correspondent?.Name ?? string.Empty,
            info?.DocumentType?.Name ?? string.Empty,
            string.Join("; ", item.Tags),
            info?.ArchiveNumber ?? string.Empty,
        };
        cells.AddRange(fields.Select(field => values.TryGetValue(field.Id, out var value) ? FieldText(value) : string.Empty));
        cells.Add(item.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return string.Join(',', cells.Select(Cell));
    }

    private static string FieldText(ViewDocumentFieldValueDto value) => value.Kind switch
    {
        ViewDocumentFieldKinds.Number => value.NumberValue?.ToString("0.##########", CultureInfo.InvariantCulture) ?? string.Empty,
        ViewDocumentFieldKinds.Money => value.NumberValue is { } amount
            ? $"{amount.ToString("0.00", CultureInfo.InvariantCulture)} {value.TextValue}".TrimEnd() : string.Empty,
        ViewDocumentFieldKinds.Date => value.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        ViewDocumentFieldKinds.Bool => value.BoolValue is { } flag ? (flag ? "Yes" : "No") : string.Empty,
        _ => value.TextValue ?? string.Empty,
    };
}
