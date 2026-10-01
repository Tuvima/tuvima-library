using MediaEngine.Web.Components.Shared;
using MudBlazor;

namespace MediaEngine.Web.Tests;

public sealed class AppPresentationTests
{
    [Theory]
    [InlineData("Books", AppIcons.Book, "var(--tl-media-books)")]
    [InlineData("Audiobooks", AppIcons.Audiobook, "var(--tl-media-audiobooks)")]
    [InlineData("Movies", AppIcons.Movie, "var(--tl-media-movies)")]
    [InlineData("TV Episodes", AppIcons.Television, "var(--tl-media-tv)")]
    [InlineData("Music", AppIcons.Music, "var(--tl-media-music)")]
    [InlineData("Comics", AppIcons.Comic, "var(--tl-media-comics)")]
    public void MediaPresentation_UsesCanonicalSemanticIconAndAccent(
        string mediaType,
        string expectedIcon,
        string expectedAccent)
    {
        Assert.Equal(expectedIcon, AppMediaPresentation.IconKeyFor(mediaType));
        Assert.Equal(expectedAccent, AppMediaPresentation.AccentFor(mediaType));
    }

    [Theory]
    [InlineData("StorageMaintenanceCompleted", "Maintenance", AppIcons.Maintenance)]
    [InlineData("ReconciliationCompleted", "Metadata", AppIcons.Metadata)]
    [InlineData("FolderCleaned", "Cleanup", AppIcons.Cleanup)]
    [InlineData("ServerStarted", "System", AppIcons.System)]
    [InlineData("FileIngested", "Ingestion", AppIcons.Ingestion)]
    public void ActivityPresentation_ClassifiesOperationalEvents(
        string actionType,
        string expectedLabel,
        string expectedIcon)
    {
        var presentation = AppActivityPresentation.For(actionType);

        Assert.Equal(expectedLabel, presentation.Label);
        Assert.Equal(expectedIcon, presentation.IconKey);
        Assert.False(string.IsNullOrWhiteSpace(presentation.AccentColor));
    }

    [Theory]
    [InlineData("ArtworkWrittenToFile", "metadata", "is-artwork", Icons.Material.Outlined.Image)]
    [InlineData("MetadataManualOverride", "artwork", "is-manual", Icons.Material.Outlined.Notes)]
    [InlineData("IdentityResolved", "metadata", "is-match", Icons.Material.Outlined.Link)]
    [InlineData("FileIngested", "review", "is-file", Icons.Material.Outlined.Description)]
    [InlineData("FileRejected", "metadata", "is-error", Icons.Material.Outlined.ErrorOutline)]
    [InlineData("HydrationEnqueued", "metadata", "is-metadata", Icons.Material.Outlined.Schedule)]
    [InlineData("FileScored", "file", "is-review", Icons.Material.Outlined.FactCheck)]
    [InlineData("ConfidenceScored", "metadata", "is-metadata", Icons.Material.Outlined.Assessment)]
    [InlineData("EntityChainCreated", "file", "is-metadata", Icons.Material.Outlined.AccountTree)]
    [InlineData("FolderCleaned", "metadata", "is-file", Icons.Material.Outlined.DeleteSweep)]
    [InlineData("ServerStarted", "metadata", "is-metadata", Icons.Material.Outlined.PowerSettingsNew)]
    public void HistoryPresentation_PrefersKnownActionOverBroadCategory(
        string eventType,
        string category,
        string expectedTone,
        string expectedIcon)
    {
        var presentation = AppHistoryPresentation.For(eventType, category);

        Assert.Equal(expectedTone, presentation.ToneClass);
        Assert.Equal(expectedIcon, presentation.Icon);
    }

    [Theory]
    [InlineData("artwork", "is-artwork", Icons.Material.Outlined.Image)]
    [InlineData("ingestion", "is-file", Icons.Material.Outlined.Description)]
    [InlineData("manual", "is-manual", Icons.Material.Outlined.Notes)]
    [InlineData("unknown", "is-metadata", Icons.Material.Outlined.EditNote)]
    public void HistoryPresentation_UsesCategoryOnlyForUnknownActions(
        string category,
        string expectedTone,
        string expectedIcon)
    {
        var presentation = AppHistoryPresentation.For("FutureAction", category);

        Assert.Equal(expectedTone, presentation.ToneClass);
        Assert.Equal(expectedIcon, presentation.Icon);
    }

    [Fact]
    public void HistoryPresentation_DoesNotFuzzyMatchUnknownActionNames()
    {
        var presentation = AppHistoryPresentation.For("ArtworkedSomething", null);

        Assert.Equal("is-metadata", presentation.ToneClass);
        Assert.Equal(Icons.Material.Outlined.EditNote, presentation.Icon);
    }
}
