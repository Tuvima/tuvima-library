using System.Text.Json;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the Engine reads and actions the v4 editor uses: match reasons and delete one file (universal editor v4, WP2).</summary>
public sealed class EditorEngineReadsContractTests
{
    [Fact]
    public void MatchCandidates_CarryAShortReasonText()
    {
        var retail = JsonSerializer.Serialize(new ItemCanonicalRetailCandidateDto { ReasonText = "Title and year match; author differs" });
        var linked = JsonSerializer.Serialize(new ItemCanonicalLinkedCandidateDto { ReasonText = "Title matches" });

        Assert.Contains("\"reason_text\":\"Title and year match; author differs\"", retail, StringComparison.Ordinal);
        Assert.Contains("\"reason_text\":\"Title matches\"", linked, StringComparison.Ordinal);
        Assert.Equal(
            "Title matches",
            JsonSerializer.Deserialize<ItemCanonicalLinkedCandidateDto>(linked)!.ReasonText);
    }

    [Fact]
    public void PairingCandidate_CarriesAReasonText_ThatDefaultsToNone()
    {
        var child = new MediaEditorPairingChildDto("ep-1", "show-1", "tvdb", "Pilot", 1, 1, null, null, null);

        var without = new MediaEditorPairingCandidateDto(child, "Strong", [], []);
        var with = new MediaEditorPairingCandidateDto(child, "Strong", [], [], true, null, "Episode number and runtime match; title differs");

        Assert.Null(without.ReasonText);
        Assert.Equal("Episode number and runtime match; title differs", with.ReasonText);
        var back = JsonSerializer.Deserialize<MediaEditorPairingCandidateDto>(JsonSerializer.Serialize(with))!;
        Assert.Equal(with.ReasonText, back.ReasonText);
    }

    [Fact]
    public void QuickFacts_UseSnakeCaseNames_AndOmitWhatIsUnknown()
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new ItemQuickFactsDto
        {
            Id = id,
            FileCount = 2,
            SizeBytes = 3_000,
            RuntimeSeconds = 90.5,
            BestQuality = "4K HDR",
            DirectOwned = 1,
            DirectTotal = 2,
            LeafOwned = 3,
            LeafTotal = 4,
            ReadOnlyLibrary = true,
            Roles = ["Author"],
            CountsByKind = [new ItemQuickFactCountDto { Label = "Films", Count = 2 }],
        });
        var back = JsonSerializer.Deserialize<ItemQuickFactsDto>(json)!;

        Assert.Contains("\"id\":\"" + id.ToString("D") + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"work\"", json, StringComparison.Ordinal);
        Assert.Contains("\"size_bytes\":3000", json, StringComparison.Ordinal);
        Assert.Contains("\"runtime_seconds\":90.5", json, StringComparison.Ordinal);
        Assert.Contains("\"best_quality\":\"4K HDR\"", json, StringComparison.Ordinal);
        Assert.Contains("\"direct_owned\":1,\"direct_total\":2,\"leaf_owned\":3,\"leaf_total\":4", json, StringComparison.Ordinal);
        Assert.Contains("\"read_only_library\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"counts_by_kind\":[{\"label\":\"Films\",\"count\":2}]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("item_count", json, StringComparison.Ordinal);
        Assert.DoesNotContain("owner", json, StringComparison.Ordinal);
        Assert.True(back.ReadOnlyLibrary);
        Assert.Equal(["Author"], back.Roles);
        Assert.Equal(2, back.CountsByKind.Single().Count);
    }
}
