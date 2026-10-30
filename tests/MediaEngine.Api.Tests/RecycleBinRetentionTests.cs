using MediaEngine.Api.Services.Libraries;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class RecycleBinRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-bin-retention-" + Guid.NewGuid().ToString("N"));
    private readonly RecycleBinService _store;
    private readonly string _library;

    public RecycleBinRetentionTests()
    {
        _library = Path.Combine(_root, "library");
        Directory.CreateDirectory(_library);
        _store = new RecycleBinService(Path.Combine(_root, "data"), NullLogger.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private RecycleBinEntry Bin(string name, string? mediaType = "Movies", string? library = "videos")
    {
        var path = Path.Combine(_library, name);
        File.WriteAllText(path, "video bytes");
        return _store.MoveIn(path, Path.GetFileNameWithoutExtension(name), library, mediaType, "Shy");
    }

    private static readonly RecycleBinViewer Admin = new(CanManage: true, CanListItems: true, Requester: "Shy");

    [Fact]
    public void PurgeExpired_RemovesOnlyItemsOlderThanTheRetentionDays()
    {
        var old = Bin("old.mp4");
        var now = DateTimeOffset.UtcNow.AddDays(91);
        var fresh = Bin("fresh.mp4");

        // Both were deleted "now"; moving the clock 91 days on expires both, so keep one fresh by purging at a clock 89 days on.
        var nothing = _store.PurgeExpired(DateTimeOffset.UtcNow.AddDays(89), _ => 90);
        Assert.Equal(0, nothing.Items);
        Assert.Equal(2, _store.List().Count);

        var purged = _store.PurgeExpired(now, _ => 90);

        Assert.Equal(2, purged.Items);
        Assert.Empty(_store.List());
        _ = (old, fresh);
    }

    [Fact]
    public void PurgeExpired_NeverRemovesAnythingWhenRetentionIsZero()
    {
        Bin("a.mp4");

        var purged = _store.PurgeExpired(DateTimeOffset.UtcNow.AddDays(4000), _ => 0);

        Assert.Equal(0, purged.Items);
        Assert.Single(_store.List());
    }

    [Fact]
    public void PurgeExpired_LeavesTheVaultsEntriesForTheVaultsOwnTools()
    {
        Bin("vault.mp4", library: "vault-videos");
        Bin("normal.mp4");

        var purged = _store.PurgeExpired(DateTimeOffset.UtcNow.AddDays(100), _ => 90, new HashSet<string> { "vault-videos" });

        Assert.Equal(1, purged.Items);
        Assert.Equal("vault-videos", Assert.Single(_store.List()).LibraryId);
    }

    [Fact]
    public void Summary_GivesAdministratorsTheNamesAndTheDaysLeft()
    {
        Bin("dune.mp4");

        var summary = RecycleBinSummaryBuilder.Build(_store.List(), DateTimeOffset.UtcNow, _ => 90, 7, null, Admin);

        var item = Assert.Single(summary.Items);
        Assert.Equal("dune", item.Title);
        Assert.Equal("Shy", item.DeletedBy);
        Assert.Equal(90, item.LeavesInDays);
        Assert.True(summary.CanManage);
        Assert.Equal(1, summary.TotalItems);
        Assert.Equal(0, summary.LeavingSoonItems);
    }

    [Fact]
    public void Summary_ForARestrictedProfileCarriesTotalsOnlyWithNoTitleOrKind()
    {
        Bin("secret title.mp4", mediaType: "Videos");
        Bin("another.mp4", mediaType: "Movies");
        var child = new RecycleBinViewer(CanManage: false, CanListItems: false, Requester: "Mia");

        var summary = RecycleBinSummaryBuilder.Build(_store.List(), DateTimeOffset.UtcNow, _ => 90, 7, null, child);

        Assert.Empty(summary.Items);
        Assert.Equal(2, summary.TotalItems);
        Assert.Equal(2, summary.OtherItems);
        Assert.True(summary.OtherBytes > 0);
        Assert.False(summary.CanManage);
        Assert.Equal(0, summary.LeavingSoonItems);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(summary), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_FiltersToTheMediaTypeOfThePageThePersonCameFrom()
    {
        Bin("movie.mp4", mediaType: "Movies");
        Bin("show.mp4", mediaType: "TV");

        var summary = RecycleBinSummaryBuilder.Build(_store.List(), DateTimeOffset.UtcNow, _ => 90, 7, "movies", Admin);

        Assert.Equal("movie", Assert.Single(summary.Items).Title);
        Assert.Equal(1, summary.TotalItems);
    }

    [Fact]
    public void Summary_WarnsAdministratorsAboutItemsLeavingWithinTheWarningDays()
    {
        Bin("soon.mp4");
        var inSixteen = DateTimeOffset.UtcNow.AddDays(84);

        var summary = RecycleBinSummaryBuilder.Build(_store.List(), inSixteen, _ => 90, 7, null, Admin);

        Assert.Equal(1, summary.LeavingSoonItems);
        Assert.InRange(summary.LeavingSoonDays ?? -1, 5, 7);
        var plain = RecycleBinSummaryBuilder.Build(_store.List(), inSixteen, _ => 90, 7, null, Admin with { CanManage = false });
        Assert.Equal(0, plain.LeavingSoonItems);
    }

    [Fact]
    public void AskToKeep_RecordsTheRequesterOnceAndMarksItForThemAndTheAdministrator()
    {
        var entry = Bin("keep.mp4");

        Assert.True(_store.AddKeepRequest(entry.Id, "Mia"));
        Assert.True(_store.AddKeepRequest(entry.Id, "mia"));

        var asMia = RecycleBinSummaryBuilder.Build(_store.List(), DateTimeOffset.UtcNow, _ => 90, 7, null, new RecycleBinViewer(false, true, "Mia"));
        Assert.True(Assert.Single(asMia.Items).Requested);
        Assert.Empty(asMia.Items[0].RequestedBy);
        var asAdmin = RecycleBinSummaryBuilder.Build(_store.List(), DateTimeOffset.UtcNow, _ => 90, 7, null, Admin);
        Assert.Equal(["Mia"], Assert.Single(asAdmin.Items).RequestedBy);
    }

    [Fact]
    public void AskToKeep_RefusesAnItemThatIsGoneOrHidden()
    {
        var entry = Bin("vault.mp4", library: "vault-videos");

        Assert.False(_store.AddKeepRequest(Guid.NewGuid(), "Mia"));
        Assert.False(_store.AddKeepRequest(entry.Id, "Mia", new HashSet<string> { "vault-videos" }));
    }
}
