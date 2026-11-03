using System.Text.Json;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Contracts.Tests;

public sealed class MediaEditorOwnedChildDtoTests
{
    [Fact]
    public void OwnedFileWireFormatPreservesAllThreeIdentityLevels()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var item = new MediaEditorOwnedChildDto
        {
            WorkId = workId,
            EditionId = editionId,
            AssetId = assetId,
            StructuralParentId = parentId,
            SelectionRevision = "v1:opaque-revision",
            IdentityOwnerEntityId = workId,
            ArtworkOwnerEntityId = editionId,
            MetadataOwnerEntityId = editionId,
            SelectionNodeKind = "asset",
            EditionLabel = "Illustrated Edition",
            EditionAssetCount = 2,
            CollapseEdition = false,
        };

        var json = JsonSerializer.Serialize(item);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(workId, parsed.RootElement.GetProperty("work_id").GetGuid());
        Assert.Equal(editionId, parsed.RootElement.GetProperty("edition_id").GetGuid());
        Assert.Equal(assetId, parsed.RootElement.GetProperty("asset_id").GetGuid());
        Assert.Equal(parentId, parsed.RootElement.GetProperty("structural_parent_id").GetGuid());
        Assert.Equal("v1:opaque-revision", parsed.RootElement.GetProperty("selection_revision").GetString());
        Assert.Equal(editionId, parsed.RootElement.GetProperty("artwork_owner_entity_id").GetGuid());
        Assert.Equal("asset", parsed.RootElement.GetProperty("selection_node_kind").GetString());
        Assert.False(parsed.RootElement.GetProperty("collapse_edition").GetBoolean());
    }

    [Fact]
    public void SelectionSnapshotWireFormatContainsOnlyFrozenAssetReferences()
    {
        var assetId = Guid.NewGuid();
        var dto = new MediaEditorOwnedChildSelectionSnapshotDto
        {
            ParentEntityId = Guid.NewGuid(),
            Count = 1,
            Items = [new() { AssetId = assetId, SelectionRevision = "v1:opaque" }],
        };

        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(dto));
        Assert.Equal(1, parsed.RootElement.GetProperty("count").GetInt32());
        var item = Assert.Single(parsed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(assetId, item.GetProperty("asset_id").GetGuid());
        Assert.Equal("v1:opaque", item.GetProperty("selection_revision").GetString());
    }

    [Fact]
    public void FamilyTreeFieldsUseTheirOwnWireNames_AndAnOldAnswerWithoutThemReadsAsMatched()
    {
        var file = new MediaEditorOwnedChildDto { MatchState = "unmatched", MatchReason = "No show on a provider matches this file yet." };
        var node = new MediaEditorNavigatorNodeDto { MatchState = "unmatched", MatchReason = "No show matches." };

        using var fileJson = JsonDocument.Parse(JsonSerializer.Serialize(file));
        using var nodeJson = JsonDocument.Parse(JsonSerializer.Serialize(node));

        Assert.Equal("unmatched", fileJson.RootElement.GetProperty("match_state").GetString());
        Assert.Equal("No show on a provider matches this file yet.", fileJson.RootElement.GetProperty("match_reason").GetString());
        Assert.Equal("unmatched", nodeJson.RootElement.GetProperty("match_state").GetString());
        Assert.Equal("No show matches.", nodeJson.RootElement.GetProperty("match_reason").GetString());

        var older = JsonSerializer.Deserialize<MediaEditorNavigatorNodeDto>("{\"entity_id\":\"00000000-0000-0000-0000-000000000001\"}");
        Assert.Equal("matched", older!.MatchState);
        Assert.Null(older.MatchReason);
    }
}
