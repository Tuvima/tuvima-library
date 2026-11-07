using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// What the editor stages for one supporting file and how it reads back (universal editor v5 WP7): one entry per file in the pending list,
/// the words Review and the footer use, and the rules for Group, Belongs to and Shown alongside. All pure: no Engine, no page.
/// </summary>
public sealed class SupportingFileStagingTests
{
    private static SupportingFileStage Fresh(EditorSupportingFileDto file) => SupportingFileStaging.Start(file);

    // -- Title, description, tags ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TypingATitle_StagesOneRow_AndTypingTheSavedTitleAgainTakesItBackOut()
    {
        var file = SupportingFileEditorTestData.File();

        var typed = SupportingFileStaging.SetTitle(file, Fresh(file), "  Shooting workbook ");
        var row = Assert.Single(typed.Rows);
        Assert.Equal((SupportingFileFields.Title, "Title", "Workbook", "Shooting workbook"), (row.Key, row.Label, row.Before, row.After));
        Assert.Equal("Shooting workbook", typed.ToRequest().Changes.Title);

        var back = SupportingFileStaging.SetTitle(file, typed, "Workbook");
        Assert.True(back.IsEmpty);
        Assert.Null(back.ToRequest().Changes.Title);
    }

    [Fact]
    public void ClearingATitleThePersonSet_GoesBackToTheNameReadFromTheFile()
    {
        var file = SupportingFileEditorTestData.File(ownTitle: "Shooting workbook");

        var cleared = SupportingFileStaging.SetTitle(file, Fresh(file), "   ");

        Assert.True(cleared.ToRequest().Changes.ClearTitle);
        Assert.Equal(SupportingFileStaging.FromFileName, Assert.Single(cleared.Rows).After);
        Assert.Equal(string.Empty, SupportingFileStaging.TitleOf(file, cleared));
    }

    [Fact]
    public void ClearingATitleTheFileNeverHad_StagesNothing()
    {
        var file = SupportingFileEditorTestData.File();

        Assert.True(SupportingFileStaging.SetTitle(file, Fresh(file), string.Empty).IsEmpty);
    }

    [Fact]
    public void ADescription_ReadsBackAsTyped_AndEmptyingItClearsWhatWasSaved()
    {
        var file = SupportingFileEditorTestData.File(description: "Drills for the week");

        var typed = SupportingFileStaging.SetDescription(file, Fresh(file), "Drills for the first week");
        Assert.Equal("Drills for the first week", SupportingFileStaging.DescriptionOf(file, typed));

        var cleared = SupportingFileStaging.SetDescription(file, typed, "");
        Assert.True(cleared.ToRequest().Changes.ClearDescription);
        Assert.Equal(EditReviewRows.NotSet, Assert.Single(cleared.Rows).After);
    }

    [Fact]
    public void Tags_AreStagedAsWhatWasAddedAndWhatWasTakenOff()
    {
        var file = SupportingFileEditorTestData.File(tags: ["drills", "week 1"]);

        var staged = SupportingFileStaging.SetTags(file, Fresh(file), ["Drills", "printable"]);

        Assert.Equal(["printable"], staged.AddTags);
        Assert.Equal(["week 1"], staged.RemoveTags);
        Assert.Equal(["drills", "printable"], SupportingFileStaging.TagsOf(file, staged).Select(tag => tag.ToLowerInvariant()));
        Assert.True(SupportingFileStaging.SetTags(file, staged, ["drills", "week 1"]).IsEmpty);
    }

    // -- Group ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AGroupThatExists_IsUsedAsItIs_AndDoesNotCreateAnything()
    {
        var file = SupportingFileEditorTestData.File();

        var staged = SupportingFileStaging.SetGroup(file, Fresh(file), "transcripts");

        Assert.Equal("Transcripts", staged.GroupName);
        Assert.False(staged.CreatesGroup);
        Assert.Equal(1, staged.ChangeCount);
    }

    [Fact]
    public void ANewGroup_IsCreatedOnSave_AndCountsAsASecondChange()
    {
        var file = SupportingFileEditorTestData.File();

        var staged = SupportingFileStaging.SetGroup(file, Fresh(file), "Warm-up sheets");

        Assert.True(staged.CreatesGroup);
        Assert.Equal("Warm-up sheets", staged.GroupName);
        Assert.Equal(2, staged.ChangeCount);
        Assert.Equal(SupportingFileStaging.NoGroup, Assert.Single(staged.Rows).Before);
    }

    [Fact]
    public void AGroupThePersonChose_AndOneReadFromAFolder_CanBothBeTakenOff()
    {
        var chosen = SupportingFileEditorTestData.File(group: "Documents", groupSource: "you");
        var cleared = SupportingFileStaging.SetGroup(chosen, Fresh(chosen), "");
        Assert.True(cleared.ToRequest().Changes.NoGroup);

        var fromFolder = SupportingFileEditorTestData.File(group: "Documents", groupSource: "folder");
        var none = SupportingFileStaging.SetGroup(fromFolder, Fresh(fromFolder), "");
        var request = none.ToRequest().Changes;
        Assert.True(request.NoGroup);
        Assert.Null(request.GroupName);
        Assert.False(request.ClearGroup);
        Assert.Equal(string.Empty, SupportingFileStaging.GroupOf(fromFolder, none));
        var row = Assert.Single(none.Rows);
        Assert.Equal(("Documents", SupportingFileStaging.NoGroup), (row.Before, row.After));
    }

    [Fact]
    public void ANoGroupChoice_IsTakenBackByPickingTheGroupItHad_AndAFileWithNoGroupHasNothingToTakeOff()
    {
        var fromFolder = SupportingFileEditorTestData.File(group: "Documents", groupSource: "folder");
        var none = SupportingFileStaging.SetGroup(fromFolder, Fresh(fromFolder), "");

        Assert.True(SupportingFileStaging.SetGroup(fromFolder, none, "Documents").IsEmpty);

        var ungrouped = SupportingFileEditorTestData.File();
        Assert.True(SupportingFileStaging.SetGroup(ungrouped, Fresh(ungrouped), "").IsEmpty);
    }

    [Fact]
    public void PickingAnotherGroup_NeverMovesAFile_SoItStagesOnlyTheGroupRow()
    {
        var file = SupportingFileEditorTestData.File(group: "Documents", groupSource: "folder");

        var staged = SupportingFileStaging.SetGroup(file, Fresh(file), "Transcripts");

        Assert.Equal([SupportingFileFields.Group], staged.Rows.Select(row => row.Key));
        Assert.False(staged.HasMove);
        Assert.Null(staged.ToRequest().Changes.ScopeKey);
    }

    // -- Belongs to ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ChoosingAnotherPlace_IsThePersonsOwn_AndShowsWhereItWas()
    {
        var file = SupportingFileEditorTestData.File();

        var staged = SupportingFileStaging.SetScope(file, Fresh(file), "season:1");

        Assert.Equal("season:1", SupportingFileStaging.ScopeOf(file, staged));
        Assert.True(SupportingFileStaging.ScopeIsSetByYou(file, staged));
        var row = Assert.Single(staged.Rows);
        Assert.Equal(("Belongs to", file.ScopeLabel, "Season 01"), (row.Label, row.Before, row.After));
    }

    [Fact]
    public void UseFolderLocation_ReturnsToThePlaceTheFolderSays_OnlyWhenThePersonHadSetAnother()
    {
        var setByYou = SupportingFileEditorTestData.File(scopeKey: "season:1", scopeSource: "you");
        var back = SupportingFileStaging.UseFolderLocation(setByYou, Fresh(setByYou));
        Assert.True(back.UseFolderLocation);
        Assert.Equal("season:2", SupportingFileStaging.ScopeOf(setByYou, back));
        Assert.False(SupportingFileStaging.ScopeIsSetByYou(setByYou, back));
        Assert.Null(back.ToRequest().Changes.ScopeKey);
        Assert.True(back.ToRequest().Changes.UseFolderLocation);

        var fromFolder = SupportingFileEditorTestData.File(scopeKey: "season:2");
        Assert.True(SupportingFileStaging.UseFolderLocation(fromFolder, Fresh(fromFolder)).IsEmpty);
    }

    [Fact]
    public void PickingThePlaceTheFolderSays_AfterTheOneThePersonSet_IsTheSameAsUseFolderLocation()
    {
        var file = SupportingFileEditorTestData.File(scopeKey: "season:1", scopeSource: "you");

        var staged = SupportingFileStaging.SetScope(file, Fresh(file), "season:2");

        Assert.True(staged.UseFolderLocation);
    }

    [Fact]
    public void TheMoveTheEngineReports_BelongsToBelongsToBelongsTo_AndGoesWhenBelongsToIsTakenOut()
    {
        var file = SupportingFileEditorTestData.File();
        var staged = SupportingFileStaging.SetScope(file, Fresh(file), "season:1");

        var moving = SupportingFileStaging.WithMove(staged, "Season 02/Documents/Workbook.pdf", "Season 01/Documents/Workbook.pdf");
        Assert.True(moving.HasMove);
        Assert.Equal(2, moving.ChangeCount);

        var withoutScope = moving.Without(SupportingFileFields.Move);
        Assert.False(withoutScope.HasMove);
        Assert.True(withoutScope.IsEmpty);

        // A move told about a file with no Belongs to staged is ignored: nothing moves for a title change.
        var titleOnly = SupportingFileStaging.SetTitle(file, Fresh(file), "New");
        Assert.False(SupportingFileStaging.WithMove(titleOnly, "a", "b").HasMove);
    }

    // -- Shown alongside ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ShownAlongside_OffersOnlyTheEpisodesUnderThePlaceTheFileBelongsTo()
    {
        var file = SupportingFileEditorTestData.File();

        Assert.Equal(["episode:s2e1", "episode:s2e3"], SupportingFileStaging.AlongsideChoices(file, "season:2").Select(place => place.Key));
        Assert.Equal(3, SupportingFileStaging.AlongsideChoices(file, "root").Count);
    }

    [Theory]
    [InlineData("root", true)]
    [InlineData("season:2", true)]
    [InlineData("episode:s2e3", false)]
    [InlineData("file:lesson-2", false)]
    [InlineData("track:4", false)]
    public void ShownAlongside_IsHiddenWhenTheFileAlreadyBelongsToAnEpisodeLessonOrTrack(string scope, bool shown) =>
        Assert.Equal(shown, SupportingFileStaging.ShowsAlongside(scope));

    [Fact]
    public void ShownAlongside_PickingTheGuessedEpisode_GoesBackToTheGuess_AndAnotherOneIsAChoice()
    {
        var file = SupportingFileEditorTestData.File(alongsideKey: "episode:s2e1", inferredAlongsideKey: "episode:s2e3");

        var guessed = SupportingFileStaging.SetAlongside(file, Fresh(file), "episode:s2e3");
        Assert.True(guessed.UseGuessedAlongside);
        Assert.True(SupportingFileStaging.AlongsideIsGuessed(file, guessed));

        var chosen = SupportingFileStaging.SetAlongside(file, Fresh(file), "episode:s1e1");
        Assert.Equal("episode:s1e1", chosen.AlongsideKey);
        Assert.False(SupportingFileStaging.AlongsideIsGuessed(file, chosen));

        var none = SupportingFileStaging.SetAlongside(file, Fresh(file), "");
        Assert.True(none.NoAlongside);
        Assert.Equal(string.Empty, SupportingFileStaging.AlongsideOf(file, none));
    }

    [Fact]
    public void MovingBelongsToAnEpisode_ClearsAStaleShownAlongside_AndMovingBackTakesTheClearingOut()
    {
        var file = SupportingFileEditorTestData.File(scopeKey: "season:2", alongsideKey: "episode:s2e1");

        var onEpisode = SupportingFileStaging.SetScope(file, Fresh(file), "episode:s2e3");
        Assert.Equal([SupportingFileFields.Scope, SupportingFileFields.Alongside], onEpisode.Rows.Select(row => row.Key));
        Assert.Equal(string.Empty, SupportingFileStaging.AlongsideOf(file, onEpisode));
        var request = onEpisode.ToRequest().Changes;
        Assert.True(request.UseGuessedAlongside);
        Assert.Null(request.AlongsideKey);

        var back = SupportingFileStaging.SetScope(file, onEpisode, "season:1");
        Assert.Equal([SupportingFileFields.Scope], back.Rows.Select(row => row.Key));
        Assert.Equal("episode:s2e1", SupportingFileStaging.AlongsideOf(file, back));
    }

    [Fact]
    public void MovingBelongsToAnEpisode_OverridesAGuessedShownAlongside_WithNone()
    {
        var file = SupportingFileEditorTestData.File(scopeKey: "season:2", inferredAlongsideKey: "episode:s2e3", alongsideKey: "episode:s2e3");

        var staged = SupportingFileStaging.SetScope(file, Fresh(file), "episode:s2e1");

        Assert.True(staged.ToRequest().Changes.NoAlongside);
    }

    [Fact]
    public void MovingBelongsToAnEpisode_LeavesAlone_AFileThatIsShownAlongsideNothing()
    {
        var file = SupportingFileEditorTestData.File(scopeKey: "season:2");

        var staged = SupportingFileStaging.SetScope(file, Fresh(file), "episode:s2e1");

        Assert.Equal([SupportingFileFields.Scope], staged.Rows.Select(row => row.Key));
    }

    [Fact]
    public void AValueTheFormChoosesAfterwards_IsNotUndoneByMovingAway()
    {
        var file = SupportingFileEditorTestData.File(scopeKey: "season:2", alongsideKey: "episode:s2e1");
        var cleared = SupportingFileStaging.SetScope(file, Fresh(file), "episode:s2e3");
        var chosen = SupportingFileStaging.SetAlongside(file, cleared, "episode:s2e3");

        var back = SupportingFileStaging.SetScope(file, chosen, "season:2");

        Assert.Equal("episode:s2e3", SupportingFileStaging.AlongsideOf(file, back));
    }

    [Theory]
    [InlineData("TV", "episode")]
    [InlineData("Videos", "lesson")]
    [InlineData("Music", "track")]
    public void TheItemsShownAlongsideAreCalledWhatTheTitleKindCallsThem(string mediaType, string noun) =>
        Assert.Equal(noun, SupportingFileStaging.AlongsideNoun(SupportingFileEditorTestData.File(mediaType: mediaType)));

    [Fact]
    public void TheSavedRequest_CarriesOnlyTheFileAndWhatWasStaged()
    {
        var file = SupportingFileEditorTestData.File();
        var staged = SupportingFileStaging.SetRole(file, SupportingFileStaging.SetTitle(file, Fresh(file), "Shooting workbook"), "guide");

        var request = staged.ToRequest();

        Assert.Equal([file.File.Id], request.Ids);
        Assert.Equal("Shooting workbook", request.Changes.Title);
        Assert.Equal("guide", request.Changes.Role);
        Assert.Null(request.Changes.ScopeKey);
        Assert.False(request.Changes.UseFolderLocation);
        Assert.Equal(["Title", "Role"], staged.Rows.Select(row => row.Label));
    }

    // -- The pending list -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void StagingAFileAgain_ReplacesItsEntry_AndAnEmptyStageTakesItOut()
    {
        var file = SupportingFileEditorTestData.File();
        var pending = new PendingEditList();

        pending.SetSupportingFile(SupportingFileStaging.SetTitle(file, Fresh(file), "One"));
        pending.SetSupportingFile(SupportingFileStaging.SetTitle(file, Fresh(file), "Two"));

        var change = Assert.Single(pending.Changes);
        Assert.Equal(EditSessionChangeKinds.SupportingFiles, change.Kind);
        Assert.Equal(SupportingFileEditorTestData.TitleId, change.EntityId);
        Assert.Equal(file.File.Id, change.TargetId);
        Assert.Equal("Two", change.SupportingFiles!.Changes.Title);

        pending.SetSupportingFile(Fresh(file));
        Assert.True(pending.IsEmpty);
    }

    [Fact]
    public void TwoFiles_AreTwoEntries_AndTheCountIsWhatSaveWouldDo()
    {
        var one = SupportingFileEditorTestData.File();
        var two = SupportingFileEditorTestData.File(SupportingFileEditorTestData.OtherFileId);
        var pending = new PendingEditList();

        pending.SetSupportingFile(SupportingFileStaging.SetGroup(one, SupportingFileStaging.SetTitle(one, Fresh(one), "A"), "New group"));
        pending.SetSupportingFile(SupportingFileStaging.SetTitle(two, Fresh(two), "B"));

        Assert.Equal(2, pending.Changes.Count);
        Assert.Equal(4, pending.ChangeCount);
        Assert.Equal(2, pending.SupportingFileStages.Count);
    }

    [Fact]
    public void Review_ListsEachFieldOfAFile_ThenCreateGroup_ThenTheFileMove()
    {
        var file = SupportingFileEditorTestData.File();
        var pending = new PendingEditList();
        var stage = SupportingFileStaging.SetGroup(file, SupportingFileStaging.SetTitle(file, Fresh(file), "Shooting workbook"), "Warm-up sheets");
        stage = SupportingFileStaging.WithMove(SupportingFileStaging.SetScope(file, stage, "season:1"), "Season 02/Documents/Workbook.pdf", "Season 01/Documents/Workbook.pdf");
        pending.SetSupportingFile(stage);

        var rows = EditReviewRows.Build(pending, _ => "unused", null);

        Assert.Equal(
            ["Title", "Group", "Create group", "Belongs to", "File moves"],
            rows.Select(row => row.What));
        var move = rows.Single(row => row.FieldKey == SupportingFileFields.Move);
        Assert.Equal(EditReviewGroup.PhysicalFiles, move.Group);
        Assert.Equal("Season 02/Documents/Workbook.pdf", move.Before);
        Assert.Equal("Season 01/Documents/Workbook.pdf", move.After);
        Assert.Equal(EditReviewRows.SupportingFileMoveWarning, move.Warning);
        Assert.All(rows, row => Assert.Equal(SupportingFileEditorTestData.FileId, row.JumpId));
        Assert.All(rows, row => Assert.Equal(EditTool.Details, row.Tool));
    }

    [Fact]
    public void RemovingAFieldRow_TakesOnlyThatFieldOut_AndBelongsToTakesTheMoveWithIt()
    {
        var file = SupportingFileEditorTestData.File();
        var pending = new PendingEditList();
        var stage = SupportingFileStaging.SetTitle(file, Fresh(file), "Shooting workbook");
        stage = SupportingFileStaging.WithMove(SupportingFileStaging.SetScope(file, stage, "season:1"), "a", "b");
        pending.SetSupportingFile(stage);

        Assert.True(pending.ClearSupportingFileField(SupportingFileEditorTestData.FileId, SupportingFileFields.Move));

        var left = Assert.IsType<SupportingFileStage>(pending.FindSupportingFile(SupportingFileEditorTestData.FileId));
        Assert.Equal([SupportingFileFields.Title], left.Rows.Select(row => row.Key));
        Assert.False(left.HasMove);

        Assert.True(pending.ClearSupportingFileField(SupportingFileEditorTestData.FileId, SupportingFileFields.Title));
        Assert.True(pending.IsEmpty);
    }

    [Fact]
    public void ADeletedFile_TakesItsStagedChangesWithIt_AndOthersStay()
    {
        var one = SupportingFileEditorTestData.File();
        var two = SupportingFileEditorTestData.File(SupportingFileEditorTestData.OtherFileId);
        var pending = new PendingEditList();
        pending.SetSupportingFile(SupportingFileStaging.SetTitle(one, Fresh(one), "A"));
        pending.SetSupportingFile(SupportingFileStaging.SetTitle(two, Fresh(two), "B"));

        pending.RemoveForSupportingFiles([SupportingFileEditorTestData.FileId]);

        Assert.Null(pending.FindSupportingFile(SupportingFileEditorTestData.FileId));
        Assert.NotNull(pending.FindSupportingFile(SupportingFileEditorTestData.OtherFileId));
    }

    [Fact]
    public void AStagedFileMove_ReplacesTheWriteBackSentenceInTheFooter()
    {
        var file = SupportingFileEditorTestData.File();
        var pending = new PendingEditList();
        pending.SetSupportingFile(SupportingFileStaging.WithMove(SupportingFileStaging.SetScope(file, Fresh(file), "season:1"), "Season 02/Workbook.pdf", "Season 01/Workbook.pdf"));

        var state = EditorFooterRules.For(FileWriteSetting.Unknown, pending);

        Assert.Equal(EditorFooterStatus.WillMove, state.Status);
        Assert.Equal("Season 02/Workbook.pdf", state.MoveFrom);
        Assert.Equal("Season 01/Workbook.pdf", state.MoveTo);
    }

    // -- What the panel shows -------------------------------------------------------------------------------------------------------

    [Fact]
    public void QuickFacts_OfAPdf_SayTypePagesSizeNameAndFolder_WithAPreviewButton()
    {
        var model = EditorQuickFactsBuilder.ForSupportingFile(SupportingFileEditorTestData.File());

        Assert.Equal(QuickFactsKind.SupportingFile, model.Kind);
        var type = model.Facts.Single(fact => fact.Key == "file_type");
        Assert.Equal("PDF document", type.Value);
        Assert.Equal("18 pages · 2.4 MB", type.Label);
        var name = model.Facts.Single(fact => fact.Key == "file_name");
        Assert.Equal("Workbook.pdf", name.Value);
        Assert.Equal("Season 02 / Documents", name.Label);
        Assert.Empty(model.Notes);
        Assert.True(model.OffersPreview);
        Assert.Equal("Preview PDF", model.PreviewLabel);
    }

    [Fact]
    public void QuickFacts_OfAFileInAReadOnlyFolder_SayTheFolderIsOnlyRead()
    {
        var model = EditorQuickFactsBuilder.ForSupportingFile(SupportingFileEditorTestData.File(readOnly: true));

        Assert.Equal([EditorQuickFactsBuilder.SupportingFileReadOnlyNote], model.Notes);
    }

    [Fact]
    public void QuickFacts_OfAFileGoneFromDisk_SayItIsNotFound_AndOfferNoPreview()
    {
        var model = EditorQuickFactsBuilder.ForSupportingFile(SupportingFileEditorTestData.File(missing: true));

        Assert.Contains(model.Notes, note => note.StartsWith(EditorQuickFactsBuilder.FileNotFoundNote, StringComparison.Ordinal));
        Assert.False(model.OffersPreview);
    }

    [Fact]
    public void TheHeadersFactsLine_ReadsTypePagesAndSize_AndTheChipIsNotText()
    {
        var file = SupportingFileEditorTestData.File();

        var parts = EditorHeaderFacts.Parts(file.Config, _ => null, null, file.Header, file);

        Assert.Equal(["PDF", "18 pages", "2.4 MB"], parts);
        Assert.True(EditorHeaderFacts.ShowsSupportingFileChip(file.Config));
    }

    [Fact]
    public async Task ASupportingFile_HasDetailsPreviewAndHistoryTabs_ButNeverArtworkOrMatch()
    {
        var file = SupportingFileEditorTestData.File();
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetSupportingFileEditorAsync), _ => Task.FromResult<EditorSupportingFileDto?>(file)));
        var fields = new ItemFieldEditSession(api);
        await fields.LoadSupportingFileAsync(SupportingFileEditorTestData.TitleId, SupportingFileEditorTestData.FileId);

        var tabs = EditorTabRules.For(SupportingFileEditorTestData.Config(), fields, showHistory: true);

        Assert.Equal([EditTool.Details, EditTool.Preview, EditTool.History], tabs.Select(tab => tab.Tool));
        Assert.False(fields.CanMatch);
    }

    [Fact]
    public void TheDeleteQuestion_NamesTheFile_AndSaysWhatHappensToItOnDisk()
    {
        var managed = SupportingFileEditorTestData.File();
        var readOnly = SupportingFileEditorTestData.File(readOnly: true);

        Assert.Equal("Delete Workbook.pdf?", EditorSupportingFileFlow.Question(managed));
        Assert.Contains("removed from its folder", EditorSupportingFileFlow.Body(managed), StringComparison.Ordinal);
        Assert.Contains("stays where it is", EditorSupportingFileFlow.Body(readOnly), StringComparison.Ordinal);
    }
}
