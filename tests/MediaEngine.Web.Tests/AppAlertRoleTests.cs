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
    [InlineData("MediaBrowseShell_TheFavoriteCouldNotBe", "Components/Browse/MediaBrowseShell.razor")]
    public void TheWarningsThatSayAnActionFailed_AreMarkedUrgentWhereTheyAreRaised(string key, string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        var source = File.ReadAllLines(Path.Combine(directory!.FullName, "src", "MediaEngine.Web", file));

        var raised = source.Where(line => line.Contains($"\"{key}\"", StringComparison.Ordinal) && line.Contains("ShowNotice", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(raised);
        Assert.All(raised, line => Assert.Contains("urgent: true", line));
    }
}
