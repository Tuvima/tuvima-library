using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

public sealed class StyleOwnershipGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly Regex Comments = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex Important = new(@"!important\b");
    private static readonly Regex Deep = new(@"::deep\b");

    [Fact]
    public void IsolatedStylesheets_StayWithinReviewedLineBudgets()
    {
        using var baseline = ReadBaseline();
        var exceptions = baseline.RootElement.GetProperty("lineExceptions");
        foreach (var path in Stylesheets().Where(path => path.EndsWith(".razor.css", StringComparison.Ordinal)))
        {
            var relative = Relative(path);
            var limit = exceptions.TryGetProperty(relative, out var value) ? value.GetInt32() : 2000;
            Assert.True(File.ReadLines(path).Count() <= limit, $"{relative} exceeds its {limit}-line ownership budget.");
        }
        if (baseline.RootElement.GetProperty("finalized").GetBoolean())
            Assert.Empty(exceptions.EnumerateObject());
    }

    [Fact]
    public void PriorityAndDeepCounts_StayWithinPerFileAndAggregateRatchets()
    {
        using var baseline = ReadBaseline();
        var budgets = baseline.RootElement.GetProperty("files");
        var important = 0;
        var deep = 0;
        foreach (var path in Stylesheets())
        {
            var relative = Relative(path);
            var text = Comments.Replace(File.ReadAllText(path), "");
            var fileImportant = Important.Matches(text).Count;
            var fileDeep = Deep.Matches(text).Count;
            var known = budgets.TryGetProperty(relative, out var budget);
            Assert.True(fileImportant <= (known ? budget.GetProperty("important").GetInt32() : 0), $"Unbudgeted priority in {relative}.");
            Assert.True(fileDeep <= (known ? budget.GetProperty("deep").GetInt32() : 0), $"Unbudgeted deep rule in {relative}.");
            important += fileImportant;
            deep += fileDeep;
        }
        var totals = baseline.RootElement.GetProperty("totals");
        Assert.True(important <= totals.GetProperty("important").GetInt32(), "Aggregate priority ratchet increased.");
        Assert.True(deep <= totals.GetProperty("deep").GetInt32(), "Aggregate deep ratchet increased.");
        if (baseline.RootElement.GetProperty("finalized").GetBoolean())
            Assert.True(important < baseline.RootElement.GetProperty("initialTotals").GetProperty("important").GetInt32());
    }

    [Fact]
    public void IncreasedOwnerBudgets_HaveBalancedDocumentedTransfers()
    {
        using var baseline = ReadBaseline();
        var initial = baseline.RootElement.GetProperty("initialFiles");
        var balances = new Dictionary<(string Path, string Metric), int>();
        foreach (var transfer in baseline.RootElement.GetProperty("transfers").EnumerateArray())
        {
            var source = transfer.GetProperty("source").GetString()!;
            var destination = transfer.GetProperty("destination").GetString()!;
            Assert.NotEqual(source, destination);
            Assert.False(string.IsNullOrWhiteSpace(transfer.GetProperty("reason").GetString()));
            Assert.NotEmpty(transfer.GetProperty("selectors").EnumerateArray());
            foreach (var metric in new[] { "important", "deep" })
            {
                var count = transfer.GetProperty(metric).GetInt32();
                Assert.True(count >= 0);
                balances[(source, metric)] = balances.GetValueOrDefault((source, metric)) - count;
                balances[(destination, metric)] = balances.GetValueOrDefault((destination, metric)) + count;
            }
        }
        foreach (var file in baseline.RootElement.GetProperty("files").EnumerateObject())
            foreach (var metric in new[] { "important", "deep" })
            {
                var old = initial.TryGetProperty(file.Name, out var oldFile) ? oldFile.GetProperty(metric).GetInt32() : 0;
                var permitted = old + balances.GetValueOrDefault((file.Name, metric));
                Assert.True(file.Value.GetProperty(metric).GetInt32() <= Math.Max(0, permitted), $"{file.Name} {metric} grew without a reviewed transfer.");
            }
    }

    private static JsonDocument ReadBaseline() => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "tests", "MediaEngine.Web.Tests", "Fixtures", "style-ownership-baseline.json")));
    private static IEnumerable<string> Stylesheets() => Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "MediaEngine.Web"), "*.css", SearchOption.AllDirectories)
        .Where(path => !Relative(path).Split('/').Any(segment => segment is "bin" or "obj" or "vendor"));
    private static string Relative(string path) => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');
}
