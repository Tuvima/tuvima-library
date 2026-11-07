using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Extras;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Supporting files in the main search (universal editor v5 item 18, D4): a "Supporting files" section found by title, description and
/// file name that follows the visibility of the title each file belongs to. A Kids profile never learns of a file in a title it may not
/// open, a Vault title in particular; a paired app has no section at all.
/// </summary>
public sealed class SupportingFileSearchTests : IDisposable
{
    private readonly SupportingFileTestWorld _world = new();
    private readonly string _vaultFolder;
    private readonly DisplayWorkRow _show;
    private readonly DisplayWorkRow _vaultTitle;

    public SupportingFileSearchTests()
    {
        _vaultFolder = Path.Combine(_world.Root, "Vault Show");
        _show = Seed("The Last Signal", Path.Combine(_world.Show, "Pilot.mkv"), "TV");
        _vaultTitle = Seed("Vault Show", Path.Combine(_vaultFolder, "Secret.mkv"), "Movies");
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task Search_FindsAFileByTitle_Description_AndFileName_WithItsOwnerAndPlace()
    {
        var guide = await _world.SeedDiskFileAsync("character-guide.pdf", "Lighthouse guide");
        var notes = await _world.SeedDiskFileAsync("notes.pdf", "Notes", ExtraScope.ForSeason(2), _world.SeasonFolder(2));
        await _world.Extras.UpdateOwnedAsync(notes.Id, new ExtraOwnedUpdate { Description = ExtraValue<string>.Set("The keeper's timeline") });
        var service = CreateService(Human(admin: true), _show);

        var byTitle = await service.SearchAsync("lighthouse", 20, CancellationToken.None);
        var byDescription = await service.SearchAsync("TIMELINE", 20, CancellationToken.None);
        var byFileName = await service.SearchAsync("character-guide", 20, CancellationToken.None);

        var hit = Assert.Single(FilesIn(byTitle));
        Assert.Equal(guide.Id, hit.Id);
        Assert.Equal("Lighthouse guide", hit.Title);
        Assert.Equal(_show.RootWorkId, hit.SupportingFile!.OwnerId);
        Assert.Equal("The Last Signal", hit.SupportingFile.OwnerName);
        Assert.Equal("document", hit.SupportingFile.Type);
        Assert.Equal("character-guide.pdf", hit.SupportingFile.FileName);
        Assert.Equal(string.Empty, hit.SupportingFile.ScopeLabel);
        Assert.Equal($"/details/tvshow/{_show.RootWorkId:D}?context=watch", hit.DetailRoute);

        var seasonHit = Assert.Single(FilesIn(byDescription));
        Assert.Equal(notes.Id, seasonHit.Id);
        Assert.Equal("Season 2", seasonHit.SupportingFile!.ScopeLabel);
        Assert.Equal(guide.Id, Assert.Single(FilesIn(byFileName)).Id);
    }

    [Fact]
    public async Task Search_ForAKidsProfile_NeverShowsAFileOfAVaultTitle()
    {
        await _world.SeedDiskFileAsync("show-guide.pdf", "Lighthouse guide");
        var vaultExtra = _world.Row(Path.Combine(_vaultFolder, "secret-notes.pdf"), ItemExtraKinds.Document, "Lighthouse secret notes", ExtraScope.Root) with { ItemFolder = _vaultFolder };
        await _world.Extras.SyncItemFolderAsync(_vaultFolder, [vaultExtra], []);

        // The projection hands a Kids profile only the titles it may open: the Vault title is not among them.
        var kids = CreateService(Human(restricted: true), _show);
        // A signed-in administrator in an unlocked Vault session sees both titles.
        var unlocked = CreateService(Human(admin: true), _show, _vaultTitle);

        var forKids = await kids.SearchAsync("lighthouse", 20, CancellationToken.None);
        var forUnlocked = await unlocked.SearchAsync("lighthouse", 20, CancellationToken.None);

        Assert.Equal(["Lighthouse guide"], FilesIn(forKids).Select(hit => hit.Title));
        Assert.DoesNotContain(forKids.Sections.SelectMany(section => section.Results), result => result.Title.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Lighthouse guide", "Lighthouse secret notes"], FilesIn(forUnlocked).Select(hit => hit.Title).Order());
        Assert.Equal(_vaultTitle.WorkId, FilesIn(forUnlocked).Single(hit => hit.Title == "Lighthouse secret notes").SupportingFile!.OwnerId);
    }

    [Fact]
    public async Task Search_ForAPairedApp_HasNoSupportingFilesSection()
    {
        await _world.SeedDiskFileAsync("show-guide.pdf", "Lighthouse guide");
        var app = CreateService(new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(),
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true), _show);

        var response = await app.SearchAsync("lighthouse", 20, CancellationToken.None);

        Assert.DoesNotContain(response.Sections, section => section.Key == UniversalSearchReadService.SupportingFilesKey);
    }

    [Fact]
    public async Task Search_CountsOnlyTitles_NotSupportingFiles_InTheResponseTotal()
    {
        await _world.SeedDiskFileAsync("guide.pdf", "Pilot guide");
        var service = CreateService(Human(admin: true), _show);

        var response = await service.SearchAsync("pilot", 20, CancellationToken.None);

        Assert.Single(FilesIn(response));
        Assert.Equal(response.Sections.Where(section => section.Key != UniversalSearchReadService.SupportingFilesKey).Sum(section => section.TotalCount), response.TotalCount);
    }

    private static IReadOnlyList<UniversalSearchResultDto> FilesIn(UniversalSearchResponseDto response) =>
        response.Sections.Where(section => section.Key == UniversalSearchReadService.SupportingFilesKey).SelectMany(section => section.Results).ToList();

    private static RequestAuthority Human(bool admin = false, bool restricted = false) => new(
        PrincipalKind.Human, true, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), null,
        AccountEnabled: true, ActiveProfileInUserHousehold: true,
        ActiveProfileIsServerAdministrator: admin, ActiveProfileIsRestricted: restricted);

    private UniversalSearchReadService CreateService(RequestAuthority authority, params DisplayWorkRow[] visible)
    {
        var display = new StubDisplayProjection(visible);
        return new UniversalSearchReadService(
            _world.Database,
            display,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new TestViewAuthorityResolver(authority),
            SpoilerTestKit.Create(display, _world.Database),
            new ProfileRepository(_world.Database),
            new WaitingPersonalStateRepository(_world.Database),
            supportingFiles: new SupportingFileSearchService(_world.Extras, new MediaAssetRepository(_world.Database)));
    }

    // A work with one media file, stored so search can tell which title a folder belongs to.
    private DisplayWorkRow Seed(string title, string filePath, string mediaType)
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var rootId = mediaType == "TV" ? Guid.NewGuid() : Guid.Empty;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var connection = _world.Database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@work, @mediaType)", new { work = GuidSql.ToBlob(workId), mediaType });
        var edition = GuidSql.ToBlob(Guid.NewGuid());
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work)", new { edition, work = GuidSql.ToBlob(workId) });
        connection.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@asset, @edition, @hash, @path)",
            new { asset = GuidSql.ToBlob(assetId), edition, hash = $"hash-{assetId:N}", path = filePath });
        return new DisplayWorkRow
        {
            WorkId = workId,
            AssetId = assetId,
            RootWorkId = rootId,
            MediaType = mediaType,
            Title = title,
            ShowName = mediaType == "TV" ? title : null,
        };
    }

    private sealed class StubDisplayProjection(IReadOnlyList<DisplayWorkRow> rows) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(rows);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);

        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
