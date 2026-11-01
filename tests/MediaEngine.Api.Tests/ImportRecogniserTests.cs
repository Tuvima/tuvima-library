using System.IO.Compression;
using System.Text;
using MediaEngine.Api.Services.Collections.ListImport;
using MediaEngine.Api.Services.Import;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Import;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The shared Import (Collections and lists spec D17): one dialog, one recogniser. A bookmark pack, a pack file and a Goodreads
/// list each open the preview in their own words, an unknown file gets the standard sentence, and a later registration of a kind
/// replaces the placeholder (how the packs packet fills in the pack file kind).
/// </summary>
public sealed class ImportRecogniserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_import_recogniser_{Guid.NewGuid():N}");
    private readonly ConfigurationDirectoryLoader _config;
    private readonly Guid _dune = Guid.NewGuid();
    private readonly Candidates _candidates;

    public ImportRecogniserTests()
    {
        Directory.CreateDirectory(_root);
        _config = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _candidates = new Candidates(
            new ListImportCandidate(_dune, "Books", "Dune", 1965, "Frank Herbert", new Dictionary<string, string> { ["isbn"] = "9780441172719" }),
            new ListImportCandidate(Guid.NewGuid(), "Movies", "Heat", 1995, "Michael Mann", new Dictionary<string, string> { ["imdb_id"] = "tt0113277" }));
    }

    [Fact]
    public async Task ABookmarkPack_OpensTheBookmarkPackPreviewAndNamesItsTitle()
    {
        var pack = BookmarkPackArchive.Write(
            new BookmarkPackContent(
                "Best scenes",
                "Someone",
                new BookmarkPackTitle(new Dictionary<string, string> { ["imdb_id"] = "tt0113277" }, 10200, null),
                [new BookmarkPackBookmark(60, null, null, null, "Opening", null), new BookmarkPackBookmark(120, null, null, null, null, null)],
                "hash"),
            DateTimeOffset.UtcNow);

        var preview = await Recogniser().RecogniseAsync(new ImportSource("scenes.zip", pack), Context(), CancellationToken.None);

        Assert.Equal(ImportKinds.BookmarkPack, preview.Kind);
        Assert.True(preview.Recognised);
        Assert.Equal("Best scenes", preview.Heading);
        Assert.Equal(2, preview.BookmarkPack!.BookmarkCount);
        Assert.Equal("Heat", preview.BookmarkPack.WorkTitle);
        Assert.False(preview.CanApply);
    }

    [Fact]
    public async Task APackFile_OpensThePackPreviewWithoutCreatingAnything()
    {
        var file = Encoding.UTF8.GetBytes("""{"format":"tuvima-collection-pack","name":"Film classics","collections":[{},{}]}""");

        var preview = await Recogniser().RecogniseAsync(new ImportSource("classics.json", file), Context(), CancellationToken.None);

        Assert.Equal(ImportKinds.PackFile, preview.Kind);
        Assert.Equal("Film classics", preview.Heading);
        Assert.Contains(preview.Facts, fact => fact.Label == "Collections" && fact.Value == "2");
    }

    [Fact]
    public async Task AGoodreadsCsv_OpensTheListPreviewWithItsShelves()
    {
        var csv = "Book Id,Title,Author,ISBN,ISBN13,My Rating,Year Published,Exclusive Shelf,Bookshelves\r\n"
            + "1,Dune,Frank Herbert,=\"0441172717\",=\"9780441172719\",5,1965,to-read,\r\n"
            + "2,Snow Crash,Neal Stephenson,=\"\",=\"\",3,1992,read,\r\n";

        var preview = await Recogniser().RecogniseAsync(new ImportSource("goodreads_library_export.csv", Encoding.UTF8.GetBytes(csv)), Context(), CancellationToken.None);

        Assert.Equal(ImportKinds.CollectionList, preview.Kind);
        Assert.Equal("Goodreads export", preview.List!.SourceLabel);
        Assert.Equal(1, preview.List.OwnedCount);
        Assert.Equal(2, preview.List.Shelves.Count);
        Assert.Equal(ImportPrivacy.FilesStayOnThisMachine, preview.PrivacyNote);
    }

    [Fact]
    public async Task AFileThatIsNoKnownKind_GetsTheStandardSentence()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("notes.bin").Open());
            writer.Write("nothing useful");
        }

        var preview = await Recogniser().RecogniseAsync(new ImportSource("mystery.zip", zip.ToArray()), Context(), CancellationToken.None);

        Assert.False(preview.Recognised);
        Assert.Equal(ListFileNotUnderstoodException.StandardMessage, preview.Message);
    }

    [Fact]
    public async Task ALaterRegistrationOfAKind_ReplacesThePlaceholder()
    {
        var recogniser = new ImportRecogniser([.. Handlers(), new RealPackHandler()]);
        var file = Encoding.UTF8.GetBytes("""{"format":"tuvima-collection-pack","name":"Film classics"}""");

        var preview = await recogniser.RecogniseAsync(new ImportSource("classics.json", file), Context(), CancellationToken.None);

        Assert.Equal("Real pack", preview.Heading);
        Assert.Equal(ImportKinds.PackFile, Assert.Single(recogniser.Kinds, kind => kind == ImportKinds.PackFile));
    }

    [Fact]
    public async Task APluginSettingsFile_ForAnUninstalledPlugin_CannotBeApplied()
    {
        var file = Encoding.UTF8.GetBytes(PluginSettingsImportKindHandler.Write("not.installed", new Dictionary<string, System.Text.Json.JsonElement>()));

        var preview = await Recogniser().RecogniseAsync(new ImportSource("settings.json", file), Context(), CancellationToken.None);
        var applied = await Recogniser().ApplyAsync(ImportKinds.PluginSettings, new ImportSource("settings.json", file), Context(), CancellationToken.None);

        Assert.Equal(ImportKinds.PluginSettings, preview.Kind);
        Assert.False(preview.CanApply);
        Assert.False(applied.Applied);
    }

    [Fact]
    public async Task ApplyingAsAKindTheFileIsNot_IsRefused()
    {
        var file = Encoding.UTF8.GetBytes("Heat (1995)\r\n");

        var result = await Recogniser().ApplyAsync(ImportKinds.PluginSettings, new ImportSource("heat.txt", file), Context(), CancellationToken.None);

        Assert.False(result.Applied);
    }

    private static ImportContext Context() => new(null, null);

    private ImportRecogniser Recogniser() => new(Handlers());

    private List<IImportKindHandler> Handlers()
    {
        var fuzzy = new FuzzyMatchingService();
        var loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        var plugins = new PluginCatalog([], new PluginSettingsService(loader), loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        var lists = new ListImportService(null!, _candidates, fuzzy, _config, new LookupStub());
        return
        [
            new BookmarkPackImportKindHandler(_candidates, fuzzy, _config),
            new PackFileImportKindHandler(),
            new PluginSettingsImportKindHandler(plugins),
            new CollectionListImportKindHandler(lists),
        ];
    }

    public void Dispose()
    {
        _config.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is cleaned up by the operating system later.
        }
    }

    private sealed class RealPackHandler : IImportKindHandler
    {
        public string Kind => ImportKinds.PackFile;

        public int Priority => 20;

        public bool Detect(ImportSource source) => source.Text.Contains("tuvima-collection-pack", StringComparison.Ordinal);

        public Task<ImportPreviewDto> PreviewAsync(ImportSource source, ImportContext context, CancellationToken ct) =>
            Task.FromResult(new ImportPreviewDto { Kind = Kind, Recognised = true, Heading = "Real pack" });

        public Task<ImportApplyResultDto> ApplyAsync(ImportSource source, ImportContext context, CancellationToken ct) =>
            Task.FromResult(new ImportApplyResultDto(true, "Added."));
    }

    private sealed class Candidates(params ListImportCandidate[] all) : IListImportCandidateRepository
    {
        public Task<IReadOnlyList<ListImportCandidate>> GetCandidatesAsync(IReadOnlySet<Guid>? allowedWorkIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ListImportCandidate>>(all);
    }

    private sealed class LookupStub : ICollectionMediaLookupReadService
    {
        public Task<List<CollectionMediaLookupDto>> LookupAsync(
            string? query, string? mediaTypes, IReadOnlySet<Guid> existingWorkIds, IReadOnlySet<Guid> allowedWorkIds,
            int? offset, int? limit, CancellationToken ct) => Task.FromResult(new List<CollectionMediaLookupDto>());

        public Task<List<CollectionItemDto>> ResolveItemsAsync(
            Guid collectionId, IReadOnlyList<CollectionItem> items, CancellationToken ct, IReadOnlySet<Guid>? allowedWorkIds = null) =>
            Task.FromResult(new List<CollectionItemDto>());

        public Task<List<CollectionResolvedItemDto>> ResolveMetadataAsync(IReadOnlyList<Guid> workIds, CancellationToken ct) =>
            Task.FromResult(new List<CollectionResolvedItemDto>());

        public Task<Dictionary<string, int>> CountMediaTypesAsync(IReadOnlyList<Guid> workIds, CancellationToken ct) =>
            Task.FromResult(new Dictionary<string, int>());
    }
}
