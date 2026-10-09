namespace MediaEngine.Api.Tests;

public sealed class OfflineMaintenanceTransactionPolicyTests
{
    [Fact]
    public void ExceptionsAreExactProtectedOfflineUtilities()
    {
        Assert.Equal(new[] { "src/MediaEngine.Api/DevSupport/RealMediaHarness.cs", "src/MediaEngine.Api/DevSupport/RealMediaRepair.cs" }, OfflineMaintenanceTransactionPolicy.Files);
        Assert.False(OfflineMaintenanceTransactionPolicy.Owns("src/MediaEngine.Storage/MediaAssetRepository.cs"));
        Assert.False(OfflineMaintenanceTransactionPolicy.Owns("src/MediaEngine.Api/DevSupport/AnotherRepair.cs"));
        var root = Root();
        var program = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Api/Program.cs"));
        var builder = program.IndexOf("WebApplicationBuilder builder", StringComparison.Ordinal);
        foreach (var marker in new[] { "--repair-real-media", "--prepare-real-media" })
        {
            var dispatch = program.IndexOf(marker, StringComparison.Ordinal);
            Assert.InRange(dispatch, 0, builder - 1);
            Assert.True(program.LastIndexOf("#if DEBUG", dispatch, StringComparison.Ordinal) > program.LastIndexOf("#endif", dispatch, StringComparison.Ordinal));
            Assert.InRange(program.IndexOf("return;", dispatch, StringComparison.Ordinal), dispatch, builder - 1);
        }
        var prepare = program.IndexOf("--prepare-real-media", StringComparison.Ordinal);
        Assert.InRange(program.IndexOf("TryAcquire(ProcessInstanceLease.EngineLeaseName)", StringComparison.Ordinal), 0, prepare - 1);
        var harness = File.ReadAllText(Path.Combine(root, OfflineMaintenanceTransactionPolicy.Files[0]));
        var repair = File.ReadAllText(Path.Combine(root, OfflineMaintenanceTransactionPolicy.Files[1]));
        foreach (var source in new[] { harness, repair })
        {
            Assert.Contains("Pooling = false", source);
            Assert.Contains("ProcessInstanceLease.DashboardLeaseName", source);
            Assert.Contains("if (!dashboard.IsAcquired) throw", source);
            Assert.Contains("RequireSeparate", source);
            Assert.Contains("library-before.db", source);
            Assert.Contains("BackupDatabase", source);
        }
        Assert.Contains("if (!engine.IsAcquired) throw", repair);
        Assert.Contains("ProcessInstanceLease.EngineLeaseName", repair);
        Assert.Contains("A protected real-media run is required.", repair);
        Assert.Contains("RealMediaHarness.ValidateConfiguration(configDirectory, run);", repair);
        Assert.True(repair.IndexOf("BackupDatabase", StringComparison.Ordinal) < repair.IndexOf("connection.BeginTransaction()", StringComparison.Ordinal));
        Assert.True(harness.IndexOf("BackupDatabase", StringComparison.Ordinal) < harness.IndexOf("ResetDatabase(connection)", StringComparison.Ordinal));
        Assert.Contains("RequireSeparate(run.SourceRoot, path)", harness);
        Assert.Contains("RequireSeparate(sourceRoot, path)", harness);
        Assert.Contains("PRAGMA foreign_keys=OFF", harness);
        Assert.Contains("PRAGMA foreign_key_check", harness);
        Assert.Contains("PRAGMA foreign_keys=ON", harness);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }
}
