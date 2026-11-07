using MediaEngine.Contracts.Details;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// How a bulk action on ticked supporting files becomes staged changes (universal editor v5 WP8, A7): one stage per file, a value that equals
/// what the file already is stages nothing, and Review reads one line per change. Pure; nothing reaches the Engine.
/// </summary>
public sealed class SupportingFileBulkStagingTests
{
    private static readonly Guid TitleId = Guid.Parse("c1000000-0000-0000-0000-000000000001");

    private static SupportingFileRowDto Row(string id = "c1000000-0000-0000-0000-0000000000a1", string? group = null, string role = "workbook", string where = "root", params string[] tags) => new()
    {
        Id = id,
        Title = "Workbook",
        Group = group,
        Role = role,
        RoleLabel = role,
        WhereKey = where,
        WhereLabel = where == "root" ? "Title level" : where,
        Tags = tags,
    };

    [Fact]
    public void SetGroup_StagesTheGroup_AndANewNameCreatesIt()
    {
        var stage = SupportingFileBulkStaging.SetGroup(TitleId, Row(), null, "Guides", ["Warm-ups"]);

        Assert.Equal("Guides", stage.GroupName);
        Assert.True(stage.CreatesGroup);
        var line = Assert.Single(stage.Rows, row => row.Key == SupportingFileFields.Group);
        Assert.Equal("Guides", line.After);
    }

    [Fact]
    public void SetGroup_ToTheGroupTheFileIsAlreadyIn_StagesNothing()
    {
        var stage = SupportingFileBulkStaging.SetGroup(TitleId, Row(group: "Guides"), null, "guides", ["Guides"]);

        Assert.Empty(stage.Rows);
    }

    [Fact]
    public void SetGroup_ToNothing_TakesAGroupedFileOutOfItsGroup()
    {
        var stage = SupportingFileBulkStaging.SetGroup(TitleId, Row(group: "Guides"), null, null, ["Guides"]);

        Assert.True(stage.NoGroup);
    }

    [Fact]
    public void SetRole_ToTheRoleItHas_StagesNothing_AndToAnotherRole_StagesIt()
    {
        Assert.Empty(SupportingFileBulkStaging.SetRole(TitleId, Row(), null, "workbook", "Workbook").Rows);

        var stage = SupportingFileBulkStaging.SetRole(TitleId, Row(), null, "transcript", "Transcript");

        Assert.Equal("transcript", stage.Role);
    }

    [Fact]
    public void AddTags_AddsOnlyTheTagsTheFileLacks()
    {
        var stage = SupportingFileBulkStaging.AddTags(TitleId, Row(tags: "pdf"), null, ["PDF", "week 1"]);

        Assert.Equal(["week 1"], stage.AddTags);
    }

    [Fact]
    public void SetAddedTags_ReplacesWhatWasStaged_SoATagTakenOutIsNotAdded()
    {
        var first = SupportingFileBulkStaging.SetAddedTags(TitleId, Row(), null, ["a", "b"]);

        var second = SupportingFileBulkStaging.SetAddedTags(TitleId, Row(), first, ["b"]);

        Assert.Equal(["b"], second.AddTags);
    }

    [Fact]
    public void SetScope_ToTheSamePlace_StagesNothing_AndToAnotherPlace_StagesTheMove()
    {
        Assert.Empty(SupportingFileBulkStaging.SetScope(TitleId, Row(), null, "root", "Title level").Rows);

        var stage = SupportingFileBulkStaging.SetScope(TitleId, Row(), null, "season:2", "Season 2");

        Assert.Equal("season:2", stage.ScopeKey);
        Assert.Contains(stage.Rows, row => row.Key == SupportingFileFields.Scope && row.After == "Season 2");
    }

    [Fact]
    public void ThePlannedMove_ReachesTheStage_SoTheFooterAndReviewCanSayFromWhereToWhere()
    {
        var staged = SupportingFileBulkStaging.SetScope(TitleId, Row(), null, "season:2", "Season 2");

        var moved = SupportingFileBulkStaging.WithPlannedMove(staged, new SupportingFilePlannedMoveDto { From = "A/Workbook.pdf", To = "Season 02/Workbook.pdf" });

        Assert.True(moved.HasMove);
        Assert.False(SupportingFileBulkStaging.WithPlannedMove(moved, null).HasMove);
    }

    [Fact]
    public void Shared_ReadsTheCommonValue_AndIsNullWhenTheFilesDiffer()
    {
        var rows = new[] { Row("1", group: "Guides"), Row("2", group: "Guides") };
        Assert.Equal("Guides", SupportingFileBulkStaging.Shared(rows, row => SupportingFileBulkStaging.GroupOf(row, null)));

        var mixed = new[] { Row("1", group: "Guides"), Row("2") };
        Assert.Null(SupportingFileBulkStaging.Shared(mixed, row => SupportingFileBulkStaging.GroupOf(row, null)));
    }

    [Fact]
    public void SetAlongside_StagesAnEpisode_AndBlankTakesItBack()
    {
        var stage = SupportingFileBulkStaging.SetAlongside(TitleId, Row(), null, "episode:2:3", "Episode 3");
        Assert.Equal("episode:2:3", stage.AlongsideKey);

        Assert.Empty(SupportingFileBulkStaging.SetAlongside(TitleId, Row(), stage, null, string.Empty).Rows);
    }
}
