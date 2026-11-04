using MediaEngine.Api.Services.Matching;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>The one-line reason shown beside a match confidence: worded only from evidence the existing scoring already produced.</summary>
public sealed class MatchReasonTextTests
{
    [Fact]
    public void FieldScores_NameWhatAgrees_WhatIsClose_AndWhatDiffers()
    {
        var text = MatchReasonText.FromFieldScores(
        [
            Weighted("title", "Title", "exact"),
            Weighted("year", "Year", "exact"),
            Weighted("author", "Author", "close"),
            Weighted("duration", "Duration", "mismatch"),
        ]);

        Assert.Equal("Title and year match; author is close; duration differs", text);
    }

    [Fact]
    public void FieldScores_LeaveOutFieldsNobodySupplied()
    {
        var text = MatchReasonText.FromFieldScores(
        [
            Weighted("title", "Title", "exact"),
            Weighted("year", "Year", "not_provided"),
            Weighted("author", "Author", "required_missing"),
        ]);

        Assert.Equal("Title matches", text);
    }

    [Fact]
    public void FieldScores_PutAnExactIdentifierFirst_AndKeepCapitalsInShortWords()
    {
        var text = MatchReasonText.FromFieldScores(
        [
            Weighted("title", "Title", "exact"),
            Weighted("isbn", "ISBN", "exact"),
            new RetailFieldScore("exact_id", "Exact identifier", 0.35, 0, false, "bonus", 0.35, "redistribute", "bonus", null, null),
        ]);

        Assert.Equal("Exact identifier, title and ISBN match", text);
    }

    [Fact]
    public void FieldScores_ReportAFailedGate_AsADifference()
    {
        var text = MatchReasonText.FromFieldScores(
        [
            Weighted("title", "Title", "exact"),
            new RetailFieldScore("format", "Media kind", 0, 0, false, "gate", 0, "zero", "fail", null, null),
        ]);

        Assert.Equal("Title matches; media kind differs", text);
    }

    [Fact]
    public void FieldScores_WithNothingToSay_GiveNoText()
    {
        Assert.Null(MatchReasonText.FromFieldScores(null));
        Assert.Null(MatchReasonText.FromFieldScores([]));
        Assert.Null(MatchReasonText.FromFieldScores([Weighted("year", "Year", "not_provided")]));
    }

    [Fact]
    public void FileToEpisodePairing_SaysWhichEvidenceAgreedAndWhichDiffers()
    {
        var asset = new PairingAssetEvidence(
            Guid.NewGuid(), "Show.S01E02.Wrong Words.mkv", SeasonNumber: 1, EpisodeNumber: 2, Duration: TimeSpan.FromMinutes(44));
        var preview = ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.TvEpisode, "tvdb", "show-1", [asset],
            [new PairingCatalogueChild("ep-2", "show-1", "tvdb", "The Real Title", 1, 2, Duration: TimeSpan.FromMinutes(44))],
            true));

        var row = Assert.Single(preview.Rows);
        Assert.Equal("Season and episode number and runtime match; title differs", row.Proposed!.ReasonText);
    }

    [Fact]
    public void FileToTrackPairing_UsesLengthAndTrackWords()
    {
        var asset = new PairingAssetEvidence(
            Guid.NewGuid(), "01 - Opening.flac", Title: "Opening", DiscNumber: 1, TrackNumber: 1, Duration: TimeSpan.FromSeconds(200));
        var preview = ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.MusicReleaseTrack, "musicbrainz", "release-1", [asset],
            [new PairingCatalogueChild("t-1", "release-1", "musicbrainz", "Opening", DiscNumber: 1, TrackNumber: 1, Duration: TimeSpan.FromSeconds(201))],
            true));

        var row = Assert.Single(preview.Rows);
        Assert.Equal("Title, disc and track number and length match", row.Proposed!.ReasonText);
    }

    [Fact]
    public void FileToEpisodePairing_WithTwoSimilarCandidates_SaysAnotherEntryLooksSimilar()
    {
        var asset = new PairingAssetEvidence(Guid.NewGuid(), "Show.S00E01.The Beginning.mkv");
        var preview = ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.TvEpisode, "tvdb", "show-1", [asset],
            [
                new PairingCatalogueChild("special", "show-1", "tvdb", "Behind the Scenes", 0, 1),
                new PairingCatalogueChild("actual", "show-1", "tvdb", "The Beginning", 1, 1),
            ], true));

        var row = Assert.Single(preview.Rows);
        Assert.Contains("another entry looks similar", row.Proposed!.ReasonText, StringComparison.Ordinal);
    }

    private static RetailFieldScore Weighted(string key, string label, string verdict) =>
        new(key, label, verdict == "not_provided" ? null : 1, 0.2, verdict == "not_provided", "weighted", 0.2, "redistribute", verdict, null, null);
}
