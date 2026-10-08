using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

public sealed class AudioMetadataReadbackTests
{
    [Fact]
    public async Task Mp3Id3v2_ReopenedFileMatchesEveryRequestedField()
    {
        var path = await CreateDisposableMp3Async();
        try
        {
            var tagger = new AudioMetadataTagger(NullLogger<AudioMetadataTagger>.Instance,
                new MediaTypeExtensionCatalog());
            var tags = new Dictionary<string, string>
            {
                ["title"] = "A Song",
                ["artist"] = "An Artist",
                ["album"] = "An Album",
                ["track_number"] = "02",
                ["narrator"] = "A Narrator",
                ["genre"] = "Rock",
                ["description"] = "A description",
                ["year"] = "2026",
                ["publisher"] = "A Publisher",
                ["isbn"] = "9781234567890",
                ["asin"] = "B001TEST",
                ["audible_id"] = "AUD-123",
                ["apple_books_id"] = "BOOK-123",
                ["apple_music_id"] = "SONG-123",
                ["apple_music_collection_id"] = "ALBUM-123",
                ["apple_artist_id"] = "ARTIST-123",
                ["musicbrainz_id"] = Guid.NewGuid().ToString("D"),
                ["wikidata_qid"] = "Q123",
            };

            await tagger.WriteTagsAsync(path, tags);
            var result = await ((IMetadataTagger)tagger).VerifyTagsAsync(path, tags);
            Assert.True(result.IsVerified, result.Reason);
            Assert.False(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
        }
        finally { DeleteFixture(path); }
    }

    [Fact]
    public async Task Mp3Id3v2_MismatchedRequestIsUnverified()
    {
        var path = await CreateDisposableMp3Async();
        try
        {
            var tagger = new AudioMetadataTagger(NullLogger<AudioMetadataTagger>.Instance,
                new MediaTypeExtensionCatalog());
            await tagger.WriteTagsAsync(path, new Dictionary<string, string>
            {
                ["title"] = "Saved title",
                ["album"] = "Saved album",
            });

            var mismatched = await tagger.VerifyTagsAsync(path, new Dictionary<string, string>
            {
                ["title"] = "Different title",
                ["album"] = "Saved album",
            });
            Assert.False(mismatched.IsVerified);
            Assert.Contains("title", mismatched.Reason);

        }
        finally { DeleteFixture(path); }
    }

    [Fact]
    public async Task ConflictingAudioAliasesFailBeforeFileMutation()
    {
        var path = await CreateDisposableMp3Async();
        try
        {
            var tagger = new AudioMetadataTagger(NullLogger<AudioMetadataTagger>.Instance,
                new MediaTypeExtensionCatalog());
            var original = await File.ReadAllBytesAsync(path);
            IReadOnlyDictionary<string, string>[] conflicts =
            [
                new Dictionary<string, string> { ["author"] = "A", ["artist"] = "B" },
                new Dictionary<string, string> { ["album"] = "A", ["series"] = "B" },
                new Dictionary<string, string> { ["track_number"] = "1", ["series_position"] = "2" },
            ];
            foreach (var conflict in conflicts)
            {
                await Assert.ThrowsAsync<NotSupportedException>(() => tagger.WriteTagsAsync(path, conflict));
                Assert.Equal(original, await File.ReadAllBytesAsync(path));
                Assert.False(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
            }
        }
        finally { DeleteFixture(path); }
    }

    [Fact]
    public async Task FailedMp3ReadbackRestoresBackupBeforeReturning()
    {
        var path = await CreateDisposableMp3Async();
        try
        {
            var tagger = new AudioMetadataTagger(NullLogger<AudioMetadataTagger>.Instance,
                new MediaTypeExtensionCatalog());
            await tagger.WriteTagsAsync(path, new Dictionary<string, string> { ["title"] = "Original" });
            var original = await File.ReadAllBytesAsync(path);

            // The writer does not create a custom ID frame for an empty value.
            // It saves the changed title first, then read-back must fail and
            // the backup must restore the exact original bytes.
            await Assert.ThrowsAsync<InvalidDataException>(() => tagger.WriteTagsAsync(path,
                new Dictionary<string, string> { ["title"] = "Changed", ["isbn"] = "" }));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(path + BackedUpMetadataTagger.BackupSuffix));
            using var reopened = TagLib.File.Create(path);
            Assert.Equal("Original", reopened.Tag.Title);
        }
        finally { DeleteFixture(path); }
    }

    [Fact]
    public async Task OtherAudioFormatsRemainUnverifiedWithoutAProvenReader()
    {
        IMetadataTagger tagger = new AudioMetadataTagger(NullLogger<AudioMetadataTagger>.Instance,
            new MediaTypeExtensionCatalog());
        var result = await tagger.VerifyTagsAsync("unopened.opus",
            new Dictionary<string, string> { ["title"] = "A Song" });
        Assert.False(result.IsVerified);
    }

    private static async Task<string> CreateDisposableMp3Async()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_readback_{Guid.NewGuid():N}.mp3");
        var frame = new byte[417];
        frame[0] = 0xff;
        frame[1] = 0xfb;
        frame[2] = 0x90;
        frame[3] = 0x64;
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 8).SelectMany(_ => frame).ToArray());
        return path;
    }

    private static void DeleteFixture(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        if (File.Exists(path + BackedUpMetadataTagger.BackupSuffix))
        {
            File.Delete(path + BackedUpMetadataTagger.BackupSuffix);
        }
    }
}
