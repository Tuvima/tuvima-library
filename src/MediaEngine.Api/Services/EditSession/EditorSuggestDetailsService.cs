using System.Net;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using UglyToad.PdfPig;
using VersOne.Epub;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// "Suggest details from the document" on a supporting file (universal editor v5 D18): reads what the document itself says about
/// itself, so the person can accept or ignore it. A PDF gives its title, subject and author; an EPUB its title, description and author;
/// plain text and Markdown give their first lines. Local only: no provider is asked and no AI is used. A read only: the suggestions are
/// shown in the form and nothing is saved until Save changes.
/// </summary>
public sealed partial class EditorSuggestDetailsService(
    IItemExtraRepository extras,
    IEditorConfigurationSource editorConfig,
    ILogger<EditorSuggestDetailsService> logger)
{
    private const int MaxLineCharacters = 200;
    private const int MaxTextBytes = 64 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".markdown", ".text", ".nfo", ".rtf" };

    /// <summary>The suggestions for one supporting file; null when there is no such supporting file.</summary>
    public async Task<EditorSuggestDetailsDto?> ReadAsync(Guid supportingFileId, CancellationToken ct = default)
    {
        var extra = await extras.GetAsync(supportingFileId, ct).ConfigureAwait(false);
        if (extra is null)
        {
            return null;
        }

        var settings = editorConfig.Current.SuggestDetails;
        var empty = new EditorSuggestDetailsDto { SupportingFileId = supportingFileId, Source = EditorSuggestDetailsSources.None };
        var extension = Path.GetExtension(extra.FilePath);
        if (!File.Exists(extra.FilePath))
        {
            return empty;
        }

        try
        {
            var read = await Task.Run(() => Read(extra.FilePath, extension, settings), ct).ConfigureAwait(false);
            return read is null
                ? empty
                : new EditorSuggestDetailsDto
                {
                    SupportingFileId = supportingFileId,
                    Source = read.Source,
                    Title = read.Title,
                    Description = read.Description,
                    Author = read.Author,
                    PageCount = read.PageCount,
                    FirstLines = read.FirstLines,
                };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A damaged, password-protected or unusual document simply gives no suggestions; the form stays as it is.
            logger.LogDebug(ex, "Could not read details from a supporting file ({Extension})", extension);
            return empty;
        }
    }

    private sealed record DocumentRead(
        string Source, string? Title, string? Description, string? Author, int? PageCount, IReadOnlyList<string> FirstLines);

    private static DocumentRead? Read(string path, string extension, EditorSuggestDetailsConfiguration settings)
    {
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPdf(path, settings);
        }

        if (extension.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            return ReadEpub(path, settings);
        }

        return TextExtensions.Contains(extension) ? ReadText(path, settings) : null;
    }

    private static DocumentRead ReadPdf(string path, EditorSuggestDetailsConfiguration settings)
    {
        using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        var info = document.Information;
        var lines = document.NumberOfPages > 0
            ? Lines(document.GetPage(1).Text, settings.FirstLines)
            : [];
        var subject = Clean(info.Subject);
        return new DocumentRead(
            EditorSuggestDetailsSources.Pdf,
            Clean(info.Title),
            Truncate(subject ?? string.Join(' ', lines), settings.DescriptionMaxCharacters),
            Clean(info.Author),
            document.NumberOfPages,
            lines);
    }

    private static DocumentRead ReadEpub(string path, EditorSuggestDetailsConfiguration settings)
    {
        var book = EpubReader.ReadBook(path);
        var lines = new List<string>();
        foreach (var chapter in book.ReadingOrder)
        {
            lines.AddRange(Lines(HtmlToText(chapter.Content), settings.FirstLines - lines.Count));
            if (lines.Count >= settings.FirstLines)
            {
                break;
            }
        }

        var description = Clean(HtmlToText(book.Description)) ?? (lines.Count == 0 ? null : string.Join(' ', lines));
        return new DocumentRead(
            EditorSuggestDetailsSources.Epub,
            Clean(book.Title),
            Truncate(description, settings.DescriptionMaxCharacters),
            Clean(book.Author),
            null,
            lines);
    }

    private static DocumentRead ReadText(string path, EditorSuggestDetailsConfiguration settings)
    {
        // Only the start of the file is read, so a large log file costs no more than a note.
        var buffer = new char[MaxTextBytes];
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        var lines = Lines(new string(buffer, 0, read), settings.FirstLines);
        return new DocumentRead(
            EditorSuggestDetailsSources.Text,
            Title: null,
            Truncate(lines.Count == 0 ? null : string.Join(' ', lines), settings.DescriptionMaxCharacters),
            Author: null,
            PageCount: null,
            lines);
    }

    private static List<string> Lines(string? text, int count)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || count < 1)
        {
            return lines;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = WhitespaceRun().Replace(raw.Trim(), " ").TrimStart('#', ' ');
            if (line.Length == 0)
            {
                continue;
            }

            lines.Add(Truncate(line, MaxLineCharacters)!);
            if (lines.Count >= count)
            {
                break;
            }
        }

        return lines;
    }

    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var withBreaks = BlockEnd().Replace(NonText().Replace(html, string.Empty), "\n");
        return WebUtility.HtmlDecode(Tag().Replace(withBreaks, string.Empty));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : WhitespaceRun().Replace(value.Trim(), " ");

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max].TrimEnd() + "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    // The head, scripts and styles hold no reading text (a page's title would run into its first line otherwise).
    [GeneratedRegex(@"<(head|script|style)\b[^>]*>.*?</\1\s*>|<\?.*?\?>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonText();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"</(p|div|h[1-6]|li|br)\s*>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();
}
