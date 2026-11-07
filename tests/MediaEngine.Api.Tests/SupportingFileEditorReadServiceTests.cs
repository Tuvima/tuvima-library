using MediaEngine.Api.Services.Extras;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The supporting-file editor's read (universal editor v5 WP7): one file with what a person set and what the folders say, the lists the form
/// chooses from, the facts for Quick facts, and the owner's header with the file marked as the row the editor is open on.
/// </summary>
public sealed class SupportingFileEditorReadServiceTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("The Expanse");

    public void Dispose() => _world.Dispose();

    private SupportingFileEditorReadService Service(bool managed = true, params ExtraScope[] places) => new(
        _world.Extras,
        new PlacesScanner(places),
        new ManagedFolders(_world.Library, managed),
        _world.Header());

    private (Guid Show, Guid Season2, Guid Episode3) SeedExpanse()
    {
        var show = _world.Work("TV", "parent", "The Expanse");
        var season2 = _world.Work("TV", "parent", "Season 2", show, 2);
        var episode3 = _world.Work("TV", "child", "Static", season2, 3);
        _world.File(episode3, "Season 02/The Expanse S02E03.mkv");
        return (show, season2, episode3);
    }

    [Fact]
    public async Task AFileOfTheTitle_IsReadWithItsValues_ItsRolesAndTheFolderItSitsIn()
    {
        var tv = SeedExpanse();
        var id = await _world.SupportingFileAsync("Season 02/Documents/guide.pdf", ExtraScope.ForSeason(2));
        _world.Write("Season 02/Documents/guide.pdf", "pdf");

        var read = await Service().GetAsync(tv.Episode3, id, null);

        Assert.NotNull(read);
        Assert.Equal(tv.Episode3, read.TitleId);
        Assert.Equal(id.ToString("D"), read.File.Id);
        Assert.Equal("guide", read.File.Title);
        Assert.Equal("document", read.Type);
        Assert.Equal("PDF", read.Badge);
        Assert.Equal("application/pdf", read.MimeType);
        Assert.Equal("Season 02 / Documents", read.Folder);
        Assert.False(read.ReadOnly);
        Assert.Contains(read.Roles, role => role.Id == ExtraRoles.Guide);
        Assert.DoesNotContain(read.Roles, role => role.Id == ExtraRoles.Trailer);
        Assert.Equal("Season 2", read.ScopeLabel);
        Assert.Equal("Season 2", read.FolderScopeLabel);
    }

    [Fact]
    public async Task TheOwnersHeader_MarksTheFileAsTheRowTheEditorIsOpenOn_AndTheConfigHasThePreviewTab()
    {
        var tv = SeedExpanse();
        var id = await _world.SupportingFileAsync("Season 02/guide.pdf", ExtraScope.ForSeason(2));

        var read = await Service().GetAsync(tv.Episode3, id, null);

        var files = read!.Header!.Level!.SupportingFiles!;
        Assert.Contains(files.Files, file => file.Id == id && file.IsCurrent);
        Assert.Equal("The Expanse", read.Header.Root.Title);
        Assert.Contains("preview", read.Config.Tabs);
    }

    [Fact]
    public async Task ThePlaces_StartWithTheTitle_ThenSeasonsWithTheirEpisodes_ThenSectionsWithTheirLessons()
    {
        var tv = SeedExpanse();
        var id = await _world.SupportingFileAsync("guide.pdf", ExtraScope.Root);

        var read = await Service(
            true,
            ExtraScope.ForFile("02 Components/01 Intro.mkv"),
            ExtraScope.ForEpisode(2, 3),
            ExtraScope.ForFolder("02 Components"),
            ExtraScope.ForSeason(2),
            ExtraScope.ForSeason(1)).GetAsync(tv.Episode3, id, null);

        var places = read!.Places;
        Assert.Equal(
            ["root", "season:1", "season:2", "episode:2:3", "folder:02 Components", "file:02 Components/01 Intro.mkv"],
            places.Select(place => place.Key));
        Assert.Equal("The Expanse", places[0].Label);
        Assert.Equal("Season 2, episode 3", places.Single(place => place.Key == "episode:2:3").Label);
        Assert.Equal("season:2", places.Single(place => place.Key == "episode:2:3").ParentKey);
        Assert.Equal("02 · Components", places.Single(place => place.Key == "folder:02 Components").Label);
        Assert.Equal("folder:02 Components", places.Single(place => place.Level == "file").ParentKey);
        Assert.Null(places[0].ParentKey);
    }

    [Fact]
    public async Task AFileSetToAnotherPlaceAndShownAlongside_ReadsBothThePlaceItHasAndTheOneItsFolderGives()
    {
        var tv = SeedExpanse();
        var id = await _world.SupportingFileAsync("Season 02/recap.pdf", ExtraScope.ForSeason(2), alongside: ExtraScope.ForEpisode(2, 3));
        await _world.Extras.UpdateOwnedManyAsync([id], new ExtraOwnedUpdate { Scope = ExtraValue<ExtraScope>.Set(ExtraScope.Root) });

        var read = await Service().GetAsync(tv.Episode3, id, null);

        Assert.Equal("The Expanse", read!.ScopeLabel);
        Assert.Equal("Season 2", read.FolderScopeLabel);
        Assert.Equal("with episode 3", read.AlongsideLabel);
        Assert.Equal("you", read.File.ScopeSource);
    }

    [Fact]
    public async Task TheGroupsOfTheTitle_AreListedForTheGroupField()
    {
        var tv = SeedExpanse();
        var id = await _world.SupportingFileAsync("guide.pdf", ExtraScope.Root);
        await _world.Extras.UpdateOwnedManyAsync([id], new ExtraOwnedUpdate { GroupName = ExtraValue<string>.Set("Character guides") });

        var read = await Service().GetAsync(tv.Episode3, id, null);

        Assert.Equal(["Character guides"], read!.Groups);
        Assert.Equal("Character guides", read.File.Group);
    }

    [Fact]
    public async Task AFileInAReadOnlyFolder_IsReadAsReadOnly()
    {
        var tv = SeedExpanse();
        var id = await _world.SupportingFileAsync("guide.pdf", ExtraScope.Root);

        var managed = await Service(managed: true).GetAsync(tv.Episode3, id, null);
        var readOnly = await Service(managed: false).GetAsync(tv.Episode3, id, null);

        Assert.False(managed!.ReadOnly);
        Assert.True(readOnly!.ReadOnly);
    }

    [Fact]
    public async Task AFolderOfPictures_IsOneFile_WithTheSizeAndCountOfTheWholeFolder()
    {
        var tv = SeedExpanse();
        var first = await _world.SupportingFileAsync("Artwork/one.jpg", ExtraScope.Root, ItemExtraKinds.Picture, ExtraRoles.Pictures);
        await _world.SupportingFileAsync("Artwork/two.jpg", ExtraScope.Root, ItemExtraKinds.Picture, ExtraRoles.Pictures);
        var rows = await _world.Extras.ListAllInItemFolderAsync(_world.TitleFolder);
        await _world.Extras.SyncItemFolderAsync(
            _world.TitleFolder, [.. rows.Select(row => row with { GroupKey = "Artwork" })], []);

        var read = await Service().GetAsync(tv.Episode3, first, null);

        Assert.Equal("Pictures", read!.TypeLabel);
        Assert.Equal(2, read.File.ItemCount);
        Assert.Equal(200, read.File.SizeBytes);
    }

    [Fact]
    public async Task ASiblingsFileInASeries_IsNotReadThroughAnotherBook()
    {
        var series = _world.Work("Books", "parent", "Dune Chronicles");
        var firstBook = _world.Work("Books", "child", "Dune", series, 1);
        var secondBook = _world.Work("Books", "child", "Dune Messiah", series, 2);
        var firstFolder = _world.AddTitleFolder("Dune");
        var secondFolder = _world.AddTitleFolder("Dune Messiah");
        _world.File(firstBook, "Dune.epub", folder: firstFolder);
        _world.File(secondBook, "Dune Messiah.epub", folder: secondFolder);
        var siblings = await _world.SupportingFileAsync("Map of Arrakis.pdf", ExtraScope.Root, folder: firstFolder);

        var service = Service();

        Assert.Null(await service.GetAsync(secondBook, siblings, null));
        Assert.Null(await service.GetAsync(secondBook, Guid.NewGuid(), null));
        Assert.NotNull(await service.GetAsync(firstBook, siblings, null));
    }

    private sealed class PlacesScanner(IReadOnlyList<ExtraScope> places) : IItemExtrasScanner
    {
        public Task<IReadOnlyList<ExtraScope>> ListPlacesAsync(string itemFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExtraScope>>([.. places]);

        public Task<ExtraTitleFolder?> ResolveTitleFolderAsync(string titleFilePath, CancellationToken ct = default) => Task.FromResult<ExtraTitleFolder?>(null);

        public Task<ItemExtra?> DescribeAsync(string filePath, string origin, ExtraDecision decision, CancellationToken ct = default) =>
            Task.FromResult<ItemExtra?>(null);

        public Task<ItemExtra?> DescribeAsync(string filePath, string origin, int? targetSeason, int? targetEpisode, CancellationToken ct = default) =>
            Task.FromResult<ItemExtra?>(null);

        public Task<bool> SyncItemFolderAsync(string itemFolder, CancellationToken ct = default) => Task.FromResult(true);

        public Task<string?> ResolveScopeFolderAsync(string itemFolder, ExtraScope scope, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<ExtraEpisodePlacement?> GetEpisodePlacementAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<ExtraEpisodePlacement?>(null);

        public Task<bool> ClaimsFileAsync(string filePath, LibraryFolderEntry? library, CancellationToken ct = default) => Task.FromResult(false);

        public void NoteFile(string filePath)
        {
        }

        public Task<int> SyncQueuedAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> SyncAllAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class ManagedFolders(string root, bool managed) : ILibraryFolderResolver
    {
        public LibraryFolderEntry? ResolveById(string libraryId) => null;

        public LibraryFolderEntry? ResolveForPath(string absolutePath) => ResolveSourceForPath(absolutePath)?.Library;

        public string? ResolveSourcePath(string absolutePath) => ResolveSourceForPath(absolutePath)?.Source.Path;

        public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath) =>
            Path.GetFullPath(absolutePath).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                ? new ResolvedLibrarySource(
                    new LibraryFolderEntry { Id = "lib" },
                    new LibrarySourceEntry
                    {
                        Id = "source",
                        Path = root,
                        ManagementMode = managed ? LibrarySourceManagementModes.ManagedByTuvima : LibrarySourceManagementModes.ExistingLibrary,
                        AccessMode = managed ? LibrarySourceAccessModes.Writable : LibrarySourceAccessModes.ReadOnly,
                    })
                : null;
    }
}
