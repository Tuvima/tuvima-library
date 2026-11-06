using System.Text.RegularExpressions;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

/// <summary>Source-level checks that the folder window (Add library step 3, setup, Edit's Add folder, Import) keeps equal padding on both sides of its 1100 by 680 frame.</summary>
public sealed class FolderPickerDialogClipCssTests
{
    private const string PickerCss = "src/MediaEngine.Web/Components/Shared/ServerFolderPicker.razor.css";
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
    public void FolderPickerWindow_KeepsItsFixedFrame()
    {
        var appCss = Read("src/MediaEngine.Web/wwwroot/app.css");

        Assert.Contains(".tl-dialog.server-folder-picker-host { width: min(calc(100vw - 32px), 1100px) !important; }", appCss, StringComparison.Ordinal);
        Assert.Contains("height: min(680px, calc(100dvh - 32px))", appCss, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PickerCss, ".server-folder-picker__inline")]
    [InlineData(PickerCss, ".server-folder-picker__list")]
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
    [InlineData(PickerCss, ".server-folder-picker__top")]
    [InlineData(PickerCss, ".server-folder-picker__name")]
    [InlineData(PickerCss, ".server-folder-picker__pathbar")]
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
