namespace MediaEngine.Web.Tests;

public sealed class SetupPageGuessCountingTests
{
    // The page cannot be rendered without a running Engine, so the rule is pinned at the source: a guess is counted
    // only when the visitor submitted a code, never on a plain page load or a start with no code.
    [Fact]
    public void SetupCodeGuess_IsCountedOnlyWhenACodeWasSubmitted()
    {
        var source = File.ReadAllText(FindSetupPage());

        Assert.Contains("submittedCode.Length > 0 && !TryCountGuess()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"unknown\" key", source, StringComparison.Ordinal);
    }

    private static string FindSetupPage()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "MediaEngine.Web", "Components", "Pages", "SetupPage.razor");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("SetupPage.razor was not found above the test output folder.");
    }
}
