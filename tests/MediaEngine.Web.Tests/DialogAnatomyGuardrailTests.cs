namespace MediaEngine.Web.Tests;

/// <summary>
/// Dialog anatomy (Look pass v2 A8): the icon tile and the close button are 44 px, and the close icon is 24 px.
/// </summary>
public sealed class DialogAnatomyGuardrailTests
{
    [Fact]
    public void DialogShellCloseButtonIsTheTouchSizeWithA24PixelIcon()
    {
        var css = ReadShared("AppDialogShell.razor.css");

        Assert.Contains("width: var(--tl-control-height-md, 44px);", css);
        Assert.Contains("height: var(--tl-control-height-md, 44px);", css);
        Assert.Contains("font-size: var(--tl-icon-size-md, 24px);", css);
    }

    [Fact]
    public void DialogShellIconTileIs44Pixels()
    {
        var css = ReadShared("AppDialogShell.razor.css");
        var tile = css[css.IndexOf("\n.app-dialog-shell__icon {", StringComparison.Ordinal)..];

        Assert.Contains("width: 44px;", tile[..tile.IndexOf('}', StringComparison.Ordinal)]);
        Assert.Contains("height: 44px;", tile[..tile.IndexOf('}', StringComparison.Ordinal)]);
    }

    [Fact]
    public void PlainDialogHeaderCloseButtonIsTheTouchSize()
    {
        var css = ReadShared("AppDialogFrame.razor.css");

        Assert.Contains("width:var(--tl-control-height-md,44px);height:var(--tl-control-height-md,44px)", css);
    }

    private static string ReadShared(string file)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "src", "MediaEngine.Web", "Components", "Shared", file)).Replace("\r\n", "\n");
    }
}
