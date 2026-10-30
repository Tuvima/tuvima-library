using System.Diagnostics;
using MediaEngine.Api.Services.Plugins.Service;

namespace MediaEngine.Api.Tests;

public sealed class ProcessTreeMemoryTests
{
    [Fact]
    public void Descendants_FollowsChildrenAndGrandchildrenButNotSiblings()
    {
        var parentOf = new Dictionary<int, int> { [10] = 1, [11] = 10, [12] = 11, [20] = 1, [21] = 20 };

        var found = ProcessTreeMemory.Descendants(parentOf, 10);

        Assert.Equal([11, 12], found.Order());
    }

    [Fact]
    public void Descendants_SurvivesLoopsAndASelfParent()
    {
        var parentOf = new Dictionary<int, int> { [10] = 10, [11] = 10, [12] = 11, [10 + 100] = 12, [13] = 12 };
        parentOf[10] = 13;

        var found = ProcessTreeMemory.Descendants(parentOf, 10);

        Assert.DoesNotContain(10, found);
        Assert.Equal(found.Count, found.Distinct().Count());
    }

    [Fact]
    public void Measure_CountsAProgramStartedByTheProgram()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var launcher = Process.Start(new ProcessStartInfo("sh", ["-c", "sleep 30 & wait"]) { UseShellExecute = false })!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ProcessTreeMemory.Descendants(ProcessTreeMemory.ReadParents(), launcher.Id).Any() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }

            Assert.NotEmpty(ProcessTreeMemory.Descendants(ProcessTreeMemory.ReadParents(), launcher.Id));
            launcher.Refresh();
            Assert.True(ProcessTreeMemory.Measure(launcher) > launcher.WorkingSet64);
        }
        finally
        {
            launcher.Kill(entireProcessTree: true);
        }
    }
}
