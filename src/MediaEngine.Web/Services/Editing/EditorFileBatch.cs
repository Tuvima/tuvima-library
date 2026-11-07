using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Several supporting files edited together (universal editor v5 WP8, screen F, D36): "Editing 2 files". The window shows only the fields the
/// files share (Group, Role, Belongs to, Shown alongside, Add tags); what is typed is staged one change per file in the same pending list as
/// everything else, so Review lists every file. The files are the browser's ticked rows; "Back to the list" closes this and opens the browser
/// again with the ticks kept. Nothing here talks to the Engine.
/// </summary>
public sealed class EditorFileBatch
{
    private readonly List<SupportingFileRowDto> _rows = [];

    public event Action? Changed;

    /// <summary>True while the window shows "Editing N files".</summary>
    public bool IsOpen { get; private set; }

    public Guid TitleId { get; private set; }

    /// <summary>The name of the title the files belong to (the label above "Editing 2 files").</summary>
    public string TitleName { get; private set; } = string.Empty;

    public IReadOnlyList<SupportingFileRowDto> Rows => _rows;

    /// <summary>Opens "Editing N files" for these files. Fewer than two is not a group, so nothing opens.</summary>
    public bool Open(Guid titleId, string titleName, IEnumerable<SupportingFileRowDto> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var next = rows.ToList();
        if (titleId == Guid.Empty || next.Count < 2)
        {
            return false;
        }

        _rows.Clear();
        _rows.AddRange(next);
        TitleId = titleId;
        TitleName = titleName;
        IsOpen = true;
        Changed?.Invoke();
        return true;
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _rows.Clear();
        Changed?.Invoke();
    }

    /// <summary>Takes files that were deleted out of the group; with fewer than two left it is no longer a group and closes.</summary>
    public void Remove(IReadOnlyCollection<Guid> ids)
    {
        if (!IsOpen || _rows.RemoveAll(row => Guid.TryParse(row.Id, out var id) && ids.Contains(id)) == 0)
        {
            return;
        }

        if (_rows.Count < 2)
        {
            Close();
            return;
        }

        Changed?.Invoke();
    }
}

/// <summary>The words the browser and "Editing N files" use. Pure, so the dialog, the window and the tests say the same thing.</summary>
public static class EditorFileBrowserRules
{
    public const string AllTypes = "all";

    /// <summary>"Editing 2 files".</summary>
    public static string EditingTitle(int count) => $"Editing {count} files";

    /// <summary>The count over the table: "Showing 1-25 of 238".</summary>
    public static string ShowingLine(SupportingFileListDto? list)
    {
        if (list is null || list.Total == 0)
        {
            return string.Empty;
        }

        var first = ((Math.Max(1, list.Page) - 1) * Math.Max(1, list.PageSize)) + 1;
        var last = Math.Min(list.Total, first + list.Items.Count - 1);
        return $"Showing {first}–{last} of {list.Total}";
    }

    /// <summary>The page control: "1 of 10".</summary>
    public static string PageLine(SupportingFileListDto? list) => list is null ? string.Empty : $"{Math.Max(1, list.Page)} of {Math.Max(1, list.PageCount)}";

    /// <summary>The window title's line under "Supporting files": "The Last Signal · 238 files across the show".</summary>
    public static string Subtitle(string titleName, int total) =>
        total == 1 ? $"{titleName} · 1 file" : $"{titleName} · {total} files";

    /// <summary>The facts line of "Editing N files": the type of each file when they are few, else a count by type.</summary>
    public static string FactsLine(IReadOnlyList<SupportingFileRowDto> rows)
    {
        var types = rows.GroupBy(row => row.TypeLabel).Select(group => group.Count() == 1 ? group.Key : $"{group.Count()} {group.Key}").ToList();
        return string.Join(" · ", types);
    }

    /// <summary>The Quick facts size line: "26 pages · 3.1 MB".</summary>
    public static string SizeLine(IReadOnlyList<SupportingFileRowDto> rows)
    {
        var pages = rows.Where(row => row.PageCount is > 0).Sum(row => row.PageCount!.Value);
        var bytes = rows.Sum(row => row.SizeBytes);
        var parts = new List<string>();
        if (pages > 0)
        {
            parts.Add(pages == 1 ? "1 page" : $"{pages} pages");
        }

        parts.Add(SizeText(bytes));
        return string.Join(" · ", parts);
    }

    public static string SizeText(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value.ToString(value >= 10 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>The length of a clip, as a small row line: "12 min", "1 h 5 min".</summary>
    public static string Length(double? seconds)
    {
        if (seconds is not > 0)
        {
            return string.Empty;
        }

        var minutes = (int)Math.Round(seconds.Value / 60d);
        return minutes switch
        {
            < 1 => "under 1 min",
            < 60 => $"{minutes} min",
            _ => $"{minutes / 60} h {minutes % 60} min",
        };
    }

    /// <summary>The line under a row's title: the file name, and the length of a clip or the pages of a document.</summary>
    public static string RowLine(SupportingFileRowDto row)
    {
        if (!row.HasOwnTitle)
        {
            return "no title yet";
        }

        var extra = row.Type == SupportingFileListDefaults.TypeClip ? Length(row.DurationSeconds)
            : row.ItemCount > 1 ? $"{row.ItemCount} pictures"
            : string.Empty;
        return extra.Length == 0 ? row.FileName : $"{row.FileName} · {extra}";
    }
}
