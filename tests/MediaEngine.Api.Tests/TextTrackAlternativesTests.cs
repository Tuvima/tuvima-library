using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Not right? Find another" and "This one is wrong" (Plugin architecture spec C24): the list is asked of the sources live, a chosen
/// result becomes the item's choice with the one before it kept, only the current and the one before are stored, a result marked wrong is
/// never picked again (not even by an ordinary lookup), and a person's own file is never replaced or removed. Real data store and
/// folders; the source is a hand-written spy.
/// </summary>
public sealed class TextTrackAlternativesTests : IDisposable
{
    private readonly LibraryMoveTestWorld _world = new(withFiles: true);
    private readonly TextTrackRepository _tracks;
    private readonly TextTrackEnrichmentWorker _worker;
    private readonly SpySource _source = new();
    private readonly Guid _assetId;
    private readonly string _central;

    public TextTrackAlternativesTests()
    {
        var files = _world.Files!;
        _tracks = new TextTrackRepository(files.Database);
        _central = Path.Combine(files.Managed, ".library");
        _worker = new TextTrackEnrichmentWorker(
            new MediaAssetRepository(files.Database),
            new WorkRepository(files.Database),
            new CanonicalValueRepository(files.Database),
            new BridgeIdRepository(files.Database),
            _tracks,
            [_source],
            new AssetPathService(_central),
            NullLogger<TextTrackEnrichmentWorker>.Instance);
        var album = _world.AddParent("Music", "The Best Of");
        var path = MoveFileWorld.WriteFile(files.Managed, "inbox/001 Song.flac", "audio");
        _assetId = _world.AddChild(album, "Music", "Song", 1, filePath: path).Item3!.Value;
        _world.Put(_assetId, "media_type", "Music");
        _world.Put(_assetId, "title", "Song");
        _world.Put(_assetId, "artist", "Artist");
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task TheList_IsAskedOfTheSourcesLive_WithAPreviewAndTheCurrentOneMarked()
    {
        _source.Results = [Result("a", 0.9), Result("b", 0.8)];
        await _worker.ChooseAlternativeAsync(_assetId, TextTrackKind.Lyrics, _source.Name, "a", null);

        var list = await _worker.FindAlternativesAsync(_assetId, TextTrackKind.Lyrics, null);

        Assert.Equal("Found", list.Status);
        Assert.Equal(["a", "b"], list.Items.OrderBy(item => item.SourceId).Select(item => item.SourceId));
        Assert.Equal("first line / second a", list.Items.First(item => item.SourceId == "a").Preview);
        Assert.True(list.Items.First(item => item.SourceId == "a").IsCurrent);
        Assert.False(list.Items.First(item => item.SourceId == "b").IsCurrent);
        Assert.Single(await _tracks.GetByAssetAsync(_assetId, TextTrackKind.Lyrics));
    }

    [Fact]
    public async Task WithNoSourceSwitchedOn_TheListSaysSo()
    {
        _source.Enabled = false;

        var list = await _worker.FindAlternativesAsync(_assetId, TextTrackKind.Lyrics, null);

        Assert.Equal("NoSourceOn", list.Status);
    }

    [Fact]
    public async Task AChosenResult_BecomesTheChoice_AndOnlyTheCurrentAndThePreviousAreKept()
    {
        _source.Results = [Result("a", 0.9), Result("b", 0.8), Result("c", 0.7)];
        await _worker.ChooseAlternativeAsync(_assetId, TextTrackKind.Lyrics, _source.Name, "a", null);
        await _worker.ChooseAlternativeAsync(_assetId, TextTrackKind.Lyrics, _source.Name, "b", null);
        var third = await _worker.ChooseAlternativeAsync(_assetId, TextTrackKind.Lyrics, _source.Name, "c", null);

        var kept = await _tracks.GetByAssetAsync(_assetId, TextTrackKind.Lyrics);

        Assert.Equal("Updated", third.Status);
        Assert.Equal(["b", "c"], kept.Select(track => track.SourceId).Order());
        Assert.Equal("c", kept.Single(track => track.IsPreferred).SourceId);
        Assert.All(kept, track => Assert.True(File.Exists(track.LocalPath)));
    }

    [Fact]
    public async Task AResultMarkedWrong_IsNeverPickedAgain_ByTheListOrByAnOrdinaryLookup()
    {
        _source.Results = [Result("a", 0.9), Result("b", 0.8)];
        await _worker.ChooseAlternativeAsync(_assetId, TextTrackKind.Lyrics, _source.Name, "a", null);
        var current = (await _tracks.GetPreferredAsync(_assetId, TextTrackKind.Lyrics))!;

        var outcome = await _worker.MarkWrongAsync(_assetId, current.Id);
        var list = await _worker.FindAlternativesAsync(_assetId, TextTrackKind.Lyrics, null);
        await _worker.EnrichAsync(_assetId, TextTrackKind.Lyrics);

        Assert.Equal("Updated", outcome.Status);
        Assert.False(File.Exists(current.LocalPath));
        Assert.DoesNotContain(list.Items, item => item.SourceId == "a");
        var afterLookup = await _tracks.GetByAssetAsync(_assetId, TextTrackKind.Lyrics);
        Assert.DoesNotContain(afterLookup, track => track.SourceId == "a");
        Assert.Contains(afterLookup, track => track.SourceId == "b");
    }

    [Fact]
    public async Task MarkingTheCurrentOneWrong_BringsTheOneBeforeItBack()
    {
        _source.Results = [Result("a", 0.9), Result("b", 0.8)];
        await _worker.ChooseAlternativeAsync(_assetId, TextTrackKind.Lyrics, _source.Name, "a", null);
        await _worker.ChooseAlternativeAsync(_assetId, TextTrackKind.Lyrics, _source.Name, "b", null);
        var current = (await _tracks.GetPreferredAsync(_assetId, TextTrackKind.Lyrics))!;

        var outcome = await _worker.MarkWrongAsync(_assetId, current.Id);

        var back = (await _tracks.GetPreferredAsync(_assetId, TextTrackKind.Lyrics))!;
        Assert.Equal("a", back.SourceId);
        Assert.Equal(back.Id, outcome.TrackId);
    }

    [Fact]
    public async Task APersonsOwnFile_CannotBeMarkedWrong_AndIsNeverRemoved()
    {
        var path = MoveFileWorld.WriteFile(_central, "lyrics/own.lrc", "[00:01.00]mine");
        var own = new TextTrack
        {
            AssetId = _assetId,
            Kind = TextTrackKind.Lyrics,
            Language = "en",
            Provider = "local",
            SourceId = "own",
            SourceFormat = "lrc",
            NormalizedFormat = "lrc",
            LocalPath = path,
            IsUserOwned = true,
            IsPreferred = true,
        };
        await _tracks.UpsertAsync(own);

        var outcome = await _worker.MarkWrongAsync(_assetId, own.Id);

        Assert.Equal("PreservedUserOwned", outcome.Status);
        Assert.True(File.Exists(path));
        Assert.NotNull(await _tracks.FindByIdAsync(own.Id));
    }

    private static TextTrackCandidate Result(string id, double confidence) =>
        new("spy", TextTrackKind.Lyrics, id, null, "en", "lrc", confidence, false, 0.95);

    private sealed class SpySource : ITextTrackProvider
    {
        public string Name => "spy";

        public TextTrackKind Kind => TextTrackKind.Lyrics;

        public bool IsEnabled => Enabled;

        public bool Enabled { get; set; } = true;

        public IReadOnlyList<TextTrackCandidate> Results { get; set; } = [];

        public bool CanHandle(MediaType mediaType) => mediaType == MediaType.Music;

        public Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default) =>
            Task.FromResult(Results);

        public Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default) =>
            Task.FromResult<TextTrackDownload?>(new TextTrackDownload(candidate, $"[00:01.00]first line\n[00:05.00]second {candidate.SourceId}", "lrc", "lrc"));
    }
}
