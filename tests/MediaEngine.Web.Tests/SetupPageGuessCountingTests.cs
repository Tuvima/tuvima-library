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

    // The Engine refuses setup from every remote caller, with or without a code. The page must say so itself,
    // before any Engine call, so a remote visitor never spends the Engine's shared Dashboard allowance.
    [Fact]
    public void RemoteVisitor_IsRefusedByThePageBeforeAnyEngineCall()
    {
        var source = File.ReadAllText(FindSetupPage()).Replace("\r\n", "\n");

        var remoteCheck = source.IndexOf("== MediaEngine.Web.Services.Configuration.IngressKind.Remote", StringComparison.Ordinal);
        var firstEngineCall = source.IndexOf("await ApiClient.BeginSetupAsync", StringComparison.Ordinal);
        Assert.True(remoteCheck > 0, "The page no longer checks for a remote visitor.");
        Assert.True(remoteCheck < firstEngineCall, "The remote check must come before BeginSetupAsync.");
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
