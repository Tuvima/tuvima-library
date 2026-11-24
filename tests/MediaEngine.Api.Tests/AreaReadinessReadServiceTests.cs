using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>Honest waiting messages WP1: the area readiness summary picks the true reason on a real temp data store.</summary>
public sealed class AreaReadinessReadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_area_readiness_{Guid.NewGuid():N}");
    private readonly Guid _films = Guid.NewGuid();
    private readonly Guid _family = Guid.NewGuid();
    private readonly Guid _vault = Guid.NewGuid();
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;

    public AreaReadinessReadServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(FilmsPath);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        WriteLibraries(
            Library(_films, "Films", "watch", "Movies", FilmsPath),
            Library(_family, "Family", "watch", "Videos", Path.Combine(_root, "family")),
            Library(_vault, "Private", "watch", "Videos", Path.Combine(_root, "private"), vault: true));
        Directory.CreateDirectory(Path.Combine(_root, "family"));
        FinishSetup();
    }

    private string FilmsPath => Path.Combine(_root, "films");

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed is cleaned up by the operating system later.
        }
    }

    [Fact]
    public async Task UnfinishedSetup_IsTheReason_EvenWhenFilesAreWaiting()
    {
        _database.CreateConnection().Execute("UPDATE onboarding_workflows SET state = 'in_progress';");
        AddFile(_films, "Queued", "UnavailableCapability");

        var result = await Service().GetAdministratorAsync("watch");

        Assert.Equal(AreaReadinessReasons.SetupUnfinished, result.Reason);
        Assert.False(result.SetupFinished);
    }

    [Fact]
    public async Task AreaWithNoLibrary_SaysNoLibrary()
    {
        var result = await Service().GetAdministratorAsync("read");

        Assert.Equal(AreaReadinessReasons.NoLibrary, result.Reason);
        Assert.Equal(0, result.LibraryCount);
    }

    [Fact]
    public async Task MissingFolder_SaysFolderUnreachable_AndHealthyFoldersNeverDo()
    {
        Assert.NotEqual(AreaReadinessReasons.FolderUnreachable, (await Service().GetAdministratorAsync("watch")).Reason);

        Directory.Delete(FilmsPath);
        var result = await Service().GetAdministratorAsync("watch");

        Assert.Equal(AreaReadinessReasons.FolderUnreachable, result.Reason);
        Assert.Equal(["Films"], result.UnreachableFolders);
    }

    [Fact]
    public async Task FilesWithoutAMetadataSource_AreCountedAsWaiting()
    {
        for (var i = 0; i < 3; i++)
        {
            AddFile(_films, "Queued", "UnavailableCapability");
        }

        var result = await Service().GetAdministratorAsync("watch");

        Assert.Equal(AreaReadinessReasons.WaitingForSource, result.Reason);
        Assert.Equal(3, result.FilesFound);
        Assert.Equal(3, result.FilesWaitingForSource);
        Assert.Equal(0, result.FilesInProgress);
    }

    [Fact]
    public async Task FilesStillBeingWorkedOn_WinOverWaitingFiles()
    {
        AddFile(_films, "Queued", "UnavailableCapability");
        AddFile(_films, "Hydrating", null);
        AddFile(_films, null, null);

        var result = await Service().GetAdministratorAsync("watch");

        Assert.Equal(AreaReadinessReasons.StillLooking, result.Reason);
        Assert.Equal(2, result.FilesInProgress);
        Assert.Equal(1, result.FilesWaitingForSource);
    }

    [Fact]
    public async Task FilesWithAPendingReview_AreCountedInReview_AndAgreeWithNeedsReview()
    {
        var asset = AddFile(_films, "RetailNoMatch", null);
        AddFile(_films, "Ready", null);
        _database.CreateConnection().Execute(
            "INSERT INTO review_queue(id, entity_id, entity_type, trigger, status) VALUES (@id, @entity, 'MediaAsset', 'LowConfidence', 'Pending');",
            new { id = Guid.NewGuid(), entity = asset });

        var result = await Service().GetAdministratorAsync("watch");

        Assert.Equal(AreaReadinessReasons.WaitingInReview, result.Reason);
        Assert.Equal(1, result.FilesInReview);
        Assert.Equal(1, result.FilesReady);
        var pending = _database.CreateConnection().ExecuteScalar<int>("SELECT COUNT(*) FROM review_queue WHERE status = 'Pending';");
        Assert.Equal(pending, result.FilesInReview);
    }

    [Fact]
    public async Task AFolderWithNoFiles_SaysNothingFound()
    {
        var result = await Service().GetAdministratorAsync("watch");

        Assert.Equal(AreaReadinessReasons.NothingFound, result.Reason);
    }

    [Fact]
    public async Task ReadyPersonalVideos_AreListedByMediaType_AndVaultFilesNeverCount()
    {
        AddFile(_films, "Queued", "UnavailableCapability");
        AddFile(_family, "Ready", null);
        AddFile(_family, "Ready", null);
        _database.CreateConnection().Execute(
            "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
            new { vault = _vault.ToString("D"), now = DateTimeOffset.UtcNow.ToString("O") });
        AddFile(_vault, "Ready", null);

        var result = await Service().GetAdministratorAsync("watch");

        Assert.Equal(3, result.FilesFound);
        Assert.Equal(2, result.MediaTypes.Single(type => type.MediaType == "Videos").FilesReady);
        Assert.Equal(1, result.MediaTypes.Single(type => type.MediaType == "Movies").FilesWaitingForSource);
        Assert.DoesNotContain(result.MediaTypes, type => type.FilesFound == 0);
    }

    [Fact]
    public async Task Home_CountsEveryAreaAtOnce()
    {
        AddFile(_films, "Queued", "UnavailableCapability");

        var result = await Service().GetAdministratorAsync("home");

        Assert.Equal(AreaReadinessReasons.WaitingForSource, result.Reason);
        Assert.Equal(2, result.LibraryCount);
    }

    [Theory]
    [InlineData(false, AreaReadinessAudiences.Member)]
    [InlineData(true, AreaReadinessAudiences.Kids)]
    public void AMemberOrKidsProfile_GetsNoCountsNamesOrFlags(bool kids, string audience)
    {
        var result = AreaReadinessReadService.ForNonAdministrator("watch", kids);

        Assert.Equal(audience, result.Audience);
        Assert.Equal(AreaReadinessReasons.Hidden, result.Reason);
        Assert.Null(result.SetupFinished);
        Assert.Equal(0, result.FilesFound);
        Assert.Empty(result.UnreachableFolders);
        Assert.Empty(result.MediaTypes);
    }

    [Fact]
    public void Reasons_FollowTheOrderOfSpecD1()
    {
        // setup beats everything; then no library; then folder; then still looking; then waiting for a source; then review.
        Assert.Equal(AreaReadinessReasons.SetupUnfinished, AreaReadinessReadService.PickReason(false, 0, 1, 5, 5, 5, 5, 5));
        Assert.Equal(AreaReadinessReasons.NoLibrary, AreaReadinessReadService.PickReason(true, 0, 1, 5, 5, 5, 5, 5));
        Assert.Equal(AreaReadinessReasons.FolderUnreachable, AreaReadinessReadService.PickReason(true, 1, 1, 5, 5, 5, 5, 5));
        Assert.Equal(AreaReadinessReasons.StillLooking, AreaReadinessReadService.PickReason(true, 1, 0, 5, 5, 5, 5, 0));
        Assert.Equal(AreaReadinessReasons.StillLooking, AreaReadinessReadService.PickReason(true, 1, 0, 0, 0, 0, 0, 3));
        Assert.Equal(AreaReadinessReasons.WaitingForSource, AreaReadinessReadService.PickReason(true, 1, 0, 5, 0, 5, 5, 0));
        Assert.Equal(AreaReadinessReasons.WaitingInReview, AreaReadinessReadService.PickReason(true, 1, 0, 5, 0, 0, 5, 0));
        Assert.Equal(AreaReadinessReasons.NothingFound, AreaReadinessReadService.PickReason(true, 1, 0, 0, 0, 0, 0, 0));
        Assert.Equal(AreaReadinessReasons.Ready, AreaReadinessReadService.PickReason(true, 1, 0, 2, 0, 0, 0, 0));
    }

    private AreaReadinessReadService Service() =>
        new(_configuration, new OnboardingRepository(_database), new AreaReadinessReadRepository(_database));

    private void FinishSetup() =>
        _database.CreateConnection().Execute("UPDATE onboarding_workflows SET state = 'complete';");

    private Guid AddFile(Guid library, string? state, string? outcome)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@work, 'Movies', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, @library);
            """,
            new { work, edition, asset, path = $"/media/{asset:N}.mkv", library = library.ToString("D"), hash = "hash-" + asset.ToString("N") });
        if (state is not null)
        {
            connection.Execute(
                """
                INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, last_outcome_category)
                VALUES (@id, @asset, 'MediaAsset', 'Movies', @state, @outcome);
                """,
                new { id = Guid.NewGuid(), asset, state, outcome });
        }

        return asset;
    }

    private void WriteLibraries(params LibraryFolderConfig[] libraries) =>
        File.WriteAllText(Path.Combine(_root, "config", "libraries.json"), JsonSerializer.Serialize(new LibrariesConfiguration
        {
            Libraries = [.. libraries],
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = _root, AllowWrite = true }],
        }));

    private static LibraryFolderConfig Library(Guid id, string name, string area, string mediaType, string path, bool vault = false) => new()
    {
        Id = id.ToString("D"),
        Name = name,
        Category = mediaType,
        MetadataPolicy = mediaType == "Videos" ? LibraryMetadataPolicies.LocalOnly : LibraryMetadataPolicies.Enriched,
        Kind = LibraryKinds.Catalogued,
        Area = area,
        MediaTypes = [mediaType],
        IsVault = vault,
        Sources = [new LibrarySourceConfig { Id = Guid.NewGuid().ToString("D"), Path = path }],
    };
}
