using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Engine works out the ingestion numbers once; the Dashboard shows them. These source checks keep a second
/// percentage, estimate or batch formula from growing back in the files that show the queue snapshot.
/// </summary>
public sealed class IngestionQueueProgressGuardrailTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string[] OldBatchFormulaMembers =
    [
        "BatchProgress",
        "ProgressPercent",
        "FilesProcessed",
        "FilesTotal",
        "WorkUnitsTotal",
        "WorkUnitsCompleted",
    ];

    [Fact]
    public void QueueSnapshotFiles_DoNotReadTheOldBatchProgressFormulaMembers()
    {
        foreach (var (path, source) in QueueSnapshotSources())
        {
            foreach (var member in OldBatchFormulaMembers)
            {
                Assert.False(
                    Regex.IsMatch(source, $@"\b{member}\b"),
                    $"{path} must not reference {member}; the Engine's queue snapshot is the only progress source.");
            }
        }
    }

    [Fact]
    public void LivePageAndOverview_DoNotReadAnEstimateOfTheirOwn()
    {
        // The queue snapshot's own EstimatedSecondsRemaining is read only by the shared formatter (and the state's change signature).
        foreach (var relative in new[] { "Components/Settings/IngestionTasksTab.razor", "Components/Settings/OverviewTab.razor" })
        {
            Assert.DoesNotContain("EstimatedSecondsRemaining", File.ReadAllText(Path.Combine(WebRoot, relative)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void QueueSnapshotFiles_DeriveNoIngestionPercentageBeyondTheEnginesProgressTimesOneHundred()
    {
        var percentExpressions = new[]
        {
            // "<done-ish count> * 100" and "100 * <done-ish count>"
            new Regex(@"(Done|Processed|Completed|Found|Waiting|Steps\w*)\s*\*\s*100", RegexOptions.None),
            new Regex(@"100d?\s*\*\s*[\w.]*(Done|Processed|Completed|Found|Waiting|Steps\w*)", RegexOptions.None),
            // "... / <total-ish count>"
            new Regex(@"/\s*[\w.()?]*(FilesFound|FilesTotal|TotalFiles|StepsTotal|StepsPossible)\b", RegexOptions.None),
        };

        var progressTimesHundred = 0;
        foreach (var (path, source) in QueueSnapshotSources())
        {
            foreach (var expression in percentExpressions)
            {
                Assert.False(expression.IsMatch(source), $"{path} derives its own percentage ({expression}).");
            }

            progressTimesHundred += Regex.Matches(source, @"\bProgress\s*\*\s*100\b").Count;
        }

        Assert.Equal(1, progressTimesHundred);
    }

    [Fact]
    public void RetiredDashboardFormulaFiles_StayDeleted()
    {
        foreach (var relative in new[]
                 {
                     "Services/Integration/IngestionBatchProgressSummary.cs",
                     "Services/Integration/IngestionLiveDashboardState.Projection.cs",
                     "Components/Settings/IngestionLiveDashboard.razor",
                     "Components/Settings/IngestionLiveDashboard.razor.cs",
                     "Components/Settings/IngestionDashboardSelectionState.cs",
                 })
        {
            Assert.False(File.Exists(Path.Combine(WebRoot, relative)), $"{relative} was retired with the old Dashboard formulas.");
        }
    }

    private static string WebRoot => Path.Combine(RepoRoot, "src", "MediaEngine.Web");

    private static IEnumerable<(string Path, string Source)> QueueSnapshotSources()
    {
        var files = new List<string>
        {
            Path.Combine(WebRoot, "Components", "Settings", "IngestionTasksTab.razor"),
            Path.Combine(WebRoot, "Components", "Settings", "OverviewTab.razor"),
            Path.Combine(WebRoot, "Components", "Settings", "IngestionQueueProgressText.cs"),
        };
        files.AddRange(Directory.GetFiles(Path.Combine(WebRoot, "Services", "Integration"), "IngestionLiveDashboardState*.cs"));

        foreach (var file in files)
        {
            Assert.True(File.Exists(file), $"{file} is expected to exist.");
            yield return (Path.GetRelativePath(RepoRoot, file), File.ReadAllText(file));
        }
    }
}
