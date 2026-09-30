using Bunit;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.MediaEditor;

namespace MediaEngine.Web.Tests;

public sealed class RetailHierarchyImpactNoticeTests : AsyncBunitContext
{
    [Fact]
    public void DisplaysTypedHierarchyPathsOnlyForAnApplicableMove()
    {
        var noPreview = Render<RetailHierarchyImpactNotice>(parameters => parameters
            .Add(component => component.Preview, null));
        Assert.Empty(noPreview.FindAll(".sme-match-hierarchy-impact-notice"));

        var sameParent = Render<RetailHierarchyImpactNotice>(parameters => parameters.Add(component => component.Preview, new MediaEditorMembershipPreviewDto
        {
            Action = "none",
            CanApply = true,
            CurrentPath = "Show / Season 1 / Episode 1",
            TargetPath = "Show / Season 1 / Episode 1",
        }));
        Assert.Empty(sameParent.FindAll(".sme-match-hierarchy-impact-notice"));

        var blocked = Render<RetailHierarchyImpactNotice>(parameters => parameters.Add(component => component.Preview, new MediaEditorMembershipPreviewDto
        {
            Action = "move_child",
            CanApply = false,
            CurrentPath = "Old Show / Season 1 / Pilot",
            TargetPath = "New Show / Season 2 / Pilot",
        }));
        Assert.Empty(blocked.FindAll(".sme-match-hierarchy-impact-notice"));

        var cut = Render<RetailHierarchyImpactNotice>(parameters => parameters.Add(component => component.Preview, new MediaEditorMembershipPreviewDto
        {
            Action = "move_child",
            CanApply = true,
            CurrentPath = "Old Show / Season 1 / Pilot",
            TargetPath = "New Show / Season 2 / Pilot",
        }));

        var notice = cut.Find(".sme-match-hierarchy-impact-notice");
        Assert.Equal("status", notice.GetAttribute("role"));
        Assert.Contains("Old Show / Season 1 / Pilot", notice.TextContent, StringComparison.Ordinal);
        Assert.Contains("New Show / Season 2 / Pilot", notice.TextContent, StringComparison.Ordinal);
        Assert.Contains("→", notice.TextContent, StringComparison.Ordinal);
        Assert.Contains("Series, season, album, position, and related context updates are applied automatically", notice.TextContent, StringComparison.Ordinal);
        Assert.Contains("Apply selection is the only confirmation", notice.TextContent, StringComparison.Ordinal);
        Assert.Empty(notice.QuerySelectorAll("button"));
    }

    [Fact]
    public void HidesNoticeWhenPathsAreTheSameForAnOtherwiseApplicableMove()
    {
        var cut = Render<RetailHierarchyImpactNotice>(parameters => parameters.Add(component => component.Preview, new MediaEditorMembershipPreviewDto
        {
            Action = "move_child",
            CanApply = true,
            CurrentPath = "Album / Track 1",
            TargetPath = "album / track 1",
        }));

        Assert.Empty(cut.FindAll(".sme-match-hierarchy-impact-notice"));
    }

    [Fact]
    public void DisplaysSeriesPlacementPathsForNonTvAndMusicMedia()
    {
        var cut = Render<RetailHierarchyImpactNotice>(parameters => parameters.Add(component => component.Preview, new MediaEditorMembershipPreviewDto
        {
            Action = "move_child",
            CanApply = true,
            CurrentPath = "The Book",
            TargetPath = "The Series / #2 The Book",
        }));

        Assert.Contains("The Book", cut.Find(".sme-match-hierarchy-impact-notice").TextContent, StringComparison.Ordinal);
        Assert.Contains("The Series / #2 The Book", cut.Find(".sme-match-hierarchy-impact-notice").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplainsWhenMovingTheLastChildLeavesTheOriginalParentForReviewedCleanup()
    {
        var cut = Render<RetailHierarchyImpactNotice>(parameters => parameters.Add(component => component.Preview, new MediaEditorMembershipPreviewDto
        {
            Action = "move_child",
            CanApply = true,
            CurrentPath = "Former Album / Last Track",
            TargetPath = "Correct Album / Last Track",
            SourceParentWillBeEmpty = true,
        }));

        var cleanup = cut.Find(".sme-match-hierarchy-impact-notice__cleanup");
        Assert.Contains("original parent will be empty", cleanup.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("later reviewed cleanup", cleanup.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetailPreviewPolicyAllowsNoOpsAndBlocksMissingOrConflictingRequiredPreviews()
    {
        Assert.True(RetailHierarchyPreviewPolicy.CanApply(
            previewRequired: false,
            loading: false,
            candidatePreviewMatches: false,
            preview: null));

        var unavailableStatus = RetailHierarchyPreviewPolicy.GetStatus(
            previewRequired: true,
            loading: false,
            candidatePreviewMatches: true,
            preview: null);
        Assert.False(RetailHierarchyPreviewPolicy.CanApply(true, false, true, null));
        Assert.Contains("unavailable", unavailableStatus, StringComparison.OrdinalIgnoreCase);

        var noOpPreview = new MediaEditorMembershipPreviewDto
        {
            Action = "none",
            CanApply = false,
            CurrentPath = "Album / Track 1",
            TargetPath = "Album / Track 1",
            Message = "No structural change is needed.",
        };
        Assert.True(RetailHierarchyPreviewPolicy.CanApply(true, false, true, noOpPreview));
        Assert.Null(RetailHierarchyPreviewPolicy.GetStatus(true, false, true, noOpPreview));

        var conflictPreview = new MediaEditorMembershipPreviewDto
        {
            Action = "conflict",
            CanApply = false,
            CurrentPath = "Old Series / #1",
            TargetPath = "New Series / #1",
            Message = "The requested position is occupied.",
            ConflictMessage = "Another owned item already uses that ordinal.",
        };
        Assert.False(RetailHierarchyPreviewPolicy.CanApply(true, false, true, conflictPreview));
        Assert.Contains("Another owned item already uses that ordinal", RetailHierarchyPreviewPolicy.GetStatus(true, false, true, conflictPreview), StringComparison.Ordinal);
    }
}
