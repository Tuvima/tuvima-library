using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaEngine.Storage.Benchmarks.Snapshot;

/// <summary>
/// <c>compare --before &lt;file&gt; --after &lt;file&gt;</c>: checks two snapshot files section by section. Exit code 0 when every
/// section matches; the only allowed difference is the D7 case, where rows tied on <c>CreatedAt</c> at Home's 1,000-row
/// cut-off moved in or out (listed as "tie at cut-off"). Anything else is printed (first 20 differing lines, counts of rows
/// only in one file) and gives exit code 1.
/// </summary>
internal static partial class CompareCommand
{
    private const int MaxDifferingLines = 20;
    private const int MaxTieRowsListed = 20;

    public static int Run(string beforePath, string afterPath)
    {
        var before = SnapshotFile.Read(beforePath);
        var after = SnapshotFile.Read(afterPath);
        var ok = before.CheckHash(beforePath) & after.CheckHash(afterPath);

        var names = before.Sections.Select(section => section.Name)
            .Union(after.Sections.Select(section => section.Name))
            .ToList();
        foreach (var name in names)
        {
            var left = before.Sections.FirstOrDefault(section => section.Name == name);
            var right = after.Sections.FirstOrDefault(section => section.Name == name);
            if (left is null || right is null)
            {
                Console.WriteLine($"{name}: DIFFERENT - section is only in the {(left is null ? "after" : "before")} file.");
                ok = false;
                continue;
            }

            ok &= CompareSection(left, right);
        }

        if (ok)
        {
            Console.WriteLine("compare passed");
            return 0;
        }

        Console.WriteLine("compare FAILED");
        return 1;
    }

    private static bool CompareSection(SnapshotSection before, SnapshotSection after)
    {
        var name = before.Name;
        if (before.Header != after.Header)
        {
            Console.WriteLine($"{name}: header differs: '{before.Header}' vs '{after.Header}' (a different sample was chosen; are the files from the same seeded database?).");
        }

        if (before.Lines.SequenceEqual(after.Lines, StringComparer.Ordinal))
        {
            Console.WriteLine($"{name}: identical ({before.Lines.Count:N0} lines)");
            return before.Header == after.Header;
        }

        var onlyBefore = Subtract(before.Lines, after.Lines);
        var onlyAfter = Subtract(after.Lines, before.Lines);

        if (name == "home" && TryTieAtCutOff(before, after, onlyBefore, onlyAfter))
        {
            return before.Header == after.Header;
        }

        Console.WriteLine($"{name}: DIFFERENT ({before.Lines.Count:N0} lines before, {after.Lines.Count:N0} after; {onlyBefore.Count:N0} only in before, {onlyAfter.Count:N0} only in after)");
        var shown = 0;
        for (var i = 0; i < Math.Max(before.Lines.Count, after.Lines.Count) && shown < MaxDifferingLines; i++)
        {
            var left = i < before.Lines.Count ? before.Lines[i] : null;
            var right = i < after.Lines.Count ? after.Lines[i] : null;
            if (string.Equals(left, right, StringComparison.Ordinal))
            {
                continue;
            }

            shown++;
            Console.WriteLine($"  line {i + 1} of the section ({Identify(left ?? right!)}):");
            PrintLinePair(left, right);
        }

        return false;
    }

    /// <summary>
    /// Home returns the newest 1,000 rows. When rows tie on <c>CreatedAt</c> at the cut-off, which of them make the cut
    /// may change (D7). If every difference is such a row, the section passes and the rows are listed.
    /// </summary>
    private static bool TryTieAtCutOff(SnapshotSection before, SnapshotSection after, List<string> onlyBefore, List<string> onlyAfter)
    {
        var cutOff = LastCreatedAt(before.Lines);
        if (cutOff is null || cutOff != LastCreatedAt(after.Lines))
        {
            return false;
        }

        var tiedBefore = onlyBefore.Where(line => CreatedAtOf(line) == cutOff).ToList();
        var tiedAfter = onlyAfter.Where(line => CreatedAtOf(line) == cutOff).ToList();
        // A row that is in both lists under the same identity changed a value; that is a real difference, not a tie moving.
        var identitiesAfter = tiedAfter.Select(Identify).ToHashSet(StringComparer.Ordinal);
        if (before.Lines.Count != after.Lines.Count || tiedBefore.Any(line => identitiesAfter.Contains(Identify(line))))
        {
            return false;
        }

        var restBefore = Subtract(before.Lines, tiedBefore);
        var restAfter = Subtract(after.Lines, tiedAfter);
        if (!restBefore.SequenceEqual(restAfter, StringComparer.Ordinal))
        {
            return false;
        }

        Console.WriteLine($"{before.Name}: identical except tie at cut-off (CreatedAt {cutOff}): {tiedBefore.Count:N0} rows only in before, {tiedAfter.Count:N0} only in after");
        foreach (var line in tiedBefore.Take(MaxTieRowsListed))
        {
            Console.WriteLine($"  tie at cut-off, only before: {Identify(line)}");
        }

        foreach (var line in tiedAfter.Take(MaxTieRowsListed))
        {
            Console.WriteLine($"  tie at cut-off, only after:  {Identify(line)}");
        }

        return true;
    }

    private static string? LastCreatedAt(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? null : CreatedAtOf(lines[^1]);

    private static string? CreatedAtOf(string line)
    {
        var match = CreatedAtPattern().Match(line);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string Identify(string line)
    {
        var parts = line.Split(RowFormatter.FieldSeparator);
        return string.Join(" ", parts.Where(part => part.StartsWith("WorkId=", StringComparison.Ordinal)
            || part.StartsWith("LibraryId=", StringComparison.Ordinal)
            || part.StartsWith("AssetId=", StringComparison.Ordinal)
            || part.StartsWith("EntityId=", StringComparison.Ordinal)
            || part.StartsWith("CreatedAt=", StringComparison.Ordinal)));
    }

    /// <summary>The lines of <paramref name="from"/> that have no unmatched equal line in <paramref name="other"/>, in order.</summary>
    private static List<string> Subtract(IReadOnlyList<string> from, IReadOnlyList<string> other)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in other)
        {
            remaining[line] = remaining.GetValueOrDefault(line) + 1;
        }

        var result = new List<string>();
        foreach (var line in from)
        {
            if (remaining.TryGetValue(line, out var count) && count > 0)
            {
                remaining[line] = count - 1;
            }
            else
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static void PrintLinePair(string? left, string? right)
    {
        if (left is null || right is null)
        {
            Console.WriteLine($"    before: {Shorten(left)}");
            Console.WriteLine($"    after:  {Shorten(right)}");
            return;
        }

        var leftFields = left.Split(RowFormatter.FieldSeparator);
        var rightFields = right.Split(RowFormatter.FieldSeparator);
        if (leftFields.Length != rightFields.Length)
        {
            Console.WriteLine($"    before: {Shorten(left)}");
            Console.WriteLine($"    after:  {Shorten(right)}");
            return;
        }

        for (var i = 0; i < leftFields.Length; i++)
        {
            if (!string.Equals(leftFields[i], rightFields[i], StringComparison.Ordinal))
            {
                Console.WriteLine($"    before: {Shorten(leftFields[i])}");
                Console.WriteLine($"    after:  {Shorten(rightFields[i])}");
            }
        }
    }

    private static string Shorten(string? text) =>
        text is null ? "<no line>" : text.Length <= 400 ? text : text[..400] + "...";

    [GeneratedRegex(@"(?:^| \| )CreatedAt=(?<value>[^ ]*)")]
    private static partial Regex CreatedAtPattern();
}

/// <summary>One <c>## name</c> block of a snapshot file.</summary>
internal sealed record SnapshotSection(string Name, string Header, List<string> Lines);

/// <summary>A parsed snapshot file: its sections and whether its own <c>sha256=</c> line matches its contents.</summary>
internal sealed class SnapshotFile
{
    private SnapshotFile(List<SnapshotSection> sections, string body, string? recordedHash)
    {
        Sections = sections;
        Body = body;
        RecordedHash = recordedHash;
    }

    public List<SnapshotSection> Sections { get; }

    private string Body { get; }

    private string? RecordedHash { get; }

    public static SnapshotFile Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"'{path}' does not exist.");
        }

        var text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var all = text.Split('\n');
        var sections = new List<SnapshotSection>();
        var bodyLength = 0;
        string? hash = null;
        SnapshotSection? current = null;
        for (var i = 0; i < all.Length; i++)
        {
            var line = all[i];
            if (i == all.Length - 1 && line.Length == 0)
            {
                break;
            }

            if (line.StartsWith("sha256=", StringComparison.Ordinal) && i >= all.Length - 2)
            {
                hash = line["sha256=".Length..];
                continue;
            }

            bodyLength += line.Length + 1;
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var header = line[3..];
                var space = header.IndexOf(' ', StringComparison.Ordinal);
                current = new SnapshotSection(space < 0 ? header : header[..space], header, []);
                sections.Add(current);
            }
            else if (current is not null)
            {
                current.Lines.Add(line);
            }
            else
            {
                throw new InvalidOperationException($"'{path}' is not a snapshot file: line {i + 1} is outside any '## section'.");
            }
        }

        return new SnapshotFile(sections, text[..bodyLength], hash);
    }

    /// <summary>Warns when the file's own hash line does not match its contents (a hand edit or a cut-off file). Not an error by itself.</summary>
    public bool CheckHash(string path)
    {
        if (RecordedHash is null)
        {
            Console.WriteLine($"note: '{path}' has no sha256 line (incomplete or cut-off file?).");
            return false;
        }

        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Body))).ToLowerInvariant();
        if (!string.Equals(actual, RecordedHash, StringComparison.Ordinal))
        {
            Console.WriteLine($"note: '{path}' does not match its own sha256 line (edited after it was written).");
        }

        return true;
    }
}
