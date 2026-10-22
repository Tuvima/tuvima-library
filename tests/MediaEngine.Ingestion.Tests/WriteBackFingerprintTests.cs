using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Library recognises its own writes (batched write-back D8): after the scheduled run writes a file, the file's new fingerprint is
/// recorded, so the watcher's change check sees a known file instead of a new or changed one.
/// </summary>
public sealed class WriteBackFingerprintTests : IDisposable
{
    private readonly FolderConversionHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task AFileLibraryJustWrote_IsRecordedUnderItsNewFingerprint_SoItIsNotTakenForAChangedFile()
    {
        var path = _harness.Write("Dune (2021).mkv", "the original bytes");
        var assetId = await _harness.RegisterAsync(path, "Dune", "2021");
        var before = (await _harness.Hasher.ComputeAsync(path)).Hex;
        _harness.Config.WritebackFields = new WritebackFieldsConfiguration { Movies = ["title"] };
        var cache = new FileHashCacheRepository(_harness.Db.Connection);
        var service = new WriteBackService(
            _harness.Assets,
            _harness.Canonicals,
            new WorkRepository(_harness.Db.Connection),
            _harness.Config,
            [new AppendingTagger()],
            _harness.Activity,
            NullLogger<WriteBackService>.Instance,
            libraryResolver: new LibraryFolderResolver(new OptionsMonitorStub<IngestionOptions>(_harness.Options)),
            assetHasher: _harness.Hasher,
            fileHashCache: cache);

        var outcome = await service.WriteMetadataWithOutcomeAsync(assetId, "manual_override");

        Assert.Equal(WriteBackOutcomeKind.Verified, outcome.Kind);
        var after = (await _harness.Hasher.ComputeAsync(path)).Hex;
        Assert.NotEqual(before, after);
        Assert.Equal(after, (await _harness.Assets.FindByIdAsync(assetId))!.ContentHash);
        var info = new FileInfo(path);
        Assert.Equal(after, await cache.TryGetAsync(path, info.Length, info.LastWriteTimeUtc));
    }

    /// <summary>Changes the file the way a real tagger does (so its fingerprint moves) and reads the tags back as written.</summary>
    private sealed class AppendingTagger : IMetadataTagger
    {
        public bool CanHandle(string filePath) => true;

        public MetadataTaggerCapabilities GetCapabilities(string filePath) =>
            new("test", ["title"], canWriteArtwork: false, adapterVersion: 1);

        public Task WriteTagsAsync(string filePath, IReadOnlyDictionary<string, string> tags, CancellationToken ct = default)
        {
            File.AppendAllText(filePath, "|tags:" + string.Join(",", tags.Select(tag => $"{tag.Key}={tag.Value}")));
            return Task.CompletedTask;
        }

        public Task<MetadataTagReadbackResult> VerifyTagsAsync(
            string filePath, IReadOnlyDictionary<string, string> tags, CancellationToken ct = default) =>
            Task.FromResult(MetadataTagReadbackResult.Verified());

        public Task WriteCoverArtAsync(string filePath, byte[] imageData, CancellationToken ct = default) => Task.CompletedTask;
    }
}
