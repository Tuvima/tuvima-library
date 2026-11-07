using System.Text.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the supporting-files browser (universal editor v5 WP4): the list, the bulk actions and the search hit.</summary>
public sealed class SupportingFileBrowserContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ListDefaults_AreTheDocumentedPageSizeAndLimits()
    {
        Assert.Equal(25, SupportingFileListDefaults.PageSize);
        Assert.Equal(200, SupportingFileListDefaults.MaxPageSize);
        Assert.Equal(
            ["document", "picture", "clip", "audio"],
            [
                SupportingFileListDefaults.TypeDocument,
                SupportingFileListDefaults.TypePicture,
                SupportingFileListDefaults.TypeClip,
                SupportingFileListDefaults.TypeAudio,
            ]);
    }

    [Fact]
    public void TheList_RoundTrips_WithRowsGroupsUngroupedAndWhereOptions()
    {
        var list = new SupportingFileListDto
        {
            Items = [new SupportingFileRowDto { Id = "a", Title = "Worksheet 1", WhereKey = "season:2", WhereLabel = "Season 2", Tags = ["maths"] }],
            Total = 34,
            TotalAll = 238,
            Page = 2,
            PageSize = 25,
            PageCount = 2,
            Groups = [new SupportingFileGroupDto { Id = "g", Name = "Homework", FileCount = 25 }],
            UngroupedCount = 213,
            WhereOptions = [new SupportingFileWhereOptionDto { Key = "season:2", Label = "Season 2", Level = "season", FileCount = 34, ParentKey = null }],
        };

        var json = JsonSerializer.Serialize(list, Web);
        var back = JsonSerializer.Deserialize<SupportingFileListDto>(json, Web)!;

        Assert.Contains("\"totalAll\":238", json, StringComparison.Ordinal);
        Assert.Contains("\"ungroupedCount\":213", json, StringComparison.Ordinal);
        Assert.Contains("\"whereOptions\":[{\"key\":\"season:2\"", json, StringComparison.Ordinal);
        Assert.Equal(238, back.TotalAll);
        Assert.Equal("Homework", Assert.Single(back.Groups).Name);
        Assert.Equal(["maths"], Assert.Single(back.Items).Tags);
        Assert.Equal(34, Assert.Single(back.WhereOptions).FileCount);
    }

    [Fact]
    public void ABulkUpdateRequest_ReadsFromTheNamesTheDashboardSends_AndANewRequestChangesNothingByDefault()
    {
        var request = JsonSerializer.Deserialize<SupportingFileBulkUpdateRequest>(
            """{"ids":["a","b"],"changes":{"groupName":"Homework","scopeKey":"season:2","addTags":["maths"],"removeTags":["old"]},"preview":true}""",
            Web)!;

        Assert.Equal(["a", "b"], request.Ids);
        Assert.Equal("Homework", request.Changes.GroupName);
        Assert.Equal("season:2", request.Changes.ScopeKey);
        Assert.Equal(["maths"], request.Changes.AddTags);
        Assert.Equal(["old"], request.Changes.RemoveTags);
        Assert.True(request.Preview);

        var empty = new SupportingFileBulkChangesDto();
        Assert.Null(empty.GroupName);
        Assert.False(empty.ClearGroup);
        Assert.Null(empty.Role);
        Assert.Null(empty.ScopeKey);
        Assert.False(empty.UseFolderLocation);
        Assert.Empty(empty.AddTags);
        Assert.Empty(empty.RemoveTags);
        Assert.False(new SupportingFileBulkUpdateRequest().Preview);
    }

    [Fact]
    public void ABulkUpdateResult_CarriesPerFileStatuses_AndThePlannedMoves()
    {
        var result = new SupportingFileBulkUpdateResultDto
        {
            Preview = true,
            Changed = 1,
            Results = [new SupportingFileBulkItemResultDto { Id = "a", Status = SupportingFileBulkStatuses.Changed }],
            Moves = [new SupportingFilePlannedMoveDto { Id = "a", From = "/lib/guide.pdf", To = "/lib/Season 02/guide.pdf" }],
        };

        var json = JsonSerializer.Serialize(result, Web);
        var back = JsonSerializer.Deserialize<SupportingFileBulkUpdateResultDto>(json, Web)!;

        Assert.Equal(
            ["changed", "unchanged", "not_found", "refused", "removed"],
            [
                SupportingFileBulkStatuses.Changed,
                SupportingFileBulkStatuses.Unchanged,
                SupportingFileBulkStatuses.NotFound,
                SupportingFileBulkStatuses.Refused,
                SupportingFileBulkStatuses.Removed,
            ]);
        Assert.Equal(
            ["planned", "moved", "failed"],
            [
                SupportingFileMoveStatuses.Planned,
                SupportingFileMoveStatuses.Moved,
                SupportingFileMoveStatuses.Failed,
            ]);
        Assert.True(back.Preview);
        Assert.Equal(SupportingFileMoveStatuses.Planned, Assert.Single(back.Moves).Status);
        Assert.Equal("/lib/Season 02/guide.pdf", Assert.Single(back.Moves).To);
    }

    [Fact]
    public void ABulkDeleteResult_SaysWhatHappenedToEachFileOnDisk()
    {
        Assert.Equal(
            ["deleted", "recycled", "kept", "gone"],
            [
                SupportingFileDiskOutcomes.Deleted,
                SupportingFileDiskOutcomes.Recycled,
                SupportingFileDiskOutcomes.Kept,
                SupportingFileDiskOutcomes.Gone,
            ]);

        var json = JsonSerializer.Serialize(
            new SupportingFileBulkDeleteResultDto
            {
                Removed = 1,
                Results = [new SupportingFileDeleteItemResultDto { Id = "a", Disk = SupportingFileDiskOutcomes.Kept, Reason = "Read-only folder" }],
            },
            Web);

        Assert.Contains("\"removed\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"disk\":\"kept\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ASearchResult_CarriesTheSupportingFileHit_OnlyWhenItIsOne()
    {
        var plain = new UniversalSearchResultDto(Guid.NewGuid(), "work", null, "Title", null, null, null, null, null, "/x", "Open", "title", 0.5);
        Assert.Null(plain.SupportingFile);

        var owner = Guid.NewGuid();
        var hit = plain with
        {
            EntityType = "supporting-file",
            SupportingFile = new SupportingFileSearchHitDto
            {
                OwnerId = owner,
                OwnerName = "The Last Signal",
                ScopeLabel = "Season 2",
                Type = "document",
                TypeLabel = "PDF",
                FileName = "guide.pdf",
            },
        };
        var back = JsonSerializer.Deserialize<UniversalSearchResultDto>(JsonSerializer.Serialize(hit, Web), Web)!;

        Assert.Equal(owner, back.SupportingFile!.OwnerId);
        Assert.Equal("Season 2", back.SupportingFile.ScopeLabel);
        Assert.Equal("guide.pdf", back.SupportingFile.FileName);
    }
}
