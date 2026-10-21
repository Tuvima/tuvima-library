using Dapper;
using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>"Rename files to match this order": Managed folders only, through the shared file mover, inside the folder each file is in.</summary>
public sealed class VideoOrderRenamerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_video_rename_{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_video_rename_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly MediaAssetRepository _assets;

    public VideoOrderRenamerTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _assets = new MediaAssetRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
        Directory.Delete(_root, recursive: true);
    }

    private VideoOrderRenamer Renamer(string managementMode, string accessMode)
    {
        var options = new IngestionOptions
        {
            LibraryFolders =
            [
                new LibraryFolderEntry
                {
                    Id = "videos",
                    PrimaryDestinationSourceId = "source",
                    Sources = [new LibrarySourceEntry { Id = "source", Path = _root, ManagementMode = managementMode, AccessMode = accessMode }],
                },
            ],
        };
        return new VideoOrderRenamer(
            _assets,
            new StubFileOrganizer(),
            new LibraryFolderResolver(new OptionsMonitorStub<IngestionOptions>(options)),
            new SourceMutationPolicyGate(),
            NullLogger<VideoOrderRenamer>.Instance);
    }

    private Guid AddAsset(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO works (id, media_type, work_kind) VALUES (@work, 'Videos', 'standalone');", new { work });
        conn.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work);", new { edition, work });
        conn.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@asset, @edition, @hash, @path, 'Normal');",
            new { asset, edition, hash = asset.ToString("N"), path });
        return asset;
    }

    [Fact]
    public async Task Rename_InAManagedFolder_GivesEachFileItsPositionAndKeepsItInItsFolder()
    {
        var a = AddAsset("course/intro.mp4");
        var b = AddAsset("course/basics.mp4");
        var renamer = Renamer(LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.Writable);

        var result = await renamer.RenameAsync([new VideoRenameItem(b, "Basics"), new VideoRenameItem(a, "Intro")]);

        Assert.Equal(new VideoRenameResult(2, 0), result);
        Assert.True(File.Exists(Path.Combine(_root, "course", "01 - Basics.mp4")));
        Assert.True(File.Exists(Path.Combine(_root, "course", "02 - Intro.mp4")));
        Assert.False(File.Exists(Path.Combine(_root, "course", "intro.mp4")));
        Assert.Equal(Path.Combine(_root, "course", "02 - Intro.mp4"), (await _assets.FindByIdAsync(a))!.FilePathRoot);
    }

    [Fact]
    public async Task Rename_InAnAsIsFolder_ChangesNothing()
    {
        var a = AddAsset("course/intro.mp4");
        var renamer = Renamer(LibrarySourceManagementModes.ExistingLibrary, LibrarySourceAccessModes.Writable);

        Assert.False(renamer.CanRename(Path.Combine(_root, "course", "intro.mp4")));
        Assert.False(await renamer.CanRenameAllAsync([a]));
        var result = await renamer.RenameAsync([new VideoRenameItem(a, "Intro")]);

        Assert.Equal(new VideoRenameResult(0, 1), result);
        Assert.True(File.Exists(Path.Combine(_root, "course", "intro.mp4")));
    }

    [Fact]
    public async Task Rename_InAReadOnlyManagedFolder_ChangesNothing()
    {
        var a = AddAsset("course/intro.mp4");
        var renamer = Renamer(LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.ReadOnly);

        Assert.False(await renamer.CanRenameAllAsync([a]));
        Assert.Equal(new VideoRenameResult(0, 1), await renamer.RenameAsync([new VideoRenameItem(a, "Intro")]));
        Assert.True(File.Exists(Path.Combine(_root, "course", "intro.mp4")));
    }

    [Fact]
    public async Task Rename_LeavesAFileAloneWhenTheNewNameIsAlreadyTakenByAnotherFile()
    {
        var a = AddAsset("course/intro.mp4");
        File.WriteAllText(Path.Combine(_root, "course", "01 - Intro.mp4"), "other");
        var renamer = Renamer(LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.Writable);

        var result = await renamer.RenameAsync([new VideoRenameItem(a, "Intro")]);

        Assert.Equal(new VideoRenameResult(0, 1), result);
        Assert.Equal("other", File.ReadAllText(Path.Combine(_root, "course", "01 - Intro.mp4")));
        Assert.True(File.Exists(Path.Combine(_root, "course", "intro.mp4")));
    }

    [Fact]
    public async Task CanRenameAll_IsFalseForAMissingFileAndForAnEmptyList()
    {
        var a = AddAsset("course/intro.mp4");
        File.Delete(Path.Combine(_root, "course", "intro.mp4"));
        var renamer = Renamer(LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.Writable);

        Assert.False(await renamer.CanRenameAllAsync([a]));
        Assert.False(await renamer.CanRenameAllAsync([]));
    }

    [Theory]
    [InlineData(1, 9, "Intro", ".mp4", "01 - Intro.mp4")]
    [InlineData(7, 120, "Part: One/Two?", ".mkv", "007 - Part One Two.mkv")]
    [InlineData(2, 3, "   ", ".mp4", "02 - Video.mp4")]
    public void NameFor_PadsThePositionAndStripsCharactersAFileNameCannotHold(int position, int total, string title, string extension, string expected)
    {
        Assert.Equal(expected, VideoOrderRenamer.NameFor(position, total, title, extension));
    }
}
