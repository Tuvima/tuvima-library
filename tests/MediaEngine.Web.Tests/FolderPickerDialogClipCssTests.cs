using System.Text.RegularExpressions;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

/// <summary>Source-level checks that the folder window (Add library step 3, setup, Edit's Add folder, Import) keeps equal padding on both sides of its 1100 by 680 frame.</summary>
public sealed class FolderPickerDialogClipCssTests
{
    private const string PickerCss = "src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor.css";
    private const string FrameCss = "src/MediaEngine.Web/Components/Shared/AppBrowserFrame.razor.css";
    private const string CreateDialogCss = "src/MediaEngine.Web/Components/Settings/Libraries/CreateLibraryDialog.razor.css";

    [Fact]
    public void FolderPicker_FillsItsWindow_InsteadOfCarryingItsOwnFixedWidth()
    {
        var root = Rule(Read(PickerCss), ".server-folder-picker");

        Assert.Contains("width: 100%;", root, StringComparison.Ordinal);
        Assert.Contains("min-width: 0;", root, StringComparison.Ordinal);
        Assert.DoesNotContain("100vw", root, StringComparison.Ordinal);
        Assert.DoesNotContain("1100px", root, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderPickerWindow_KeepsItsFixedHeightAndTakesItsWidthFromTheWindowLadder()
    {
        var appCss = Read("src/MediaEngine.Web/wwwroot/app.css");

        // Editor v5 D32: the folder picker and the supporting-files browser are Look v3's Window at size L; this file sets only the one fixed height.
        Assert.Contains(".tl-dialog.app-browser-host .app-dialog-shell { width: 100%; height: min(680px, calc(100dvh - 32px)); max-height: none; }", appCss, StringComparison.Ordinal);
        Assert.DoesNotContain("server-folder-picker-host", appCss, StringComparison.Ordinal);
        Assert.DoesNotContain("1100px", Rule(appCss, ".tl-dialog.app-browser-host .app-dialog-shell"), StringComparison.Ordinal);
        Assert.Equal("app-browser-host", ServerFolderPicker.HostClass);
    }

    [Theory]
    [InlineData(PickerCss, ".server-folder-picker__inline")]
    [InlineData(CreateDialogCss, ".create-library")]
    [InlineData(CreateDialogCss, ".create-library__picker")]
    public void FolderStepContainers_CanShrinkToTheWindow(string file, string selector)
    {
        var rule = Rule(Read(file), selector);

        Assert.True(
            rule.Contains("min-width: 0;", StringComparison.Ordinal),
            $"{selector} must be able to shrink (min-width: 0) so its right padding is never lost.");
    }

    [Theory]
    [InlineData(FrameCss, ".app-browser")]
    [InlineData(FrameCss, ".app-browser__main")]
    [InlineData(FrameCss, ".app-browser__list")]
    public void BrowserFrameColumns_CanShrinkToTheWindow(string file, string selector)
    {
        Assert.Contains("min-width: 0;", Rule(Read(file), selector), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PickerCss, ".server-folder-picker__top")]
    [InlineData(PickerCss, ".server-folder-picker__name")]
    [InlineData(CreateDialogCss, ".create-library")]
    [InlineData(CreateDialogCss, ".create-library__picker")]
    public void FolderStepGrids_UseAShrinkableSingleColumn(string file, string selector)
    {
        var rule = Rule(Read(file), selector);

        Assert.Contains("grid-template-columns: minmax(0, 1fr);", rule, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PickerCss, ".server-folder-picker")]
    [InlineData(PickerCss, ".server-folder-picker__inline")]
    [InlineData(CreateDialogCss, ".create-library")]
    [InlineData(CreateDialogCss, ".create-library__picker")]
    public void FolderStep_DoesNotHideTheClipWithOverflowHidden(string file, string selector)
    {
        var rule = Rule(Read(file), selector);

        Assert.DoesNotContain("overflow-x: hidden", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow: hidden", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInlineBrowserFrame_HasNoFixedHeight_AndLetsTheListTakeTheSpareHeight()
    {
        var css = Read(FrameCss);
        var inline = Rule(css, ".app-browser--inline");

        Assert.DoesNotContain("28rem", css, StringComparison.Ordinal);
        Assert.Contains("height: auto;", inline, StringComparison.Ordinal);
        Assert.DoesNotContain("height: 28", inline, StringComparison.Ordinal);
        Assert.Contains("flex: 1 1 auto;", inline, StringComparison.Ordinal);

        // The list takes the height left over and never shows fewer than five rows (a header row and five rows is about 16.5rem).
        var list = Rule(css, ".app-browser--inline .app-browser__list");
        Assert.Contains("flex: 1 1 0;", list, StringComparison.Ordinal);
        Assert.Contains("min-height: 16.5rem;", list, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSupportingFilesBrowserKeepsItsOwnFrameHeight()
    {
        var frame = Rule(Read(FrameCss), ".app-browser");

        Assert.Contains("height: 100%;", frame, StringComparison.Ordinal);
        Assert.Contains("min-height: 24rem;", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("app-browser--inline", Read("src/MediaEngine.Web/Components/Editor/EditorFileBrowserDialog.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFolderPickerReservesNoSpaceForMessages()
    {
        var css = Read(PickerCss);

        Assert.DoesNotContain("__notices", css, StringComparison.Ordinal);
        Assert.DoesNotContain("4.75rem", css, StringComparison.Ordinal);
        Assert.DoesNotContain("min-height", Rule(css, ".server-folder-picker__message"), StringComparison.Ordinal);
        Assert.DoesNotContain("server-folder-picker__notices", Read("src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/MediaEngine.Web/Components/Settings/Libraries/AddViewFolderDialog.razor.css", ".add-view-folder")]
    [InlineData(CreateDialogCss, ".create-library[data-step=\"3\"]")]
    public void TheFolderWindowsFillTheWindowBodySoTheListGetsTheSpareHeight(string file, string selector)
    {
        Assert.Contains("min-height: 100%;", Rule(Read(file), selector), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\", "")]
    [InlineData("D:", "")]
    [InlineData("/", "")]
    [InlineData("", "")]
    [InlineData(@"C:\Media\Movies", "Movies")]
    [InlineData("/media/tv/", "tv")]
    public void FriendlyNameFromAPath_IsEmptyForABareDriveRoot_AndTheLastFolderOtherwise(string path, string expected)
    {
        Assert.Equal(expected, FolderHandlingWords.NameFromPath(path));
    }

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, "(?m)^" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"No rule for {selector}.");
        return match.Groups["body"].Value;
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(
        FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("MediaEngine.slnx not found above the test output folder.");
    }
}
