using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackSnapshotStreamTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ReadAsyncRoundTripsSnapshotLargerThanDefaultHubPayloadAndOwnsResources()
    {
        var snapshot = new ListenPlaybackSnapshot
        {
            Experience = PlayerExperienceModes.Music,
            CurrentIndex = 42,
            Queue = Enumerable.Range(0, 48)
                .Select(index => new ListenQueueItem
                {
                    WorkId = Guid.NewGuid(),
                    AssetId = Guid.NewGuid(),
                    MediaType = "Music",
                    Title = $"Track {index}: {new string((char)('a' + index % 26), 1_000)}",
                    Subtitle = "Preserved artist",
                    Album = "Preserved album",
                    StreamUrl = $"/stream/{index}",
                    Duration = "3:42",
                })
                .ToList(),
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        Assert.True(payload.Length > 32 * 1024);
        var stream = new TrackingMemoryStream(payload);
        var reference = new FakeJsStreamReference(stream);

        var restored = await PlaybackSnapshotStream.ReadAsync(reference, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(snapshot.Queue.Count, restored.Queue.Count);
        Assert.Equal(snapshot.CurrentIndex, restored.CurrentIndex);
        Assert.Equal(snapshot.Queue[42].Title, restored.Queue[42].Title);
        Assert.Equal(snapshot.Queue[42].StreamUrl, restored.Queue[42].StreamUrl);
        Assert.True(stream.WasDisposed);
        Assert.True(reference.WasDisposed);
    }

    [Fact]
    public async Task ReadAsyncRejectsOversizedAndMalformedPayloadsAndDisposesReferences()
    {
        var oversizedPayload = Encoding.UTF8.GetBytes(
            "{\"queue\":[],\"padding\":\"" + new string('x', (int)PlaybackSnapshotStream.MaximumBytes + 1) + "\"}");
        var oversizedStream = new TrackingMemoryStream(oversizedPayload);
        var oversizedReference = new FakeJsStreamReference(oversizedStream);

        var oversizedError = await Assert.ThrowsAsync<InvalidDataException>(
            () => PlaybackSnapshotStream.ReadAsync(oversizedReference, JsonOptions));
        Assert.Contains("maximum size", oversizedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(oversizedStream.WasDisposed);
        Assert.True(oversizedReference.WasDisposed);

        var malformedStream = new TrackingMemoryStream(Encoding.UTF8.GetBytes("{not-json"));
        var malformedReference = new FakeJsStreamReference(malformedStream);

        await Assert.ThrowsAsync<JsonException>(() => PlaybackSnapshotStream.ReadAsync(malformedReference, JsonOptions));
        Assert.True(malformedStream.WasDisposed);
        Assert.True(malformedReference.WasDisposed);
        var nullJsonReference = new FakeJsStreamReference(new TrackingMemoryStream(Encoding.UTF8.GetBytes("null")));
        await Assert.ThrowsAsync<InvalidDataException>(() => PlaybackSnapshotStream.ReadAsync(nullJsonReference, JsonOptions));
        Assert.True(nullJsonReference.WasDisposed);
        Assert.Null(await PlaybackSnapshotStream.ReadAsync(null, JsonOptions));
    }

    private sealed class FakeJsStreamReference(Stream stream) : IJSStreamReference
    {
        public long Length => stream.Length;
        public bool WasDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            stream.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512_000, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length > maxAllowedSize)
            {
                throw new InvalidDataException($"The stream exceeds the maximum size of {maxAllowedSize} bytes.");
            }
            return ValueTask.FromResult(stream);
        }
    }

    private sealed class TrackingMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
