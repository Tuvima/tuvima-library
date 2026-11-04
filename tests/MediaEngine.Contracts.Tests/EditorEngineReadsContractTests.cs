using System.Text.Json;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the Engine reads and actions the v4 editor uses: match reasons and delete one file (universal editor v4, WP2).</summary>
public sealed class EditorEngineReadsContractTests
{
    [Fact]
    public void FileDeletionRequests_UseSnakeCaseNames()
    {
        var work = Guid.NewGuid();
        var asset = Guid.NewGuid();

        var preview = JsonSerializer.Serialize(new LibraryItemFileDeletionPreviewRequest { WorkId = work, AssetId = asset });
        var confirm = JsonSerializer.Serialize(new LibraryItemFileDeletionRequest { WorkId = work, AssetId = asset, Fingerprint = "abc" });

        Assert.Equal("{\"work_id\":\"" + work.ToString("D") + "\",\"asset_id\":\"" + asset.ToString("D") + "\"}", preview);
        Assert.Equal(
            "{\"work_id\":\"" + work.ToString("D") + "\",\"asset_id\":\"" + asset.ToString("D") + "\",\"fingerprint\":\"abc\"}",
            confirm);
    }

    [Fact]
    public void DeletionPreview_CarriesTheFileScope_OnlyForAOneFileDelete()
    {
        var plain = JsonSerializer.Serialize(new LibraryItemDeletionPreview());
        Assert.DoesNotContain("file_scope", plain, StringComparison.Ordinal);

        var work = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new LibraryItemDeletionPreview
        {
            FileScope = new LibraryItemDeletionFileScopeDto
            {
                WorkId = work,
                AssetId = asset,
                FileName = "Show.S01E01.2160p.mkv",
                ItemRemains = true,
                RemainingFiles = 1,
            },
        });
        var back = JsonSerializer.Deserialize<LibraryItemDeletionPreview>(json)!;

        Assert.Contains(
            "\"file_scope\":{\"work_id\":\"" + work.ToString("D") + "\",\"asset_id\":\"" + asset.ToString("D")
            + "\",\"file_name\":\"Show.S01E01.2160p.mkv\",\"item_remains\":true,\"remaining_files\":1}",
            json,
            StringComparison.Ordinal);
        Assert.True(back.FileScope!.ItemRemains);
        Assert.Equal(1, back.FileScope.RemainingFiles);
    }

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
}
