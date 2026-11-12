using Bunit;
using MediaEngine.Web.Components.Editor;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The "Looking up…" row of a Match step (universal editor v6, section 4): every flow, and the plain search, draws a wait the same way, with the words
/// and not only a placeholder bar.
/// </summary>
public sealed class EditorMatchLookingUpTests : AsyncBunitContext
{
    [Fact]
    public void TheWaitRow_SaysLookingUp_InWords_AndIsABusyStatus()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<EditorMatchLookingUp>(parameters => parameters.Add(row => row.TestId, "wikidata-searching"));

        var row = cut.Find("[data-testid=wikidata-searching]");
        Assert.Equal("status", row.GetAttribute("role"));
        Assert.Equal("true", row.GetAttribute("aria-busy"));
        Assert.Contains("Looking up…", row.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMatchFlow_ShowsItsWaitThroughTheSharedRow_NotABareBar()
    {
        var editor = Path.Combine(FindWebRoot(), "Components", "Editor");
        foreach (var flow in new[] { "EditorMatchResults", "EditorTvMatchFlow", "EditorMusicMatchFlow", "EditorWikidataMatch", "EditorGroupMatchPanel" })
        {
            var markup = File.ReadAllText(Path.Combine(editor, flow + ".razor"));
            Assert.Contains("<EditorMatchLookingUp", markup, StringComparison.Ordinal);
        }
    }

    private static string FindWebRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."), "src", "MediaEngine.Web");
    }
}
