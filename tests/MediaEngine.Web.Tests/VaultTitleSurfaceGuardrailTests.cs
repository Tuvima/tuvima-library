using System.Text.RegularExpressions;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A Vault item never names itself in the browser tab, history, lock screen or media controls (Videos spec D33, WP17 part C).
/// These scans fail when a Vault page sets a title that is not a fixed word, or a Vault component writes the document title or the
/// media session from item data.
/// </summary>
public sealed class VaultTitleSurfaceGuardrailTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string Web(params string[] parts) => Path.Combine([RepoRoot, "src", "MediaEngine.Web", .. parts]);

    private static IEnumerable<string> VaultSources()
    {
        var components = Directory.EnumerateFiles(Web("Components", "Vault"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal));
        var pages = Directory.EnumerateFiles(Web("Components", "Pages"), "Vault*.razor");
        return components.Concat(pages);
    }

    [Fact]
    public void TheVaultVideoPage_SetsTheTitleVideo_AndNothingElse()
    {
        var source = LocalizedSource.ReadAllText(Web("Components", "Pages", "VaultScenePage.razor"));

        var titles = Regex.Matches(source, @"<PageTitle>(?<text>.*?)</PageTitle>", RegexOptions.Singleline).Select(match => match.Groups["text"].Value).ToList();

        Assert.Equal(["Video"], titles);
        Assert.Equal(VaultPrivacy.GenericTitle, titles[0]);
    }

    [Fact]
    public void EveryVaultPage_SetsOnlyAFixedWordAsItsTitle_NeverAnExpressionFromData()
    {
        foreach (var path in VaultSources())
        {
            var source = LocalizedSource.ReadAllText(path);
            foreach (Match match in Regex.Matches(source, @"<PageTitle>(?<text>.*?)</PageTitle>", RegexOptions.Singleline))
            {
                var text = match.Groups["text"].Value;
                Assert.False(text.Contains('@') || text.Contains('{'), $"{Path.GetFileName(path)} sets a computed title: {text}");
            }

            Assert.DoesNotContain("<HeadContent", source);
        }
    }

    [Fact]
    public void NoVaultComponent_WritesTheDocumentTitleOrTheMediaSession()
    {
        foreach (var path in VaultSources())
        {
            var source = LocalizedSource.ReadAllText(path);
            foreach (var forbidden in new[] { "document.title", "mediaSession", "MediaSession", "playback-media-session", "navigator.", "history.replaceState", "history.pushState" })
            {
                Assert.False(source.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetFileName(path)} uses {forbidden}.");
            }
        }
    }

    [Fact]
    public void ThePlayerPage_TakesItsTitleFromTheVaultRule_AndTheMediaSessionIsGenericForAVaultVideo()
    {
        var page = LocalizedSource.ReadAllText(Web("Components", "Pages", "WatchPlayerPage.razor"));
        Assert.Contains("VaultPrivacy.PageTitle(", page);
        Assert.Single(Regex.Matches(page, "<PageTitle>"));
        Assert.Contains("<PageTitle>@PageTitleText - Library</PageTitle>", page);

        var vault = Create(isVault: true);
        var metadata = VideoMediaSessionMetadata.Create(vault, "Secret Show", new Uri("http://localhost/"));
        Assert.Equal(VaultPrivacy.GenericTitle, metadata.Title);
        Assert.Empty(metadata.Artwork);
        Assert.DoesNotContain("Secret", $"{metadata.Title}{metadata.Artist}{metadata.Album}", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VaultPrivacy.GenericTitle, VaultPrivacy.PageTitle(vault, vault.WorkId, "Secret Title"));
    }

    private static ListenQueueItem Create(bool isVault) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        Title = "Secret Title",
        Album = "Secret Album",
        Manifest = new PlaybackManifestDto { IsVault = isVault },
    };
}
