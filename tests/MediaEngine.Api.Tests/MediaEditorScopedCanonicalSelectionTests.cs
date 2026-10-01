using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Api.Tests;

public sealed class MediaEditorScopedCanonicalSelectionTests
{
    [Fact]
    public void WorkLaunch_DoesNotChooseBetweenConflictingEditionTitleAndRuntime()
    {
        var work = BuildTwoEditionWork();

        var selected = MetadataEndpoints.EnumerateScopedCanonicalValues(work).ToList();
        var selectedOwners = MetadataEndpoints.EnumerateScopedCanonicalOwners(work).ToList();

        Assert.DoesNotContain(selected, field => field.OwnerEntityKind == "Edition");
        Assert.DoesNotContain(selected, field => field.OwnerEntityKind == "MediaAsset");
        Assert.Equal([(work.Id, "Work")], selectedOwners);
        Assert.Contains(selected, field => field.OwnerEntityKind == "Work" && field.Value.Key == "title");
    }

    [Fact]
    public void EditionLaunch_UsesOnlySelectedEditionValues()
    {
        var work = BuildTwoEditionWork();
        var selectedEditionId = work.Editions[1].Id;

        var selected = MetadataEndpoints.EnumerateScopedCanonicalValues(work, selectedEditionId).ToList();
        var selectedOwners = MetadataEndpoints.EnumerateScopedCanonicalOwners(work, selectedEditionId).ToList();

        Assert.Contains(selected, field => field.OwnerEntityId == selectedEditionId
            && field.Value.Key == "title" && field.Value.Value == "Extended Edition");
        Assert.Contains(selected, field => field.OwnerEntityId == selectedEditionId
            && field.Value.Key == "runtime" && field.Value.Value == "142");
        Assert.DoesNotContain(selected, field => field.OwnerEntityId == work.Editions[0].Id);
        Assert.DoesNotContain(selected, field => field.OwnerEntityKind == "MediaAsset");
        Assert.Equal([(work.Id, "Work"), (selectedEditionId, "Edition")], selectedOwners);
    }

    [Fact]
    public void AssetLaunch_UsesOnlySelectedAssetAndItsOwningEdition()
    {
        var work = BuildTwoEditionWork();
        var edition = work.Editions[1];
        var asset = edition.Assets[0];

        var selected = MetadataEndpoints.EnumerateScopedCanonicalValues(work, selectedAssetId: asset.Id).ToList();
        var selectedOwners = MetadataEndpoints.EnumerateScopedCanonicalOwners(work, selectedAssetId: asset.Id).ToList();

        Assert.Contains(selected, field => field.OwnerEntityId == edition.Id
            && field.Value.Key == "runtime" && field.Value.Value == "142");
        Assert.Contains(selected, field => field.OwnerEntityId == asset.Id
            && field.Value.Key == "episode_title" && field.Value.Value == "I'm Used to It");
        Assert.DoesNotContain(selected, field => field.OwnerEntityId == work.Editions[0].Id);
        Assert.DoesNotContain(selected, field => field.OwnerEntityId != asset.Id
            && field.OwnerEntityKind == "MediaAsset");
        Assert.Equal([(work.Id, "Work"), (edition.Id, "Edition"), (asset.Id, "MediaAsset")], selectedOwners);
    }

    [Fact]
    public void ContributorPersonIds_RequireExactContributorQidEvidence()
    {
        var knownPersonId = Guid.NewGuid();
        var arrays = new List<MediaEditorScopedFieldArrayDto>
        {
            new()
            {
                Key = "director",
                Entries = [new MediaEditorScopedArrayEntryDto { Value = "Same Name", ValueQid = "Q42" }],
            },
            new()
            {
                Key = "cast_member",
                Entries = [new MediaEditorScopedArrayEntryDto { Value = "Same Name" }],
            },
            new()
            {
                Key = "genre",
                Entries = [new MediaEditorScopedArrayEntryDto { Value = "Drama", ValueQid = "Q42" }],
            },
        };

        MetadataEndpoints.ApplyCanonicalContributorIds(
            arrays,
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["Q42"] = knownPersonId });

        Assert.Equal(knownPersonId, arrays[0].Entries[0].LocalPersonId);
        Assert.Equal("Q42", arrays[0].Entries[0].ValueQid);
        Assert.Null(arrays[1].Entries[0].LocalPersonId);
        Assert.Null(arrays[2].Entries[0].LocalPersonId);
    }

    private static WorkDetailDto BuildTwoEditionWork()
    {
        var workId = Guid.NewGuid();
        var firstEditionId = Guid.NewGuid();
        var secondEditionId = Guid.NewGuid();
        var siblingAssetId = Guid.NewGuid();
        var selectedAssetId = Guid.NewGuid();
        return new WorkDetailDto
        {
            Id = workId,
            CanonicalValues = [Field("title", "Canonical Work")],
            Editions =
            [
                new EditionDto
                {
                    Id = firstEditionId,
                    CanonicalValues = [Field("title", "Standard Edition"), Field("runtime", "120")],
                    Assets = [new EditionAssetDto { Id = siblingAssetId, EditionId = firstEditionId,
                        CanonicalValues = [Field("episode_title", "Sibling file title")] }],
                },
                new EditionDto
                {
                    Id = secondEditionId,
                    CanonicalValues = [Field("title", "Extended Edition"), Field("runtime", "142")],
                    Assets = [new EditionAssetDto { Id = selectedAssetId, EditionId = secondEditionId,
                        CanonicalValues = [Field("title", "Solo Leveling"), Field("episode_title", "I'm Used to It")] }],
                },
            ],
        };
    }

    private static CanonicalValueDto Field(string key, string value) => new() { Key = key, Value = value };
}
