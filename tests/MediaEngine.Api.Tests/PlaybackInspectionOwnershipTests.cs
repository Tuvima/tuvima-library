using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Tests;

public sealed class PlaybackInspectionOwnershipTests
{
    [Fact]
    public async Task IngestionPersistsFactsAcrossWriterRestartsAndOnlyReinspectsChangedContent()
    {
        using var db = new DatabaseConnection(Path.Combine(Path.GetTempPath(), $"inspection-{Guid.NewGuid():N}.db"));
        db.InitializeSchema();
        db.RunStartupChecks();
        var asset = new MediaAsset { Id = Guid.NewGuid(), EditionId = Guid.NewGuid(), ContentHash = "first", FilePathRoot = "recording.m4b" };
        using (var connection = db.CreateConnection())
        {
            connection.Execute("""
                    INSERT INTO works(id,media_type) VALUES(@work,'Audiobooks');
                    INSERT INTO editions(id,work_id) VALUES(@edition,@work);
                    INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,status) VALUES(@id,@edition,@hash,@path,'Normal');
                    """, new { work = Guid.NewGuid(), edition = asset.EditionId, id = asset.Id, hash = asset.ContentHash, path = asset.FilePathRoot });
        }
        var repository = new PlaybackStateRepository(db);
        var probe = new CountingProbe();
        await new PlaybackInspectionWriter(probe, repository).InspectAsync(asset, default);
        await new PlaybackInspectionWriter(probe, new PlaybackStateRepository(db)).InspectAsync(asset, default);
        Assert.Equal(1, probe.Calls);
        var facts = JsonSerializer.Deserialize<MediaProbeResult>((await repository.GetInspectionMetadataAsync(asset.Id, "first"))!);
        Assert.Equal("Original chapter", Assert.Single(facts!.Chapters).Title);
        Assert.Equal(2, facts.AudioStreams.Count);
        asset.ContentHash = "changed";
        await new PlaybackInspectionWriter(probe, repository).InspectAsync(asset, default);
        Assert.Equal(2, probe.Calls);
        Assert.NotNull(await repository.GetInspectionMetadataAsync(asset.Id, "changed"));
    }

    private sealed class CountingProbe : IFFmpegService
    {
        public int Calls;
        public string? FfmpegPath => "fake";
        public string? FfprobePath => "fake";
        public bool IsAvailable => true;
        public HardwareCapabilities HardwareCapabilities => new();
        public Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<MediaProbeResult?>(new()
            {
                Duration = TimeSpan.FromSeconds(120),
                FileSizeBytes = 2000,
                Chapters = [new(0, "Original chapter", 0, 120)],
                AudioStreams = [new(0, "aac", "en", true), new(1, "aac", "de", false)],
            });
        }
        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
