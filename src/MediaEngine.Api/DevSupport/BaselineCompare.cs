using System.Globalization;
using System.Text;

namespace MediaEngine.Api.DevSupport;

public enum BaselineDifferenceKind
{
    Added,
    Removed,
    Changed,
}

/// <summary>One difference between two records: a subject (an item, a group, a count) and one of its fields.</summary>
public sealed record BaselineDifference(string Subject, string Field, BaselineDifferenceKind Kind, string? Before, string? After);

/// <summary>A timing that moved. Listed in the report, never counted as a difference.</summary>
public sealed record BaselineTimingDelta(string Subject, double Before, double After, bool BeyondMargin)
{
    public double Change => After - Before;

    public double Ratio => Before > 0 ? (After - Before) / Before : (After > 0 ? 1 : 0);
}

public sealed class BaselineComparison
{
    public List<BaselineDifference> Differences { get; } = [];

    public List<BaselineTimingDelta> Timings { get; } = [];

    public double Margin { get; init; }

    public bool IsIdentical => Differences.Count == 0;
}

/// <summary>
/// Compares two baseline records: <c>--compare-baseline &lt;before.json&gt; &lt;after.json&gt; &lt;report.md&gt; [--margin 0.2]</c>.
/// Exit code 0 when there is no comparable difference, 1 when there is. Timings never decide the exit code.
/// </summary>
public static class BaselineComparer
{
    public const double DefaultMargin = 0.2;

    // A timing only counts as beyond the margin when it also moved by at least this many seconds, so a 0.4 s step
    // that became 0.6 s is not reported as a 50 percent regression.
    private const double MinimumTimingChangeSeconds = 1.0;

    public static Task<int> RunAsync(string beforePath, string afterPath, string reportPath, string[] options)
    {
        var margin = DefaultMargin;
        var marginIndex = Array.IndexOf(options, "--margin");
        if (marginIndex >= 0 && marginIndex + 1 < options.Length
            && double.TryParse(options[marginIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            margin = parsed;
        }

        var before = BaselineRecordJson.Load(beforePath);
        var after = BaselineRecordJson.Load(afterPath);
        var comparison = Compare(before, after, margin);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, RenderReport(before, after, comparison));
        Console.WriteLine(comparison.IsIdentical
            ? $"Baselines match: no comparable differences ({comparison.Timings.Count(t => t.BeyondMargin)} timing(s) beyond the {margin:P0} margin). Report: {Path.GetFullPath(reportPath)}"
            : $"Baselines differ: {comparison.Differences.Count} difference(s). Report: {Path.GetFullPath(reportPath)}");
        return Task.FromResult(comparison.IsIdentical ? 0 : 1);
    }

    public static BaselineComparison Compare(BaselineRecord before, BaselineRecord after, double margin = DefaultMargin)
    {
        var comparison = new BaselineComparison { Margin = margin };
        var left = Flatten(before);
        var right = Flatten(after);
        foreach (var subject in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var hasLeft = left.TryGetValue(subject, out var leftFields);
            var hasRight = right.TryGetValue(subject, out var rightFields);
            if (!hasRight)
            {
                comparison.Differences.Add(new(subject, "(whole entry)", BaselineDifferenceKind.Removed, Summary(leftFields!), null));
                continue;
            }

            if (!hasLeft)
            {
                comparison.Differences.Add(new(subject, "(whole entry)", BaselineDifferenceKind.Added, null, Summary(rightFields!)));
                continue;
            }

            foreach (var field in leftFields!.Keys.Union(rightFields!.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var inLeft = leftFields.TryGetValue(field, out var leftValue);
                var inRight = rightFields.TryGetValue(field, out var rightValue);
                if (inLeft && inRight)
                {
                    if (!string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                    {
                        comparison.Differences.Add(new(subject, field, BaselineDifferenceKind.Changed, leftValue, rightValue));
                    }
                }
                else if (inLeft)
                {
                    comparison.Differences.Add(new(subject, field, BaselineDifferenceKind.Removed, leftValue, null));
                }
                else
                {
                    comparison.Differences.Add(new(subject, field, BaselineDifferenceKind.Added, null, rightValue));
                }
            }
        }

        CompareTimings(before, after, margin, comparison);
        return comparison;
    }

    private static string Summary(SortedDictionary<string, string> fields) =>
        string.Join("; ", fields.Take(4).Select(pair => $"{pair.Key}={Shorten(pair.Value)}"));

    private static void CompareTimings(BaselineRecord before, BaselineRecord after, double margin, BaselineComparison comparison)
    {
        void Add(string subject, double left, double right)
        {
            var delta = new BaselineTimingDelta(subject, left, right, false);
            var beyond = Math.Abs(delta.Change) >= MinimumTimingChangeSeconds && Math.Abs(delta.Ratio) > margin;
            comparison.Timings.Add(delta with { BeyondMargin = beyond });
        }

        if (before.Timings.TotalSeconds is { } totalBefore && after.Timings.TotalSeconds is { } totalAfter)
        {
            Add("total", totalBefore, totalAfter);
        }

        foreach (var path in before.Timings.RegisteredToIdentified.Keys.Intersect(after.Timings.RegisteredToIdentified.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Add(path, before.Timings.RegisteredToIdentified[path], after.Timings.RegisteredToIdentified[path]);
        }
    }

    // -- flattening -----------------------------------------------------------------------------------------

    /// <summary>Every comparable fact of a record as subject, field and text value.</summary>
    public static SortedDictionary<string, SortedDictionary<string, string>> Flatten(BaselineRecord record)
    {
        var facts = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        SortedDictionary<string, string> Subject(string key)
        {
            if (!facts.TryGetValue(key, out var fields))
            {
                facts[key] = fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
            }

            return fields;
        }

        foreach (var (type, counts) in record.Counts.ByMediaType)
        {
            var fields = Subject($"count: {type}");
            fields["assets"] = counts.Assets.ToString(CultureInfo.InvariantCulture);
            fields["works"] = counts.Works.ToString(CultureInfo.InvariantCulture);
            fields["with Wikidata QID"] = counts.WithWikidataQid.ToString(CultureInfo.InvariantCulture);
            fields["with artwork"] = counts.WithArtwork.ToString(CultureInfo.InvariantCulture);
            fields["review pending"] = counts.ReviewPending.ToString(CultureInfo.InvariantCulture);
            fields["review closed"] = counts.ReviewClosed.ToString(CultureInfo.InvariantCulture);
            fields["text tracks"] = counts.TextTracks.ToString(CultureInfo.InvariantCulture);
        }

        var totals = Subject("count: totals");
        totals["works"] = record.Counts.Works.ToString(CultureInfo.InvariantCulture);
        totals["collections"] = record.Counts.Collections.ToString(CultureInfo.InvariantCulture);
        foreach (var (state, count) in record.Counts.IdentityJobStates)
        {
            totals[$"identity jobs {state}"] = count.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var (kind, count) in record.Counts.ViewItemsByKind)
        {
            totals[$"View items {kind}"] = count.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var item in record.Items)
        {
            FlattenItem(item, Subject($"item: {item.Path}"));
        }

        foreach (var view in record.ViewItems)
        {
            var fields = Subject($"view item: {view.Path}");
            fields["kind"] = view.MediaKind;
            fields["title"] = view.Title ?? string.Empty;
            fields["mime type"] = view.MimeType ?? string.Empty;
            fields["bytes"] = view.ByteSize.ToString(CultureInfo.InvariantCulture);
            fields["content hash"] = view.ContentHash ?? string.Empty;
            fields["captured at"] = view.CapturedAt ?? string.Empty;
            fields["tags"] = Join(view.Tags);
            fields["annotations"] = Join(view.Annotations);
            foreach (var (key, value) in view.Details)
            {
                fields[$"details.{key}"] = value;
            }
        }

        foreach (var collection in record.Collections)
        {
            var fields = Subject($"group: {collection.Type} {collection.Name} [{collection.Qid}]");
            fields["parent"] = collection.Parent ?? string.Empty;
            fields["status"] = collection.Status ?? string.Empty;
            fields["assets"] = Join(collection.Assets);
            fields["relationships"] = Join(collection.Relationships);
        }

        foreach (var member in record.SeriesMembers)
        {
            var fields = Subject($"series member: {member.SeriesQid} / {member.WorkQid}");
            fields["label"] = member.Label ?? string.Empty;
            fields["position"] = member.Position ?? string.Empty;
            fields["owned"] = member.Owned ? "yes" : "no";
        }

        if (record.UnattachedReview.Count > 0)
        {
            Subject("review: not attached to a file")["entries"] =
                Join(record.UnattachedReview.Select(review => $"{review.Subject} {review.Trigger} {review.Status}"));
        }

        return facts;
    }

    private static void FlattenItem(BaselineItem item, SortedDictionary<string, string> fields)
    {
        fields["media type"] = item.MediaType;
        fields["identity.job state"] = item.Identity.JobState ?? string.Empty;
        fields["identity.resolved QID"] = item.Identity.ResolvedQid ?? string.Empty;
        fields["identity.outcome"] = item.Identity.Outcome ?? string.Empty;
        fields["identity.title"] = item.Identity.Title ?? string.Empty;
        fields["identity.Wikidata QID"] = item.Identity.WikidataQid ?? string.Empty;
        FlattenWork("work", item.Work, fields);
        if (item.ParentWork is not null)
        {
            FlattenWork("parent work", item.ParentWork, fields);
        }

        fields["edition.format label"] = item.Edition.FormatLabel ?? string.Empty;
        fields["edition.QID"] = item.Edition.Qid ?? string.Empty;
        fields["edition.assets"] = Join(item.Edition.Assets);
        fields["groups"] = Join(item.Groups.Select(group => $"{group.Type}: {group.Name} [{group.Qid}]"));
        foreach (var (key, values) in item.ExternalIds)
        {
            fields[$"external id.{key}"] = Join(values);
        }

        foreach (var (scope, set) in item.Values)
        {
            foreach (var (key, value) in set.Scalars)
            {
                fields[$"{scope} value.{key}"] =
                    $"{value.Value} (won by {value.Provider ?? "unknown"}{(value.Conflicted ? ", conflicted" : string.Empty)}{(value.NeedsReview ? ", needs review" : string.Empty)})";
            }

            foreach (var (key, values) in set.Arrays)
            {
                fields[$"{scope} list.{key}"] = Join(values);
            }
        }

        foreach (var (key, artwork) in item.Artwork)
        {
            fields[$"artwork.{key}"] =
                $"{artwork.Provider} | {artwork.SourceUrl} | {artwork.ContentHash} | {artwork.Width}x{artwork.Height}";
        }

        fields["people"] = Join(item.People.Select(person => $"{person.Role}: {person.Name} [{person.Qid}]"));
        fields["text tracks"] = Join(item.TextTracks.Select(track =>
            $"{track.Kind} {track.Language} {track.Provider} {track.SourceFormat}->{track.NormalizedFormat}"));
        fields["review"] = Join(item.Review.Select(review => $"{review.Trigger} ({review.Status})"));
    }

    private static void FlattenWork(string prefix, BaselineWork work, SortedDictionary<string, string> fields)
    {
        fields[$"{prefix}.kind"] = work.Kind;
        fields[$"{prefix}.title"] = work.Title ?? string.Empty;
        fields[$"{prefix}.QID"] = work.Qid ?? string.Empty;
        fields[$"{prefix}.ordinal"] = work.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        fields[$"{prefix}.ownership"] = work.Ownership ?? string.Empty;
        fields[$"{prefix}.match level"] = work.MatchLevel ?? string.Empty;
        fields[$"{prefix}.external identifiers"] = work.ExternalIdentifiers ?? string.Empty;
        fields[$"{prefix}.grouped assets"] = Join(work.Assets);
    }

    private static string Join(IEnumerable<string> values) => string.Join(" | ", values);

    // -- report ---------------------------------------------------------------------------------------------

    public static string RenderReport(BaselineRecord before, BaselineRecord after, BaselineComparison comparison)
    {
        var text = new StringBuilder();
        text.AppendLine("# Ingestion baseline comparison");
        text.AppendLine();
        text.AppendLine("| | Before | After |");
        text.AppendLine("|---|---|---|");
        text.AppendLine($"| Label | {Cell(before.Metadata.Label)} | {Cell(after.Metadata.Label)} |");
        text.AppendLine($"| Build | {Cell(before.Metadata.BuildCommit)} | {Cell(after.Metadata.BuildCommit)} |");
        text.AppendLine($"| Started | {Cell(before.Metadata.StartedAt)} | {Cell(after.Metadata.StartedAt)} |");
        text.AppendLine($"| Provider answers | {Cell(before.Metadata.ProviderAnswers ?? "live")} | {Cell(after.Metadata.ProviderAnswers ?? "live")} |");
        text.AppendLine($"| Duration (s) | {Cell(before.Metadata.TotalDurationSeconds?.ToString("0.0", CultureInfo.InvariantCulture))} | {Cell(after.Metadata.TotalDurationSeconds?.ToString("0.0", CultureInfo.InvariantCulture))} |");
        text.AppendLine($"| Items / View items | {before.Items.Count} / {before.ViewItems.Count} | {after.Items.Count} / {after.ViewItems.Count} |");
        text.AppendLine();
        text.AppendLine(comparison.IsIdentical
            ? "**Result: no comparable differences.**"
            : $"**Result: {comparison.Differences.Count} difference(s).**");
        text.AppendLine();

        AppendCounts(text, before, after);
        AppendDifferences(text, comparison);
        AppendTimings(text, comparison);
        return text.ToString();
    }

    private static void AppendCounts(StringBuilder text, BaselineRecord before, BaselineRecord after)
    {
        text.AppendLine("## Counts per media type");
        text.AppendLine();
        text.AppendLine("| Media type | Assets | Works | With QID | With artwork | Review pending | Review closed | Text tracks |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var type in before.Counts.ByMediaType.Keys.Union(after.Counts.ByMediaType.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            before.Counts.ByMediaType.TryGetValue(type, out var left);
            after.Counts.ByMediaType.TryGetValue(type, out var right);
            text.AppendLine($"| {type} | {Pair(left?.Assets, right?.Assets)} | {Pair(left?.Works, right?.Works)} | {Pair(left?.WithWikidataQid, right?.WithWikidataQid)} | {Pair(left?.WithArtwork, right?.WithArtwork)} | {Pair(left?.ReviewPending, right?.ReviewPending)} | {Pair(left?.ReviewClosed, right?.ReviewClosed)} | {Pair(left?.TextTracks, right?.TextTracks)} |");
        }

        text.AppendLine();
        text.AppendLine($"View items: {string.Join(", ", before.Counts.ViewItemsByKind.Keys.Union(after.Counts.ViewItemsByKind.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(kind => $"{kind} {Pair(CountOf(before.Counts.ViewItemsByKind, kind), CountOf(after.Counts.ViewItemsByKind, kind))}"))}");
        text.AppendLine();
    }

    private static int CountOf(IDictionary<string, int> counts, string key) => counts.TryGetValue(key, out var value) ? value : 0;

    private static string Pair(int? left, int? right) =>
        left == right ? $"{left}" : $"{left?.ToString(CultureInfo.InvariantCulture) ?? "-"} -> {right?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    private static void AppendDifferences(StringBuilder text, BaselineComparison comparison)
    {
        text.AppendLine("## Differences");
        text.AppendLine();
        if (comparison.Differences.Count == 0)
        {
            text.AppendLine("None.");
            text.AppendLine();
            return;
        }

        // Review-queue differences get their own section; the rest are grouped by what kind of subject they are about.
        var review = comparison.Differences.Where(IsReview).ToList();
        var others = comparison.Differences.Except(review).GroupBy(difference => SubjectKind(difference.Subject)).OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in others)
        {
            text.AppendLine($"### {group.Key}");
            text.AppendLine();
            AppendGrouped(text, group.ToList());
        }

        if (review.Count > 0)
        {
            text.AppendLine("### Needs Review queue");
            text.AppendLine();
            AppendGrouped(text, review);
        }
    }

    private static bool IsReview(BaselineDifference difference) =>
        difference.Field is "review" or "entries" or "review pending" or "review closed"
        || difference.Subject.StartsWith("review:", StringComparison.Ordinal);

    private static string SubjectKind(string subject)
    {
        var colon = subject.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? subject[..colon] : subject;
    }

    // Identical changes on several subjects (a parent work shared by every episode of a show) are one line.
    private static void AppendGrouped(StringBuilder text, List<BaselineDifference> differences)
    {
        foreach (var group in differences.GroupBy(d => (d.Field, d.Kind, d.Before, d.After)).OrderBy(g => g.Min(d => d.Subject), StringComparer.Ordinal))
        {
            var first = group.First();
            var subjects = group.Select(d => d.Subject).Order(StringComparer.Ordinal).ToList();
            var where = subjects.Count == 1
                ? $"`{subjects[0]}`"
                : $"{subjects.Count} entries, e.g. `{subjects[0]}`, `{subjects[1]}`{(subjects.Count > 2 ? " ..." : string.Empty)}";
            var change = first.Kind switch
            {
                BaselineDifferenceKind.Added => $"added: {Shorten(first.After)}",
                BaselineDifferenceKind.Removed => $"removed: {Shorten(first.Before)}",
                _ => $"{Shorten(first.Before)} -> {Shorten(first.After)}",
            };
            text.AppendLine($"- {where}: **{first.Field}** {change}");
        }

        text.AppendLine();
    }

    private static void AppendTimings(StringBuilder text, BaselineComparison comparison)
    {
        text.AppendLine($"## Timings (not differences; margin {comparison.Margin:P0})");
        text.AppendLine();
        var beyond = comparison.Timings.Where(t => t.BeyondMargin).ToList();
        if (beyond.Count == 0)
        {
            text.AppendLine($"{comparison.Timings.Count} timing(s) compared; none moved beyond the margin.");
            text.AppendLine();
            return;
        }

        text.AppendLine($"{beyond.Count} of {comparison.Timings.Count} timing(s) moved beyond the margin.");
        text.AppendLine();
        text.AppendLine("| Subject | Before (s) | After (s) | Change |");
        text.AppendLine("|---|---|---|---|");
        foreach (var delta in beyond.OrderByDescending(t => Math.Abs(t.Ratio)).Take(40))
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| `{delta.Subject}` | {delta.Before:0.0} | {delta.After:0.0} | {delta.Ratio:+0%;-0%;0%} |"));
        }

        text.AppendLine();
    }

    private static string Cell(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("|", "/", StringComparison.Ordinal);

    private static string Shorten(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        var single = value.Replace('\r', ' ').Replace('\n', ' ');
        return single.Length <= 240 ? single : single[..237] + "...";
    }
}
