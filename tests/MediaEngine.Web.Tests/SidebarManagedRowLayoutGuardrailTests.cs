using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A playlist or Gallery made by hand is a managed row in the sidebar. Its grip column exists only in reorder mode; without that the name used to fall into
/// the 28 px grip column and the "..." menu was drawn over it, leaving almost nothing to drop songs or photos on (issues 1248, 1249, 1253).
/// </summary>
public sealed class SidebarManagedRowLayoutGuardrailTests
{
    private static readonly string Shell =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "MediaHub"));

    [Fact]
    public void ManagedRow_ReservesTheGripColumnOnlyInReorderMode()
    {
        var css = File.ReadAllText(Path.Combine(Shell, "MediaSectionShell.razor.css"));

        var resting = Regex.Match(css, @"\.media-section-shell__managed-main\s*\{[^}]*grid-template-columns:\s*([^;]+);");
        var reordering = Regex.Match(css, @"\.media-section-shell__managed-main\.has-grip\s*\{[^}]*grid-template-columns:\s*([^;]+);");

        Assert.True(resting.Success && reordering.Success);
        Assert.DoesNotContain("28px", resting.Groups[1].Value);
        Assert.StartsWith("28px", reordering.Groups[1].Value.Trim());
    }

    [Fact]
    public void EveryManagedMain_MarksTheGripColumnWithHasGrip_AndGalleriesAreDropPlaces()
    {
        var razor = File.ReadAllText(Path.Combine(Shell, "MediaSectionShell.razor"));

        Assert.Equal(2, Regex.Matches(razor, @"media-section-shell__managed-main @\(management\.CanReorder && InReorder\(management\) \? ""has-grip"" : null\)").Count);
        Assert.Contains("ManualGalleryNavigationDropTarget => \"gallery\"", razor);
    }
}
