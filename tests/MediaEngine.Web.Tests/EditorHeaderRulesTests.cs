using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The pure rules behind the editor's v5 header (universal editor v5, 3a and D9, D10, D29): the gradient from the cover's colours, the facts
/// line, the words of the item dropdown, the footer's file status, the file facts in Quick facts and the counts in the Delete confirmation.
/// </summary>
public sealed class EditorHeaderRulesTests
{
    // -- A4: the header gradient's colours ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheGradient_IsMadeFromTheStoredCoverColours()
    {
        var colours = EditorHeaderGradient.Resolve(new EditorCoverColorsDto { PrimaryHex = "#5a3a8a", SecondaryHex = "#2a1858" });

        Assert.Equal(new EditorHeaderColours("#5a3a8a", "#2a1858"), colours);
    }

    [Fact]
    public void ACoverWithNoStoredColours_GetsNoGradient_SoTheHeaderIsThePlainWindowSurface()
    {
        Assert.Null(EditorHeaderGradient.Resolve(null));
        Assert.Null(EditorHeaderGradient.Resolve(new EditorCoverColorsDto()));
    }

    [Fact]
    public void AColourThatIsNotAPlainHexValue_IsNeverPassedOn()
    {
        Assert.Null(EditorHeaderGradient.Resolve(new EditorCoverColorsDto { PrimaryHex = "red;background:url(x)" }));
    }

    [Fact]
    public void WithOnlyOneUsableColour_TheGradientUsesItForBothEnds()
    {
        Assert.Equal(new EditorHeaderColours("#2a1858", "#2a1858"), EditorHeaderGradient.Resolve(new EditorCoverColorsDto { SecondaryHex = "#2a1858" }));
        Assert.Equal(new EditorHeaderColours("#112233", "#112233"), EditorHeaderGradient.Resolve(new EditorCoverColorsDto { AccentHex = "#112233" }));
    }

    // -- The facts line --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFactsLine_ListsTheKindsPiecesInOrder_AndLeavesOutWhatNothingKnows()
    {
        var header = EditorHeaderTestData.TvEpisode();
        var fields = new Dictionary<string, string> { ["episode_number"] = "3", ["air_date"] = "2021-10-22" };

        var parts = EditorHeaderFacts.Parts(header.Config, keys => keys.Select(key => fields.GetValueOrDefault(key)).FirstOrDefault(value => value is not null), null, header);

        Assert.Equal(["Episode 3", "2021-10-22"], parts);
    }

    [Fact]
    public void TheMatchState_IsAChipBesideTheFactsLine_NotAPieceOfTheText()
    {
        var header = EditorHeaderTestData.TvEpisode();

        Assert.True(EditorHeaderFacts.ShowsMatchState(header.Config));
        Assert.DoesNotContain(EditorHeaderFacts.MatchState, EditorHeaderFacts.Parts(header.Config, _ => null, null, header));
        Assert.False(EditorHeaderFacts.ShowsMatchState(new EditorHeaderConfigDto { FactsLine = ["year"] }));
    }

    [Fact]
    public void AShowsFactsLine_CountsItsSeasonsAndEpisodes_FromTheTotals()
    {
        var config = new EditorHeaderConfigDto { FactsLine = ["season_count", "episode_count", "total_runtime"] };
        var totals = new ItemQuickFactsDto { DirectTotal = 2, LeafTotal = 24, RuntimeSeconds = 24 * 3000 };

        var parts = EditorHeaderFacts.Parts(config, _ => null, totals);

        Assert.Equal("2 seasons", parts[0]);
        Assert.Equal("24 episodes", parts[1]);
        Assert.Equal(3, parts.Count);
    }

    // -- A1: the words of the item dropdown ------------------------------------------------------------------------------------------

    [Fact]
    public void AnEpisodeRow_ShowsItsSeasonAndEpisodeCode()
    {
        Assert.Equal("S02E03", EditorNavText.Code(EditorHeaderTestData.Episode(EditorHeaderTestData.EpisodeThreeId, "S02E03", "Echoes in the Dark")));
    }

    [Fact]
    public void ALessonRow_ShowsItsNumberAndLength_NeverASeasonAndEpisodeCode()
    {
        var lesson = new EditorNavItemDto { Id = Guid.NewGuid(), Kind = "video", Number = 2, Title = "Graded wash", DurationSeconds = 840 };

        var code = EditorNavText.Code(lesson);

        Assert.Equal("02 · 14 min", code);
        Assert.DoesNotContain("S0", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemWithNoCode_SaysWhatItIs()
    {
        Assert.Equal("This movie", EditorNavText.Code(new EditorNavItemDto { Kind = "movie", Title = "Dune: Part One" }));
        Assert.Equal("This book", EditorNavText.Code(new EditorNavItemDto { Kind = "book", Title = "Dune" }));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0d, null)]
    [InlineData(30d, "1 min")]
    [InlineData(720d, "12 min")]
    [InlineData(3900d, "1 h 5 min")]
    public void ALength_IsWrittenInPlainWords(double? seconds, string? expected)
    {
        Assert.Equal(expected, EditorNavText.Length(seconds));
    }

    [Fact]
    public void TheDropdownsWord_FollowsTheKind_SoACourseNeverSaysEpisode()
    {
        Assert.Equal("episode", EditorNavText.ChooseWord(EditorHeaderTestData.TvEpisode()));
        Assert.Equal("video", EditorNavText.ChooseWord(EditorHeaderTestData.CourseLesson()));
        Assert.Equal("song", EditorNavText.ChooseWord(new EditorHeaderDto { Kind = EditorKindNames.Album }));
    }

    private static EditorHeaderDto SeriesOf(string itemKind) => new()
    {
        Kind = EditorKindNames.Series,
        Level = new EditorHeaderLevelDto { Items = [new EditorNavItemDto { Id = Guid.NewGuid(), Kind = itemKind, Title = "One" }] },
    };

    [Theory]
    [InlineData("movie", "movie")]
    [InlineData("book", "book")]
    [InlineData("audiobook", "audiobook")]
    [InlineData("comic", "comic")]
    [InlineData("track", "song")]
    public void ASeriesDropdown_SaysTheWordOfItsOwnMembers_NotOneWordForEverySeries(string itemKind, string expected)
    {
        Assert.Equal(expected, EditorNavText.ChooseWord(SeriesOf(itemKind)));
    }

    [Fact]
    public void ASeriesWithNoMembersRead_SaysItemRatherThanGuessingBook()
    {
        Assert.Equal("item", EditorNavText.ChooseWord(new EditorHeaderDto { Kind = EditorKindNames.Series }));
    }

    [Fact]
    public void ASupportingFilesRow_SaysItsGroupAndWhereItIsShown()
    {
        var withLesson = EditorHeaderTestData.SupportingFile(Guid.NewGuid(), "Script, S02E03", "ZIP", "archive", "Script", group: "Scripts", alongside: "with episode 3");
        var guide = new EditorHeaderSupportingFileDto { Title = "Character guide", RoleLabel = "Guide", PageCount = 18 };

        Assert.Equal("Scripts · with episode 3", EditorNavText.FileLine(withLesson));
        Assert.Equal("Guide · 18 pages", EditorNavText.FileLine(guide));
    }

    [Fact]
    public void AllSupportingFiles_SaysHowManyAreAtThisLevelAndInTheWholeTitle()
    {
        var header = EditorHeaderTestData.TvEpisode();

        Assert.Equal("12 in this season · 238 in the show", EditorNavText.AllFilesLine(header, header.Level.SupportingFiles));
        Assert.Equal("1 in all", EditorNavText.AllFilesLine(EditorHeaderTestData.Movie(true), EditorHeaderTestData.Movie(true).Level.SupportingFiles));
    }

    [Fact]
    public void ASeasonRowInTheSecondDropdown_CountsItsEpisodesAndSupportingFiles()
    {
        var season = EditorHeaderTestData.TvEpisode().SecondLevel!.Entries[1];

        Assert.Equal("8 episodes · 12 supporting files", EditorNavText.EntryLine(season, "episode"));
    }

    // -- A5: the footer's file status ---------------------------------------------------------------------------------------------------

    [Fact]
    public void WhenWriteBackIsOff_TheFooterSaysEditsStayInLibraryAndTheFilesAreUnchanged()
    {
        Assert.Equal(EditorFooterStatus.WriteBackOff, EditorFooterRules.For(new FileWriteSetting(FileWriteState.Off), new PendingEditList()).Status);
    }

    [Fact]
    public void WhenWriteBackIsOn_TheFooterSaysEditsAreAlsoWrittenIntoTheFiles()
    {
        Assert.Equal(EditorFooterStatus.WriteBackOn, EditorFooterRules.For(new FileWriteSetting(FileWriteState.On), null).Status);
    }

    [Fact]
    public void WhenTheSettingCannotBeRead_TheFooterClaimsNothingAboutTheFiles()
    {
        Assert.Equal(EditorFooterStatus.Unknown, EditorFooterRules.For(FileWriteSetting.Unknown, new PendingEditList()).Status);
    }

    [Fact]
    public void AStagedFileMove_ReplacesTheWriteBackSentenceWithTheAmberWarning_NamingFromAndTo()
    {
        var pending = new PendingEditList();
        pending.SetTarget(EditSessionChangeKinds.FileMove, Guid.NewGuid(), "target", label: "Shows/The Last Signal/Season 02/S02E03.mkv", before: "Inbox/ep3.mkv");

        var state = EditorFooterRules.For(new FileWriteSetting(FileWriteState.On), pending);

        Assert.Equal(EditorFooterStatus.WillMove, state.Status);
        Assert.Equal("Inbox/ep3.mkv", state.MoveFrom);
        Assert.Equal("Shows/The Last Signal/Season 02/S02E03.mkv", state.MoveTo);
    }

    [Fact]
    public void AStagedFileMoveWithNoPathsRecorded_StillWarns_WithAGenericLine()
    {
        var pending = new PendingEditList();
        pending.SetTarget(EditSessionChangeKinds.FileMove, Guid.NewGuid(), "target");

        var state = EditorFooterRules.For(FileWriteSetting.Unknown, pending);

        Assert.Equal(EditorFooterStatus.WillMove, state.Status);
        Assert.Null(state.MoveFrom);
        Assert.Null(state.MoveTo);
    }

    [Fact]
    public void DiscardingTheMove_BringsTheWriteBackSentenceBack()
    {
        var pending = new PendingEditList();
        pending.SetTarget(EditSessionChangeKinds.FileMove, Guid.NewGuid(), "target");
        pending.Discard();

        Assert.Equal(EditorFooterStatus.WriteBackOff, EditorFooterRules.For(new FileWriteSetting(FileWriteState.Off), pending).Status);
    }

    // -- Quick facts gain the file facts ---------------------------------------------------------------------------------------------------

    private static QuickFactsModel Episode() => new(QuickFactsKind.Episode, [], []);

    [Fact]
    public void QuickFacts_GainTheFileFactsTheKindAsksFor_AndTheSupportingFileCount()
    {
        var model = EditorQuickFactsBuilder.WithFileFacts(Episode(), EditorHeaderTestData.TvEpisode(), "TheTVDB");

        Assert.Equal("Matched with TheTVDB", model.Facts.Single(fact => fact.Key == "match_state").Value);
        Assert.Equal("1080p", model.Facts.Single(fact => fact.Key == "file_quality").Value);
        Assert.Equal("MKV", model.Facts.Single(fact => fact.Key == "container").Value);
        Assert.Equal("Season 02", model.Facts.Single(fact => fact.Key == "folder").Value);
        Assert.Equal("238", model.Facts.Single(fact => fact.Key == "supporting_file_count").Value);
    }

    [Fact]
    public void QuickFacts_LeaveOutAFactTheKindsListDoesNotAskFor()
    {
        var header = EditorHeaderTestData.TvEpisode();
        var slim = new EditorHeaderDto
        {
            Kind = header.Kind,
            Root = header.Root,
            CurrentId = header.CurrentId,
            Level = header.Level,
            FileFacts = header.FileFacts,
            Config = new EditorHeaderConfigDto { Kind = header.Kind, QuickFacts = ["container"] },
        };

        var model = EditorQuickFactsBuilder.WithFileFacts(Episode(), slim);

        Assert.Equal(["container"], model.Facts.Select(fact => fact.Key));
    }

    [Fact]
    public void AMissingFile_IsNamedWithItsLastKnownFolder()
    {
        var header = new EditorHeaderDto
        {
            Kind = EditorKindNames.Movie,
            Level = new EditorHeaderLevelDto { SupportingFiles = new EditorHeaderSupportingFilesDto() },
            FileFacts = new EditorFileFactsDto { IsMissing = true, Folder = "Dune (2021)" },
            Config = EditorHeaderTestData.Config(EditorKindNames.Movie),
        };

        var model = EditorQuickFactsBuilder.WithFileFacts(new QuickFactsModel(QuickFactsKind.Film, [], []), header);

        Assert.Contains($"{EditorQuickFactsBuilder.FileNotFoundNote} · last known folder Dune (2021)", model.Notes);
    }

    [Fact]
    public void AFileInAReadOnlyFolder_CarriesTheReadOnlyNote()
    {
        var header = new EditorHeaderDto
        {
            Kind = EditorKindNames.Movie,
            Level = new EditorHeaderLevelDto { SupportingFiles = new EditorHeaderSupportingFilesDto() },
            FileFacts = new EditorFileFactsDto { ReadOnly = true, Container = "MKV" },
            Config = EditorHeaderTestData.Config(EditorKindNames.Movie),
        };

        var model = EditorQuickFactsBuilder.WithFileFacts(new QuickFactsModel(QuickFactsKind.Film, [], []), header);

        Assert.Contains(EditorQuickFactsBuilder.ReadOnlyFolderNote, model.Notes);
    }

    [Fact]
    public void AnItemWithNoHeader_KeepsItsQuickFactsAsTheyWere()
    {
        var model = Episode();

        Assert.Same(model, EditorQuickFactsBuilder.WithFileFacts(model, null));
    }

    // -- D29: the Delete confirmation counts ----------------------------------------------------------------------------------------------

    [Fact]
    public void DeletingAShow_CountsEverySupportingFileOfTheTitle_AndASeasonOnlyItsOwn()
    {
        var header = EditorHeaderTestData.TvEpisode();
        var show = EditorDeleteRules.GroupFor(EditorHeaderTestData.ShowId, EditorHeaderTestData.ShowTitle, "series", "TV")!;
        var season = EditorDeleteRules.GroupFor(EditorHeaderTestData.SeasonTwoId, "Season 2", "season", "TV")!;

        Assert.Equal(238, EditorDeleteRules.SupportingFilesIn(show, header));
        Assert.Equal(12, EditorDeleteRules.SupportingFilesIn(season, header));
    }

    [Fact]
    public void WithNoHeaderRead_TheConfirmationLeavesTheSupportingFilesClauseOut()
    {
        var show = EditorDeleteRules.GroupFor(EditorHeaderTestData.ShowId, EditorHeaderTestData.ShowTitle, "series", "TV")!;

        Assert.Equal(0, EditorDeleteRules.SupportingFilesIn(show, null));
    }

    [Fact]
    public void TheFootersDeleteButton_IsAlwaysTheOneWordDelete()
    {
        Assert.Equal("Delete", EditorDeleteRules.FooterLabel);
    }

    [Fact]
    public void TheTabsFixedOrder_IsDetailsArtworkMatchPreviewBookmarksHistory_WhateverTheConfigListingOrder()
    {
        Assert.Equal(["details", "artwork", "pictures", "match", "preview", "bookmarks", "history"], EditorTabRules.Order);
    }

    // -- Tabs: the kind says which, in the fixed order ----------------------------------------------------------------------------------

    [Fact]
    public void TheStandardTabs_AreDetailsArtworkMatchHistory_AndNeverContentsOrFiles()
    {
        Assert.Equal(["details", "artwork", "match", "history"], EditorTabRules.Standard);
        Assert.DoesNotContain("contents", EditorTabRules.Standard);
        Assert.DoesNotContain("files", EditorTabRules.Standard);
    }
}
