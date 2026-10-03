using System.Text.Json;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Contracts.Tests;

public sealed class ListenPlaybackCommandRoundTripTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void BookmarkNoteAndSharedPopupCommand_RoundTripWithTypedPayloadsAndTransientReplyCorrelation()
    {
        var bookmark = new AudiobookBookmarkDto
        {
            Id = Guid.NewGuid(),
            ProfileId = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            PositionSeconds = 125.5,
            Label = "Chapter 4",
            Note = "Read this again.\nThe note stays separate from its label.",
        };
        var command = new ListenPlaybackCommandDto
        {
            CommandId = Guid.NewGuid(),
            RecipientId = Guid.NewGuid(),
            DialogId = Guid.NewGuid(),
            OwnerGeneration = 3,
            ProfileId = bookmark.ProfileId,
            WorkId = bookmark.WorkId,
            SessionLeaseId = Guid.NewGuid(),
            ExpectedAssetId = bookmark.AssetId,
            ExpectedPlaybackRequestVersion = 57,
            Action = ListenPlaybackCommandActions.SaveBookmarkDraft,
            BookmarkDraft = new AudiobookBookmarkDraftPayloadDto
            {
                DraftGeneration = 9,
                ProfileId = bookmark.ProfileId,
                WorkId = bookmark.WorkId,
                SessionLeaseId = Guid.NewGuid(),
                AssetId = bookmark.AssetId,
                ChapterIndex = 3,
                ChapterTitle = "Chapter 4",
                PositionSeconds = 125.5,
                DurationSeconds = 900,
                CapturedAt = DateTimeOffset.Parse("2026-10-02T12:00:00Z"),
                Note = bookmark.Note,
            },
            Reply = new ListenPlaybackCommandReplyDto
            {
                CommandId = Guid.NewGuid(),
                RecipientId = Guid.NewGuid(),
                Outcome = AudiobookBookmarkOperationOutcomes.Unknown,
                Bookmark = bookmark,
                Message = "Reload Saved to check.",
            },
        };

        var json = JsonSerializer.Serialize(command, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ListenPlaybackCommandDto>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(command.CommandId, roundTrip.CommandId);
        Assert.Equal(command.RecipientId, roundTrip.RecipientId);
        Assert.Equal(command.OwnerGeneration, roundTrip.OwnerGeneration);
        Assert.Equal(command.ProfileId, roundTrip.ProfileId);
        Assert.Equal(command.WorkId, roundTrip.WorkId);
        Assert.Equal(command.SessionLeaseId, roundTrip.SessionLeaseId);
        Assert.Equal(command.ExpectedAssetId, roundTrip.ExpectedAssetId);
        Assert.Equal(command.ExpectedPlaybackRequestVersion, roundTrip.ExpectedPlaybackRequestVersion);
        Assert.Equal(command.BookmarkDraft, roundTrip.BookmarkDraft);
        Assert.Equal(command.Reply, roundTrip.Reply);
        Assert.Equal(bookmark.Note, roundTrip.Reply!.Bookmark!.Note);
        Assert.Contains("bookmarkDraft", json, StringComparison.Ordinal);
        Assert.Contains("note", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarkNativeCapture_RoundTripsSourceProofWithoutTransportUrl()
    {
        var capture = new AudiobookBookmarkNativeCaptureDto
        {
            AssetId = Guid.NewGuid(),
            PlaybackRequestVersion = 82,
            SourceVerified = true,
            PositionSeconds = 721.25,
            DurationSeconds = 2400,
        };

        var json = JsonSerializer.Serialize(capture, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<AudiobookBookmarkNativeCaptureDto>(json, JsonOptions);

        Assert.Equal(capture, roundTrip);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
    }
}
