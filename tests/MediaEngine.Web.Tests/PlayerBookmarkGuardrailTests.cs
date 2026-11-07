using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Everyone's bookmarks are made, changed, imported and exported by an administrator in the editor's Bookmarks tab (universal editor v5 D37),
/// never in a player or in the book reader. The players and the reader keep the Everyone/Mine filter, the markers, jumping, and adding,
/// renaming and deleting your own.
/// </summary>
public sealed class PlayerBookmarkGuardrailTests
{
    private static readonly string[] PlayerFiles =
    [
        "src/MediaEngine.Web/Components/Shared/BookmarksPanel.razor",
        "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor",
        "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor",
        "src/MediaEngine.Web/Components/Pages/BookReader.razor",
    ];

    private static readonly string[] RetiredNames =
    [
        "Promote",
        "BookmarkFindView",
        "AudiobookGlobalExtras",
        "GetEveryonesBookmarks",
        "UpdateGlobalAudiobookBookmark",
        "ExportBookmark",
        "FindBookmarks",
        "AllowEveryones",
        "GetEveryonesReaderBookmarks",
        "PromoteReaderBookmark",
        "ExportGlobal",
        "BookmarkFind",
        "_showEveryones",
    ];

    [Fact]
    public void ThePlayers_DoNotOfferPromoteEveryonesEditingFindOrExport()
    {
        foreach (var file in PlayerFiles)
        {
            var source = Read(file);
            foreach (var name in RetiredNames)
            {
                Assert.False(source.Contains(name, StringComparison.Ordinal), $"{file} still mentions {name}.");
            }
        }
    }

    [Fact]
    public void TheBookmarksPanel_HasNoSwitchToMakeANewBookmarkEveryones_AndOldPanesAreGone()
    {
        var panel = Read("src/MediaEngine.Web/Components/Shared/BookmarksPanel.razor");

        // The only switch is "Show on timeline"; a bookmark is never shared from the player.
        Assert.Single(Regex.Matches(panel, "<AppSwitchRow"));
        Assert.DoesNotContain("CanShare", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("IsGlobal = true", panel, StringComparison.Ordinal);
        var root = FindRepoRoot();
        foreach (var gone in new[]
        {
            "src/MediaEngine.Web/Components/Watch/VideoBookmarkPane.razor",
            "src/MediaEngine.Web/Components/Shared/AudiobookBookmarkDialog.razor",
            "src/MediaEngine.Web/wwwroot/js/audiobook-bookmark-dialog.js",
        })
        {
            Assert.False(File.Exists(Path.Combine(root, gone)), $"{gone} should be gone: the Bookmarks panel replaced it.");
        }
    }

    [Fact]
    public void ThePlayerBookmarkWording_NeverSaysGlobalEveryonesPromoteFindOrImport()
    {
        var root = FindRepoRoot();
        var banned = new Regex(@"\bGlobal\b|Everyone['’]s|\bPromote\b|\bFind\b|\bImport\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var strings = XDocument.Load(Path.Combine(root, "src/MediaEngine.Web/Resources/SharedStrings.resx"));
        var values = strings.Root!.Elements("data")
            .Where(data => ((string?)data.Attribute("name"))?.StartsWith("BookmarksPanel_", StringComparison.Ordinal) == true)
            .Select(data => (Name: (string)data.Attribute("name")!, Value: (string?)data.Element("value") ?? string.Empty))
            .ToList();
        Assert.NotEmpty(values);
        foreach (var (name, value) in values)
        {
            Assert.False(banned.IsMatch(value), $"{name} says \"{value}\".");
        }

        Assert.DoesNotContain("Global", (string?)strings.Root!.Elements("data").Single(data => (string?)data.Attribute("name") == "Common_Everyone").Element("value"));
    }

    [Fact]
    public void TheReader_ListsOnlyMineAndEveryones_AndAdministratorsCannotChangeAnEveryonesBookmarkThere()
    {
        var reader = Read("src/MediaEngine.Web/Components/Pages/BookReader.razor");

        // Mine and Everyone's are one list; only a personal bookmark has a delete button.
        Assert.Contains("CanChange(mark)", reader, StringComparison.Ordinal);
        Assert.Contains("private static bool CanChange(ReaderBookmarkDto mark) => !mark.IsGlobal;", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("IsAdministrator", reader, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/MediaEngine.Web/Services/Integration/IEngineApiClient.Playback.cs")]
    [InlineData("src/MediaEngine.Web/Services/Integration/EngineApiClient.Playback.cs")]
    public void TheDashboardClient_NoLongerCarriesTheAdministratorBookmarkCalls(string file)
    {
        var client = Read(file);

        foreach (var name in new[]
        {
            "PromoteAudiobookBookmark", "GetEveryonesBookmarks", "UpdateGlobalAudiobookBookmark", "CompareBookmarkSet", "ApplyBookmarkSet",
            "ExportGlobalBookmarks", "CompareReaderBookmarkSet", "ApplyReaderBookmarkSet", "ExportGlobalReaderBookmarks",
            "GetEveryonesReaderBookmarks", "PromoteReaderBookmark",
        })
        {
            Assert.False(client.Contains(name, StringComparison.Ordinal), $"{file} still carries {name}.");
        }
    }

    [Theory]
    [InlineData("src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs")]
    [InlineData("src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs")]
    public void TheEngine_NoLongerServesThePlayerOrReaderAdministratorBookmarkRoutes(string file)
    {
        var endpoints = Read(file);

        foreach (var route in new[] { "/promote", "bookmarks/everyones", "bookmark-sets", "bookmarks/global" })
        {
            Assert.False(endpoints.Contains(route, StringComparison.Ordinal), $"{file} still serves a route containing {route}.");
        }

        // The players change only your own bookmarks: no route updates a shared one.
        Assert.DoesNotContain("UpdateGlobal", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditorBookmarkFiles_NeverSayGlobalLibraryBookmarksOrShare()
    {
        var root = FindRepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src/MediaEngine.Web/Components/Details"), "EditorBookmark*")
            .Concat(Directory.GetFiles(Path.Combine(root, "src/MediaEngine.Web/Services/Editing"), "EditorBookmark*.cs"))
            .ToList();
        Assert.NotEmpty(files);

        var banned = new Regex(@"\bGlobal\b|\bShare\b|Library bookmarks", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var file in files)
        {
            var match = banned.Match(File.ReadAllText(file));
            Assert.False(match.Success, $"{Path.GetFileName(file)} says \"{match.Value}\"; the word is Everyone's.");
        }

        var strings = XDocument.Load(Path.Combine(root, "src/MediaEngine.Web/Resources/SharedStrings.resx"));
        var values = strings.Root!.Elements("data")
            .Where(data => ((string?)data.Attribute("name"))?.StartsWith("EditorBookmarks_", StringComparison.Ordinal) == true)
            .Select(data => (Name: (string)data.Attribute("name")!, Value: (string?)data.Element("value") ?? string.Empty))
            .ToList();
        Assert.NotEmpty(values);
        foreach (var (name, value) in values)
        {
            Assert.False(banned.IsMatch(value), $"{name} says \"{value}\"; the word is Everyone's.");
        }
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
