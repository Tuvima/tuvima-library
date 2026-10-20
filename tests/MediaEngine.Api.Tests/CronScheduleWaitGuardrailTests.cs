using System.Reflection;
using System.Text.RegularExpressions;
using MediaEngine.Api.Services;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Cron-scheduled background work must wait with <see cref="CronScheduler.WaitUntilAsync(DateTimeOffset, CancellationToken)"/>.
/// A single <c>Task.Delay</c> of the gap to the next run throws for gaps over ~49.7 days (a yearly schedule, say), and
/// the unhandled exception stops the Engine.
/// </summary>
public sealed class CronScheduleWaitGuardrailTests
{
    // A statement that hands a cron run time to Task.Delay, or turns one into a gap by subtraction.
    private static readonly Regex DelayForCronRun = new(@"Task\.Delay\([^;]*(NextRunAt|NextOccurrence|nextRun)", RegexOptions.Compiled);
    private static readonly Regex GapToCronRun = new(@"(NextRunAt|NextOccurrence|nextRun)\b[^;]*\s-\s|\s-\s[^;]*(NextRunAt|NextOccurrence|nextRun)\b", RegexOptions.Compiled);

    [Fact]
    public void CronScheduler_OffersNoDelayToHandToTaskDelay()
    {
        var spans = typeof(CronScheduler)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(TimeSpan) || method.ReturnType == typeof(TimeSpan?))
            .Select(method => method.Name)
            .ToList();

        Assert.True(spans.Count == 0,
            $"CronScheduler must hand out a run time and wait with WaitUntilAsync, not return a delay: {string.Join(", ", spans)}");
    }

    [Fact]
    public void EngineSource_NeverDelaysForTheGapToACronRun()
    {
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(FindRepoDirectory("src"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            if (source.Contains("CronScheduler.UntilNext", StringComparison.Ordinal) || DelayForCronRun.IsMatch(source) || GapToCronRun.IsMatch(source))
            {
                offenders.Add(Path.GetFileName(path));
            }
        }

        Assert.True(offenders.Count == 0,
            $"Wait for a cron run with CronScheduler.WaitUntilAsync, not Task.Delay: {string.Join(", ", offenders)}");
    }

    private static string FindRepoDirectory(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}");
    }
}
