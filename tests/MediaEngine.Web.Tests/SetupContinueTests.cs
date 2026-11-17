namespace MediaEngine.Web.Tests;

// Setup shows only Back and Continue (issue 1038): the old "return to the current step" button could land on All set without its checklist.
public sealed class SetupContinueTests
{
    [Fact]
    public void SetupStepsOfferNoReturnToCurrentStepButton()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var files = Directory.GetFiles(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Setup"), "*.razor")
            .Append(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Pages", "SetupPage.razor"));

        foreach (var file in files)
        {
            Assert.DoesNotContain("ReturnToCurrentStep", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ContinueFromARevisitedLibrariesStepMovesOnAndLoadsThatStep()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var page = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Pages", "SetupPage.razor"));
        var start = page.IndexOf("private async Task ValidateMediaAsync", StringComparison.Ordinal);
        var body = page[start..page.IndexOf("OfferVaultAsync", start, StringComparison.Ordinal)];

        Assert.Contains("if (revisiting) await GoNextAsync();", body, StringComparison.Ordinal);
        Assert.Contains("await EnsureCurrentStageDataAsync();", body, StringComparison.Ordinal);
    }
}

// A finished batch says how long it took (issue 1039); the Engine already serves the batch's start and end times.
public sealed class ActivityTookTests
{
    [Theory]
    [InlineData(20, "Took under a minute")]
    [InlineData(12 * 60, "Took 12 min")]
    [InlineData(60 * 60, "Took 1 h")]
    [InlineData(125 * 60, "Took 2 h 5 min")]
    public void ABatchSaysHowLongItTook(int seconds, string expected)
    {
        Assert.Equal(expected, MediaEngine.Web.Components.Settings.LibraryActivity.LibraryActivityText.Took(TimeSpan.FromSeconds(seconds)));
    }
}
