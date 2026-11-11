using Bunit;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

/// <summary>Which alerts a screen reader announces at once: errors always, and a warning only when the page says it reports a failed action.</summary>
public sealed class AppAlertRoleTests : LocalizedBunitContext
{
    [Fact]
    public void AnErrorAlert_IsAnnouncedAtOnce()
    {
        var cut = Render<AppAlert>(p => p.Add(a => a.Severity, AppSeverity.Error));

        Assert.Equal("alert", cut.Find(".app-alert").GetAttribute("role"));
    }

    [Fact]
    public void AWarningAlert_ByDefault_IsAQuietStatus()
    {
        var cut = Render<AppAlert>(p => p.Add(a => a.Severity, AppSeverity.Warning));

        Assert.Equal("status", cut.Find(".app-alert").GetAttribute("role"));
    }

    [Fact]
    public void AWarningAlertThatReportsAFailedAction_WhenUrgent_IsAnnouncedAtOnce()
    {
        var cut = Render<AppAlert>(p => p.Add(a => a.Severity, AppSeverity.Warning).Add(a => a.Urgent, true));

        Assert.Equal("alert", cut.Find(".app-alert").GetAttribute("role"));
        Assert.Contains("tl-alert-warning", cut.Find(".app-alert").ClassList);
    }

    [Theory]
    [InlineData("ListenPage_PlaylistOrderSaveFailed", "Components/Pages/ListenPage.razor.cs")]
    [InlineData("ListenPage_SongRemoveFailed", "Components/Pages/ListenPage.razor.cs")]
    [InlineData("MediaBrowseShell_TheFavoriteCouldNotBe", "Components/Browse/MediaBrowseShell.Actions.cs")]
    public void TheWarningsThatSayAnActionFailed_AreMarkedUrgentWhereTheyAreRaised(string key, string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory!.FullName, "src", "MediaEngine.Web", file);
        // A component with partial code files (ListenPage.razor.cs plus ListenPage.*.cs) is read as the lines of all of them, raw.
        var source = file.EndsWith(".razor.cs", StringComparison.Ordinal)
            ? Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path)[..^".razor.cs".Length] + ".*.cs")
                .SelectMany(File.ReadAllLines)
                .ToArray()
            : File.ReadAllLines(path);

        var raised = source.Where(line => line.Contains($"\"{key}\"", StringComparison.Ordinal) && line.Contains("ShowNotice", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(raised);
        Assert.All(raised, line => Assert.Contains("urgent: true", line));
    }
}
