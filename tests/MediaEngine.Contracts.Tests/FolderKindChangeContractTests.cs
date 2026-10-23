using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shape of "Change what this folder is" (Library setup spec D40(c), D60): the request, the preview and the job type.</summary>
public sealed class FolderKindChangeContractTests
{
    [Fact]
    public void TheRequest_NamesTheKindAndOptionallyTheLibrary_InSnakeCase_AndHasNoNewLibraryName()
    {
        var json = JsonSerializer.Serialize(new FolderKindChangeRequest { KindKey = "other_audio", LibraryId = "library-a" });

        Assert.Equal("{\"kind_key\":\"other_audio\",\"library_id\":\"library-a\"}", json);
        Assert.DoesNotContain("new_library_name", json, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<FolderKindChangeRequest>("{\"kind_key\":\"youtube\"}")!.LibraryId);
    }

    [Fact]
    public void ThePreview_CarriesTheCountsTheFlagsAndThePeopleWhoWouldLoseSight_InSnakeCase()
    {
        var preview = new FolderKindChangePreviewResponse
        {
            SourceId = "source-a",
            FromKindLabel = "Movies",
            ToKindKey = "other_videos",
            ToKindLabel = "Videos",
            ToLibraryId = "library-b",
            ToLibraryName = "Home videos",
            FileCount = 12,
            Moves = 12,
            DropsDetails = true,
            StartsFirstCheck = false,
            OtherAppsWarning = true,
            VisibilityLossChecked = true,
            VisibilityLoss = [new VisibilityLossDto(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Sam", 12)],
        };

        var json = JsonSerializer.Serialize(preview);
        var roundTrip = JsonSerializer.Deserialize<FolderKindChangePreviewResponse>(json)!;

        Assert.Contains("\"file_count\":12", json, StringComparison.Ordinal);
        Assert.Contains("\"moves\":12", json, StringComparison.Ordinal);
        Assert.Contains("\"drops_details\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"starts_first_check\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"other_apps_warning\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"visibility_loss_checked\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"visibility_loss\":[{\"account_id\":\"11111111-1111-1111-1111-111111111111\",\"display_name\":\"Sam\",\"file_count\":12}]", json, StringComparison.Ordinal);
        Assert.Equal(("Sam", 12), (roundTrip.VisibilityLoss.Single().DisplayName, roundTrip.VisibilityLoss.Single().FileCount));
    }

    [Fact]
    public void APreviewThatCouldNotCheckVisibility_SaysSo_RatherThanCarryingAnEmptyListAsAnAnswer()
    {
        var unanswered = new FolderKindChangePreviewResponse { VisibilityMessage = "Library couldn't work out who would lose sight of these files." };

        Assert.False(unanswered.VisibilityLossChecked);
        Assert.Empty(unanswered.VisibilityLoss);
        Assert.Contains("\"visibility_loss_checked\":false", JsonSerializer.Serialize(unanswered), StringComparison.Ordinal);
    }

    [Fact]
    public void TheJobStatus_ReadsAChangeKindJobType_WithTheOtherFolderJobFieldsUnchanged()
    {
        var status = JsonSerializer.Deserialize<FolderJobStatusDto>(
            "{\"operation_id\":\"op\",\"source_id\":\"source-a\",\"job_type\":\"change_folder_kind\",\"state\":\"running\",\"items_total\":10,\"items_completed\":4}")!;

        Assert.Equal("change_folder_kind", status.JobType);
        Assert.Equal(("running", 10, 4), (status.State, status.ItemsTotal, status.ItemsCompleted));
    }
}
