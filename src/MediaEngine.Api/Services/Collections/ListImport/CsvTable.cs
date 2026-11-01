using System.Text;

namespace MediaEngine.Api.Services.Collections.ListImport;

/// <summary>
/// A small, strict-enough CSV reader for the exports lists come in (RFC 4180 quoting, quotes inside quotes doubled, line breaks
/// inside quoted fields, a byte order mark, and comma, semicolon or tab separators). It reads text that is already in memory;
/// the caller has limited the size.
/// </summary>
public static class CsvTable
{
    /// <summary>Reads every row. Blank lines come back as a row with one empty cell so a caller can see section breaks.</summary>
    public static List<List<string>> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var delimiter = DetectDelimiter(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var cellStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            if (c == '"' && !cellStarted)
            {
                inQuotes = true;
                cellStarted = true;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
                cellStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                cellStarted = false;
                rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(c);
                cellStarted = true;
            }
        }

        if (cell.Length > 0 || row.Count > 0 || cellStarted)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>True for a row that holds nothing but empty cells.</summary>
    public static bool IsBlank(IReadOnlyList<string> row) => row.All(string.IsNullOrWhiteSpace);

    // The separator that appears most outside quotes on the first non-empty line wins; comma when nothing else is clear.
    private static char DetectDelimiter(string text)
    {
        var comma = 0;
        var semicolon = 0;
        var tab = 0;
        var inQuotes = false;
        var seenContent = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                seenContent = true;
            }
            else if (!inQuotes && c is '\r' or '\n')
            {
                if (seenContent && (comma + semicolon + tab) > 0)
                {
                    break;
                }

                if (seenContent)
                {
                    break;
                }
            }
            else if (!inQuotes)
            {
                seenContent = true;
                switch (c)
                {
                    case ',':
                        comma++;
                        break;
                    case ';':
                        semicolon++;
                        break;
                    case '\t':
                        tab++;
                        break;
                }
            }
        }

        if (tab > comma && tab >= semicolon)
        {
            return '\t';
        }

        return semicolon > comma ? ';' : ',';
    }
}
