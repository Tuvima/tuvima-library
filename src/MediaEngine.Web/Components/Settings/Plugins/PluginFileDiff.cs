namespace MediaEngine.Web.Components.Settings;

/// <summary>What a line of Review changes says about a plugin file: unchanged, taken out, put in, or a run of unchanged lines left out.</summary>
public enum PluginFileDiffKind
{
    Same,
    Removed,
    Added,
    Skipped,
}

/// <summary>One row of the line-by-line comparison. Numbers are 1-based; a removed line has only an old number, an added one only a new number.</summary>
public sealed record PluginFileDiffLine(PluginFileDiffKind Kind, int? OldNumber, int? NewNumber, string Text);

/// <summary>
/// The line-by-line comparison Review changes shows before a plugin file is saved (Plugins and Integrations screens v5 D55). Pure: it
/// compares two texts and nothing else. Line endings do not count as a difference.
/// </summary>
public static class PluginFileDiff
{
    // Above this many cells the middle part is shown as one block removed and one added, so a huge file cannot stall the circuit.
    private const long MaximumCells = 4_000_000;

    /// <summary>Every line of both texts, in order, marked as the same, removed or added.</summary>
    public static IReadOnlyList<PluginFileDiffLine> Compare(string before, string after)
    {
        var old = Split(before);
        var now = Split(after);

        var start = 0;
        while (start < old.Length && start < now.Length && old[start] == now[start])
        {
            start++;
        }

        var endOld = old.Length;
        var endNew = now.Length;
        while (endOld > start && endNew > start && old[endOld - 1] == now[endNew - 1])
        {
            endOld--;
            endNew--;
        }

        var lines = new List<PluginFileDiffLine>(old.Length + now.Length);
        for (var index = 0; index < start; index++)
        {
            lines.Add(new PluginFileDiffLine(PluginFileDiffKind.Same, index + 1, index + 1, old[index]));
        }

        AddMiddle(lines, old, now, start, endOld, endNew);

        for (var offset = 0; offset < old.Length - endOld; offset++)
        {
            lines.Add(new PluginFileDiffLine(PluginFileDiffKind.Same, endOld + offset + 1, endNew + offset + 1, old[endOld + offset]));
        }

        return lines;
    }

    /// <summary>
    /// Keeps the changed lines and <paramref name="context"/> unchanged lines around each change; a longer unchanged run becomes one
    /// <see cref="PluginFileDiffKind.Skipped"/> row that says how many lines it hides.
    /// </summary>
    public static IReadOnlyList<PluginFileDiffLine> WithContext(IReadOnlyList<PluginFileDiffLine> lines, int context = 2)
    {
        var keep = new bool[lines.Count];
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Kind == PluginFileDiffKind.Same)
            {
                continue;
            }

            for (var near = Math.Max(0, index - context); near <= Math.Min(lines.Count - 1, index + context); near++)
            {
                keep[near] = true;
            }
        }

        var result = new List<PluginFileDiffLine>();
        var hidden = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            if (keep[index])
            {
                if (hidden > 0)
                {
                    result.Add(new PluginFileDiffLine(PluginFileDiffKind.Skipped, null, null, hidden == 1 ? "1 unchanged line" : $"{hidden} unchanged lines"));
                    hidden = 0;
                }

                result.Add(lines[index]);
            }
            else
            {
                hidden++;
            }
        }

        if (hidden > 0)
        {
            result.Add(new PluginFileDiffLine(PluginFileDiffKind.Skipped, null, null, hidden == 1 ? "1 unchanged line" : $"{hidden} unchanged lines"));
        }

        return result;
    }

    /// <summary>True when the two texts differ in anything but their line endings.</summary>
    public static bool Differ(string before, string after) =>
        !Split(before).AsSpan().SequenceEqual(Split(after));

    private static string[] Split(string text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n').Split('\n');

    private static void AddMiddle(List<PluginFileDiffLine> lines, string[] old, string[] now, int start, int endOld, int endNew)
    {
        var oldCount = endOld - start;
        var newCount = endNew - start;
        if (oldCount == 0 || newCount == 0 || (long)oldCount * newCount > MaximumCells)
        {
            for (var index = start; index < endOld; index++)
            {
                lines.Add(new PluginFileDiffLine(PluginFileDiffKind.Removed, index + 1, null, old[index]));
            }

            for (var index = start; index < endNew; index++)
            {
                lines.Add(new PluginFileDiffLine(PluginFileDiffKind.Added, null, index + 1, now[index]));
            }

            return;
        }

        // The longest common run of lines, found with the usual table, walked back from the end.
        var table = new int[oldCount + 1, newCount + 1];
        for (var i = oldCount - 1; i >= 0; i--)
        {
            for (var j = newCount - 1; j >= 0; j--)
            {
                table[i, j] = old[start + i] == now[start + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        int a = 0, b = 0;
        while (a < oldCount && b < newCount)
        {
            if (old[start + a] == now[start + b])
            {
                lines.Add(new PluginFileDiffLine(PluginFileDiffKind.Same, start + a + 1, start + b + 1, old[start + a]));
                a++;
                b++;
            }
            else if (table[a + 1, b] >= table[a, b + 1])
            {
                lines.Add(new PluginFileDiffLine(PluginFileDiffKind.Removed, start + a + 1, null, old[start + a]));
                a++;
            }
            else
            {
                lines.Add(new PluginFileDiffLine(PluginFileDiffKind.Added, null, start + b + 1, now[start + b]));
                b++;
            }
        }

        for (; a < oldCount; a++)
        {
            lines.Add(new PluginFileDiffLine(PluginFileDiffKind.Removed, start + a + 1, null, old[start + a]));
        }

        for (; b < newCount; b++)
        {
            lines.Add(new PluginFileDiffLine(PluginFileDiffKind.Added, null, start + b + 1, now[start + b]));
        }
    }
}
