using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

// View folders behave like every other library row (spec view-folders-in-libraries, issue #680): names, handling, uploads and clipped cells.
public sealed class ViewFolderRulesTests
{
    private static ViewSourceAdminDto Source(string name, string kind, string mode = "linked", string location = "E:\\Pictures", bool intake = false, string type = "folder") =>
        new(Guid.NewGuid(), intake ? "browser_upload" : type, name, mode, location, true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ContentKind: kind, AcceptsPhotosAndVideos: intake);

    [Fact]
    public void AnEmptyNameSavesTheFoldersOwnName()
    {
        Assert.Equal("Pictures", ViewFolderRules.NameFor("", "D:\\Pictures", []));
        Assert.Equal("Pictures", ViewFolderRules.NameFor("   ", "/mnt/nas/Pictures/", []));
    }

    [Fact]
    public void ATypedNameWinsOverTheFolder()
    {
        Assert.Equal("Camera roll", ViewFolderRules.NameFor(" Camera roll ", "D:\\Pictures", []));
    }

    [Fact]
    public void ANameAlreadyUsedInTheSameKindGetsANumber()
    {
        Assert.Equal("Pictures (2)", ViewFolderRules.NameFor("", "D:\\Pictures", ["Pictures"]));
        Assert.Equal("pictures (3)", ViewFolderRules.NameFor("pictures", null, ["Pictures", "Pictures (2)"]));
    }

    [Fact]
    public void ANumberedNameStaysWithinTheLimit()
    {
        var name = ViewFolderRules.NameFor(new string('a', 60), null, [new string('a', 60)]);
        Assert.True(name.Length <= FolderHandlingWords.MaxFolderNameLength);
        Assert.EndsWith(" (2)", name, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameIsNeverEmpty()
    {
        Assert.Equal("Folder", ViewFolderRules.NameFor("", "C:\\", []));
    }

    [Fact]
    public void OnlyFoldersAPersonAddedToTheKindAreListed_NotTheBuiltInIntake()
    {
        var photos = Source("Photos A", "photos");
        var videos = Source("Videos", "personal_videos");
        var intake = Source("Uploads", "photos", "managed", intake: true);
        var listed = ViewFolderRules.OfKind([photos, videos, intake], "photos");
        Assert.Equal([photos], listed);
    }

    [Fact]
    public void UploadsAreOffUntilAManagedFolderExists_ForEveryKind()
    {
        foreach (var kind in new[] { "photos", "personal_videos", "documents" })
        {
            Assert.False(ViewFolderRules.HasManagedFolder([Source("Read", kind)], kind));
            Assert.True(ViewFolderRules.HasManagedFolder([Source("Read", kind), Source("Managed", kind, "managed")], kind));
        }

        // A Managed folder of another kind does not count.
        Assert.False(ViewFolderRules.HasManagedFolder([Source("Managed", "documents", "managed")], "photos"));
    }

    [Fact]
    public void TheUploadsLineNamesTheKind()
    {
        Assert.Equal("You can't upload photos and videos until you add a Managed folder.", ViewFolderRules.UploadsNeedManagedLine(ViewFolderRules.Noun("photos", "Photos")));
        Assert.Equal("You can't upload personal videos until you add a Managed folder.", ViewFolderRules.UploadsNeedManagedLine(ViewFolderRules.Noun("personal_videos", "Personal videos")));
        Assert.Equal("You can't upload documents until you add a Managed folder.", ViewFolderRules.UploadsNeedManagedLine(ViewFolderRules.Noun("documents", "Documents")));
    }

    [Fact]
    public void AReadOnlyFolderIsLinkedWhereItIsAndAManagedOneIsCreated()
    {
        var linked = ViewFolderRules.RequestFor(new ViewFolderPlan("photos", "", false, "E:\\Pictures"), "Pictures");
        Assert.Equal("linked", linked.StorageMode);
        Assert.Equal("E:\\Pictures", linked.Path);
        Assert.True(linked.IncludeInTimeline);

        var managed = ViewFolderRules.RequestFor(new ViewFolderPlan("documents", "Scans", true, null), "Scans");
        Assert.Equal("managed", managed.StorageMode);
        Assert.Null(managed.Path);
        Assert.False(managed.IncludeInTimeline);
    }

    [Fact]
    public void LibrariesTablesHaveFixedColumnsSoALongPathCannotWidenThem()
    {
        var root = FindRoot();
        var css = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "wwwroot", "app.css"));
        Assert.Contains(".setup-libs__table table, .libs__table table { table-layout: fixed; width: 100%; }", css, StringComparison.Ordinal);
        Assert.Contains(".setup-libs__table td, .libs__table td { min-width: 0; }", css, StringComparison.Ordinal);

        // Setup's table prints no path in a cell any more: the folders are a count that opens a list, and Edit is one button.
        var table = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Setup", "SetupLibrariesTable.razor"));
        Assert.DoesNotContain("@row.Path", table, StringComparison.Ordinal);
        Assert.DoesNotContain("setup-libs__path", table, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFolderListPartServesLibraryFoldersAndViewFoldersThroughOneNeutralItem()
    {
        var popover = File.ReadAllText(Path.Combine(FindRoot(), "src", "MediaEngine.Web", "Components", "Settings", "Libraries", "LibraryFoldersPopover.razor"));
        Assert.Contains("IReadOnlyList<FolderListItem>? Items", popover, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
