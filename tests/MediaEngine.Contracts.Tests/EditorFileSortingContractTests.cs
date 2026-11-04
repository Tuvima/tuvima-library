using System.Text.Json;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes behind "What is this file?" in Match &amp; Identity (universal editor v4, D20, WP7b).</summary>
public sealed class EditorFileSortingContractTests
{
    [Fact]
    public void FileSorting_UsesSnakeCaseNames_AndRoundTrips()
    {
        var id = Guid.NewGuid();
        var dto = new EditorFileSortingDto
        {
            EntityId = id,
            CanSort = true,
            FileName = "Season 1 trailer.mkv",
            TitleKind = EditorFileSortingTitleKinds.Show,
            TitleName = "Show",
            CanBeExtra = true,
            InTitleFolder = true,
            ExtraKinds = ["trailer", "other_clip"],
            Guess = new EditorFileGuessDto { ExtraKind = "trailer", Season = 1 },
        };

        var json = JsonSerializer.Serialize(dto);
        var back = JsonSerializer.Deserialize<EditorFileSortingDto>(json)!;

        Assert.Equal(
            "{\"entity_id\":\"" + id.ToString("D") + "\",\"can_sort\":true,\"file_name\":\"Season 1 trailer.mkv\",\"title_kind\":\"show\",\"title_name\":\"Show\","
            + "\"can_be_extra\":true,\"in_title_folder\":true,\"extra_kinds\":[\"trailer\",\"other_clip\"],"
            + "\"guess\":{\"extra_kind\":\"trailer\",\"season\":1,\"episode\":null,\"track\":null},\"folder_files\":[]}",
            json);
        Assert.Equal(1, back.Guess!.Season);
    }

    [Fact]
    public void ASeasonTrailerChoice_RoundTripsThroughItsToken()
    {
        var choice = new ExtraFileChoice(EditSessionExtraMove.ActionExtra, "trailer", 1, null, null, null);

        var token = EditSessionExtraMove.ToFileToken(choice);

        Assert.Equal("file:extra;kind=trailer;season=1", token);
        Assert.True(EditSessionExtraMove.TryParseFile(token, out var back));
        Assert.Equal(choice, back);
    }

    [Fact]
    public void AnAttachChoice_CarriesTheTitle_AndIgnoreCarriesNothing()
    {
        var title = Guid.NewGuid();
        var attach = EditSessionExtraMove.ToFileToken(new ExtraFileChoice(EditSessionExtraMove.ActionAttach, "document", null, null, null, title));

        Assert.Equal("file:attach;kind=document;title=" + title.ToString("D"), attach);
        Assert.True(EditSessionExtraMove.TryParseFile(attach, out var back));
        Assert.Equal(title, back!.TitleId);
        Assert.Equal("file:ignore", EditSessionExtraMove.ToFileToken(new ExtraFileChoice(EditSessionExtraMove.ActionIgnore, null, null, null, null, null)));
    }

    [Theory]
    [InlineData("file:extra;kind=nonsense")]
    [InlineData("file:extra;kind=trailer;episode=3")]
    [InlineData("file:extra;kind=trailer;season=1;track=2")]
    [InlineData("file:attach;kind=document")]
    [InlineData("file:extra;kind=trailer;title=00000000-0000-0000-0000-000000000001")]
    [InlineData("file:explode")]
    [InlineData("extras")]
    public void ANonsenseToken_IsRefused(string token)
    {
        Assert.False(EditSessionExtraMove.TryParseFile(token, out var choice));
        Assert.Null(choice);
    }

    [Fact]
    public void ExcludedFileReasons_NameTheIgnoredByYouReason()
    {
        Assert.Equal("ignored_by_you", ExcludedFileReasons.IgnoredByYou);
    }
}
