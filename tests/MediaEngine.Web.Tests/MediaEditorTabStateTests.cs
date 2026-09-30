using MediaEngine.Web.Components.MediaEditor;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorTabStateTests
{
    [Theory]
    [InlineData("identity", "links")]
    [InlineData("universe", "links")]
    [InlineData("inspector", "details")]
    [InlineData("file", "details")]
    [InlineData("files", "details")]
    [InlineData("options", "details")]
    [InlineData("chapters", "details")]
    [InlineData("episodes", "details")]
    [InlineData("tracks", "details")]
    [InlineData("unexpected-tab", "details")]
    [InlineData(null, "details")]
    public void Initialize_NormalizesEditorEntryTabs(string? requested, string expected)
    {
        var state = new MediaEditorTabState();

        state.Initialize(requested);

        Assert.Equal(expected, state.ActiveTab);
    }

    [Fact]
    public void LegacyFileRoute_OpensDetailsWithoutChangingTheCurrentDestination()
    {
        var state = new MediaEditorTabState();
        state.Activate("artwork");

        state.ActivateFile();

        Assert.Equal("details", state.ActiveTab);
        Assert.Equal("details", state.LastNonFileTab);
    }

    [Fact]
    public void EnsureVisible_FallsBackToFirstAvailableTab()
    {
        var state = new MediaEditorTabState();
        state.Activate("history");

        state.EnsureVisible(tab => tab is "details" or "options", ["details", "options"]);

        Assert.Equal("details", state.ActiveTab);
    }
}
