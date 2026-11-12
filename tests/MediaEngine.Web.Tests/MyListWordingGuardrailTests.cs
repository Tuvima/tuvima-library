using System.Xml.Linq;

namespace MediaEngine.Web.Tests;

/// <summary>
/// My List spec D8 and D14: saving a title has one icon (a plus, a tick once saved; the heart is only a song's Favorite) and one set of
/// words. The action is "Add to My List" or "Remove from My List" and the place is "My List"; the older variants must not come back.
/// </summary>
public sealed class MyListWordingGuardrailTests
{
    private static readonly string[] RetiredWording =
        ["In My List", "Save to My List", "Saved to My List", "Added to My List", "My List heart", "My list", "Watchlist"];

    [Fact]
    public void TheStringTableHasNoRetiredMyListWording()
    {
        // Whole labels, matched with their capitals, so a sentence such as "cannot be added to My List yet" is not mistaken for one.
        var resources = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Resources");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(resources, "SharedStrings*.resx"))
        {
            offenders.AddRange(XDocument.Load(file).Root!.Elements("data")
                .Select(data => (Key: (string)data.Attribute("name")!, Value: (string?)data.Element("value") ?? string.Empty))
                .Where(entry => RetiredWording.Any(word => entry.Value.Contains(word, StringComparison.Ordinal)))
                .Select(entry => $"{Path.GetFileName(file)}: {entry.Key} (\"{entry.Value}\")"));
        }

        Assert.True(offenders.Count == 0,
            "My List has three labels only (Add to My List, Remove from My List, My List; spec D14): " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheMyListActionLabelsAreTheThreeOfTheSpec()
    {
        var english = XDocument.Load(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Resources", "SharedStrings.resx")).Root!
            .Elements("data").ToDictionary(data => (string)data.Attribute("name")!, data => (string?)data.Element("value") ?? string.Empty);

        Assert.Equal("Add to My List", english["MediaTile_AddToMyList"]);
        Assert.Equal("Remove from My List", english["MediaTile_RemoveFromMyList"]);
        Assert.Equal("My List", english["TopBar_MyList"]);
        Assert.Equal("My List", english["Nav_MyList"]);
        Assert.Equal("My List", english["MyList_Title"]);
    }

    [Fact]
    public void ThePlayersDrawAHeartOnlyForASong()
    {
        // The one place the players choose the save icon: music keeps Favorite, every other media shows Add or Check.
        var actions = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Player", "PlaybackIdentityActions.razor"));

        Assert.Contains("Favorited ? PlaybackUtilityGlyphKind.Check : PlaybackUtilityGlyphKind.Add", actions, StringComparison.Ordinal);
        Assert.Equal(1, actions.Split("PlaybackUtilityGlyphKind.Favorite").Length - 1);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
