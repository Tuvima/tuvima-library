using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Domain.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Photos and personal videos reach the Activity log as one count row per library, never one row per file.
/// </summary>
public sealed class PersonalMediaActivityRecorderTests
{
    private static readonly Guid Library = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    private readonly RecordingActivityLog _activity = new();
    private readonly ManualTime _time = new();

    [Fact]
    public async Task ThreeThousandPhotos_AreOneRowWithTheCount()
    {
        var recorder = Recorder();
        for (var index = 0; index < 3000; index++)
        {
            recorder.Record(Library, "Family photos", isVideo: false);
        }

        await recorder.FlushAsync(Library);

        var row = Assert.Single(_activity.Entries);
        Assert.Equal(SystemActionType.PersonalMediaAdded, row.ActionType);
        Assert.Equal("3,000 photos", row.CollectionName);
        Assert.Equal("Added to Family photos", row.Detail);
        Assert.Equal("PersonalMedia", row.EntityType);
        Assert.Equal(Library, row.EntityId);
        Assert.Contains("\"photos\":3000", row.ChangesJson, StringComparison.Ordinal);
        Assert.Contains("\"videos\":0", row.ChangesJson, StringComparison.Ordinal);
        Assert.Contains("\"count\":3000", row.ChangesJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 0, "1 photo")]
    [InlineData(0, 1, "1 video")]
    [InlineData(0, 12, "12 videos")]
    [InlineData(3000, 12, "3,000 photos and 12 videos")]
    public async Task PhotosAndVideos_AreWordedWithSingularsAndThousandsSeparators(int photos, int videos, string words)
    {
        var recorder = Recorder();
        for (var index = 0; index < photos; index++)
        {
            recorder.Record(Library, "Family photos", isVideo: false);
        }

        for (var index = 0; index < videos; index++)
        {
            recorder.Record(Library, "Family photos", isVideo: true);
        }

        await recorder.FlushAsync(Library);

        Assert.Equal(words, Assert.Single(_activity.Entries).CollectionName);
    }

    [Fact]
    public async Task FlushingAgain_WritesNothingUntilMoreIsAdded()
    {
        var recorder = Recorder();
        recorder.Record(Library, "Family photos", isVideo: false);

        await recorder.FlushAsync(Library);
        await recorder.FlushAsync(Library);

        Assert.Single(_activity.Entries);
    }

    [Fact]
    public async Task ALibraryThatHasBeenQuietForThirtySeconds_IsWrittenWithoutAnExplicitFlush()
    {
        var recorder = Recorder();
        recorder.Record(Library, "Family photos", isVideo: false);

        _time.Advance(TimeSpan.FromSeconds(29));
        await recorder.FlushQuietLibrariesAsync(CancellationToken.None);
        Assert.Empty(_activity.Entries);

        recorder.Record(Library, "Family photos", isVideo: false);
        _time.Advance(TimeSpan.FromSeconds(29));
        await recorder.FlushQuietLibrariesAsync(CancellationToken.None);
        Assert.Empty(_activity.Entries);

        _time.Advance(TimeSpan.FromSeconds(2));
        await recorder.FlushQuietLibrariesAsync(CancellationToken.None);

        Assert.Equal("2 photos", Assert.Single(_activity.Entries).CollectionName);
    }

    [Fact]
    public async Task EachLibraryGetsItsOwnRow()
    {
        var other = Guid.NewGuid();
        var recorder = Recorder();
        recorder.Record(Library, "Family photos", isVideo: false);
        recorder.Record(other, "Holiday videos", isVideo: true);

        await recorder.FlushAsync(Library);
        await recorder.FlushAsync(other);

        Assert.Equal(["Added to Family photos", "Added to Holiday videos"], _activity.Entries.Select(entry => entry.Detail));
    }

    [Fact]
    public async Task AFailedWrite_KeepsTheCountsSoTheNextFlushWritesThem()
    {
        var recorder = Recorder();
        recorder.Record(Library, "Family photos", isVideo: false);
        recorder.Record(Library, "Family photos", isVideo: false);
        _activity.Fail = true;

        await recorder.FlushAsync(Library);
        Assert.Empty(_activity.Entries);

        _activity.Fail = false;
        await recorder.FlushAsync(Library);

        Assert.Equal("2 photos", Assert.Single(_activity.Entries).CollectionName);
    }

    private PersonalMediaActivityRecorder Recorder() =>
        new(_activity, _time, NullLogger<PersonalMediaActivityRecorder>.Instance);

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
