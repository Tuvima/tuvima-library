using MediaEngine.Api.Services;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Lyrics beside the song (Plugin architecture spec PA7): with write-back off nothing is written; with it on, timed lyrics become
/// <c>Song.lrc</c> and plain ones <c>Song.txt</c>; a changed choice replaces the one file the Library wrote; a person's own lyrics file
/// is never replaced or removed. Real temp folders and a real data store.
/// </summary>
public sealed class LyricsWritebackTests : IDisposable
{
    private readonly LibraryMoveTestWorld _world = new(withFiles: true);
    private readonly TextTrackRepository _tracks;
    private readonly MediaAssetRepository _assets;
    private readonly TextTrackExportService _export;
    private readonly Guid _assetId;
    private readonly string _songPath;

    public LyricsWritebackTests()
    {
        _tracks = new TextTrackRepository(Files.Database);
        _assets = new MediaAssetRepository(Files.Database);
        _export = new TextTrackExportService(
            new LibraryFolderResolver(new FixedOptions(Files.Options)),
            new SourceMutationPolicyGate(Files.MergeLedger),
            Files.Configuration,
            NullLogger<TextTrackExportService>.Instance,
            filingHold: null,
            textTracks: _tracks);
        var album = _world.AddParent("Music", "The Best Of");
        _songPath = MoveFileWorld.WriteFile(Files.Managed, "inbox/001 Song.flac", "audio bytes");
        _assetId = _world.AddChild(album, "Music", "Song", 1, filePath: _songPath).Item3!.Value;
        _world.Put(_assetId, "media_type", "Music");
    }

    private MoveFileWorld Files => _world.Files!;

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task WithWritebackOff_NothingIsWrittenBesideTheSong()
    {
        var track = await AddTrackAsync("[00:01.00]hello", plain: false);

        Assert.Null(await _export.ExportPreferredLyricsAsync(await AssetAsync(), track));
        Assert.False(File.Exists(Sidecar(".lrc")));
    }

    [Fact]
    public async Task WithWritebackOn_TimedLyricsAreWrittenAsAnLrcFile_AndPlainOnesAsATxtFile()
    {
        TurnWritebackOn();
        var timed = await AddTrackAsync("[00:01.00]hello", plain: false);

        Assert.Equal(Sidecar(".lrc"), await _export.ExportPreferredLyricsAsync(await AssetAsync(), timed));
        Assert.Equal("[00:01.00]hello", await File.ReadAllTextAsync(Sidecar(".lrc")));
        Assert.Equal("audio bytes", await File.ReadAllTextAsync(_songPath));
    }

    [Fact]
    public async Task AChangedChoice_ReplacesTheOneFileTheLibraryWrote()
    {
        TurnWritebackOn();
        var first = await AddTrackAsync("[00:01.00]first", plain: false);
        first.SidecarPath = await _export.ExportPreferredLyricsAsync(await AssetAsync(), first);
        await _tracks.UpsertAsync(first);
        var second = await AddTrackAsync("plain words", plain: true);

        var written = await _export.ExportPreferredLyricsAsync(await AssetAsync(), second);

        Assert.Equal(Sidecar(".txt"), written);
        Assert.False(File.Exists(Sidecar(".lrc")));
        Assert.True(File.Exists(Sidecar(".txt")));
        Assert.Null((await _tracks.FindByIdAsync(first.Id))!.SidecarPath);
    }

    [Theory]
    [InlineData(".lrc")]
    [InlineData(".txt")]
    public async Task APersonsOwnLyricsFile_IsNeverReplacedOrRemoved(string ownExtension)
    {
        TurnWritebackOn();
        await File.WriteAllTextAsync(Sidecar(ownExtension), "their own words");
        var track = await AddTrackAsync("[00:01.00]found online", plain: false);

        Assert.Null(await _export.ExportPreferredLyricsAsync(await AssetAsync(), track));
        Assert.Equal("their own words", await File.ReadAllTextAsync(Sidecar(ownExtension)));
    }

    [Fact]
    public async Task APersonsOwnImportedTrack_IsNotCopiedAndItsFileIsNeverTreatedAsTheLibrarys()
    {
        TurnWritebackOn();
        var own = await AddTrackAsync("their words", plain: true, userOwned: true);
        own.SidecarPath = Sidecar(".txt");
        await File.WriteAllTextAsync(Sidecar(".txt"), "their words");
        await _tracks.UpsertAsync(own);
        var found = await AddTrackAsync("[00:01.00]found online", plain: false);

        Assert.Null(await _export.ExportPreferredLyricsAsync(await AssetAsync(), own));
        Assert.Null(await _export.ExportPreferredLyricsAsync(await AssetAsync(), found));
        Assert.Equal("their words", await File.ReadAllTextAsync(Sidecar(".txt")));
        Assert.Equal(Sidecar(".txt"), (await _tracks.FindByIdAsync(own.Id))!.SidecarPath);
    }

    private string Sidecar(string extension) => AssetPathService.BuildLyricsSidecarPath(_songPath, extension);

    private async Task<MediaAsset> AssetAsync() => (await _assets.FindByIdAsync(_assetId))!;

    private void TurnWritebackOn() =>
        Files.Configuration.SaveConfig(string.Empty, "writeback", new WriteBackConfiguration { Enabled = true });

    private async Task<TextTrack> AddTrackAsync(string content, bool plain, bool userOwned = false)
    {
        var path = MoveFileWorld.WriteFile(Path.Combine(Files.Managed, ".library"), $"lyrics/{Guid.NewGuid():N}.{(plain ? "txt" : "lrc")}", content);
        var track = new TextTrack
        {
            AssetId = _assetId,
            Kind = TextTrackKind.Lyrics,
            Language = "en",
            Provider = "test",
            SourceFormat = plain ? "txt" : "lrc",
            NormalizedFormat = plain ? "txt" : "lrc",
            LocalPath = path,
            TimingMode = plain ? "Plain" : "Line",
            IsUserOwned = userOwned,
        };
        await _tracks.UpsertAsync(track);
        return track;
    }

    private sealed class FixedOptions(MediaEngine.Ingestion.Models.IngestionOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<MediaEngine.Ingestion.Models.IngestionOptions>
    {
        public MediaEngine.Ingestion.Models.IngestionOptions CurrentValue => value;

        public MediaEngine.Ingestion.Models.IngestionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<MediaEngine.Ingestion.Models.IngestionOptions, string?> listener) => null;
    }
}
