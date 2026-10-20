namespace MediaEngine.Storage.Benchmarks.Snapshot;

/// <summary>Turns result rows into snapshot lines in the order the compare command expects (spec D7).</summary>
internal static class SnapshotRows
{
    /// <summary>
    /// Keeps the order the reader returned, except that consecutive rows with the same <c>CreatedAt</c> are sorted by
    /// <c>WorkId</c>, <c>LibraryId</c>, <c>AssetId</c> (and <c>EntityId</c> for the library list) inside their group,
    /// so a changed plan cannot change the file. Rows without a <c>CreatedAt</c> keep the reader's order.
    /// </summary>
    public static List<string> InReaderOrder(IEnumerable<object> rows)
    {
        var formatted = rows.Select(RowFormatter.Format).ToList();
        var lines = new List<string>(formatted.Count);
        var index = 0;
        while (index < formatted.Count)
        {
            var end = index + 1;
            if (formatted[index].CreatedAt is { } createdAt)
            {
                while (end < formatted.Count && string.Equals(formatted[end].CreatedAt, createdAt, StringComparison.Ordinal))
                {
                    end++;
                }
            }

            var group = formatted.GetRange(index, end - index);
            if (group.Count > 1)
            {
                group.Sort(CompareByKeys);
            }

            lines.AddRange(group.Select(row => row.Line));
            index = end;
        }

        return lines;
    }

    /// <summary>The whole section sorted by <c>WorkId</c>, <c>LibraryId</c>, <c>AssetId</c> (its order is not part of the contract).</summary>
    public static List<string> Sorted(IEnumerable<object> rows)
    {
        var formatted = rows.Select(RowFormatter.Format).ToList();
        formatted.Sort(CompareByKeys);
        return [.. formatted.Select(row => row.Line)];
    }

    private static int CompareByKeys(FormattedRow left, FormattedRow right)
    {
        var count = Math.Min(left.Keys.Count, right.Keys.Count);
        for (var i = 0; i < count; i++)
        {
            var result = string.CompareOrdinal(left.Keys[i], right.Keys[i]);
            if (result != 0)
            {
                return result;
            }
        }

        // Rows equal on every key column (not expected) still get one fixed order.
        return string.CompareOrdinal(left.Line, right.Line);
    }
}
