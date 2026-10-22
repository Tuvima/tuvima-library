using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

// Backup and restore (spec D3 to D6): a restored entry is put on a file only when exactly one file is the same title; a different cut is
// reported as such; nothing is guessed. The rules reuse the bookmark pack matcher.
public sealed class RestoredTitleRulesTests
{
    private static BookmarkPackTitle Title(string? imdb, double? runningTime = 7200, string? edition = null, string? tmdb = null)
    {
        var ids = new Dictionary<string, string>();
        if (imdb is not null)
        {
            ids["imdb_id"] = imdb;
        }

        if (tmdb is not null)
        {
            ids["tmdb_id"] = tmdb;
        }

        return new BookmarkPackTitle(ids, runningTime, edition);
    }

    [Fact]
    public void ChooseFile_OneFileOfTheSameTitleAndCut_IsExact()
    {
        var decision = RestoredTitleRules.ChooseFile([(Title("tt1"), Title("tt1", 7201)), (Title("tt1"), Title("tt2"))]);

        Assert.Equal(RestoredTitleOutcome.Exact, decision.Outcome);
        Assert.Equal(0, decision.Index);
    }

    [Fact]
    public void ChooseFile_TwoFilesOfTheSameTitleAndCut_AreAmbiguous()
    {
        var decision = RestoredTitleRules.ChooseFile([(Title("tt1"), Title("tt1")), (Title("tt1"), Title("tt1", 7200))]);

        Assert.Equal(RestoredTitleOutcome.Unmatched, decision.Outcome);
    }

    [Fact]
    public void ChooseFile_AFileWhoseOtherIdDisagrees_IsNotThisTitle()
    {
        // Same IMDb id but a different TMDB id: the ids both sides hold must all agree.
        var decision = RestoredTitleRules.ChooseFile([(Title("tt1", tmdb: "10"), Title("tt1", tmdb: "11"))]);

        Assert.Equal(RestoredTitleOutcome.Unmatched, decision.Outcome);
    }

    [Fact]
    public void ChooseFile_ADifferentCutWithNoFileOfTheSameCut_IsADifferentCut()
    {
        var decision = RestoredTitleRules.ChooseFile([(Title("tt1", 9300), Title("tt1", 8000))]);

        Assert.Equal(RestoredTitleOutcome.DifferentCut, decision.Outcome);
        Assert.Equal(0, decision.Index);
    }

    [Fact]
    public void ChooseFile_ASameCutFileWinsOverADifferentCut()
    {
        var decision = RestoredTitleRules.ChooseFile([(Title("tt1", 9300), Title("tt1", 8000)), (Title("tt1", 9300), Title("tt1", 9300))]);

        Assert.Equal(RestoredTitleOutcome.Exact, decision.Outcome);
        Assert.Equal(1, decision.Index);
    }

    [Fact]
    public void ChooseFile_NoIdInCommon_IsNeverAMatch()
    {
        var decision = RestoredTitleRules.ChooseFile([(Title(null, tmdb: "10"), Title("tt1"))]);

        Assert.Equal(RestoredTitleOutcome.Unmatched, decision.Outcome);
    }

    [Fact]
    public void ChooseFile_AnIdOnlyTheStoreHolds_CountsWhenBothSidesHaveIt()
    {
        // A key the pack format does not carry (the Stash-box record) still has to agree when both sides hold it.
        var backup = new BookmarkPackTitle(new Dictionary<string, string> { ["imdb_id"] = "tt1", ["stash_box_id"] = "a|1" }, null, null);
        var same = new BookmarkPackTitle(new Dictionary<string, string> { ["imdb_id"] = "tt1", ["stash_box_id"] = "a|1" }, null, null);
        var other = new BookmarkPackTitle(new Dictionary<string, string> { ["imdb_id"] = "tt1", ["stash_box_id"] = "a|2" }, null, null);

        Assert.Equal(RestoredTitleOutcome.Exact, RestoredTitleRules.ChooseFile([(backup, same)]).Outcome);
        Assert.Equal(RestoredTitleOutcome.Unmatched, RestoredTitleRules.ChooseFile([(backup, other)]).Outcome);
    }

    [Fact]
    public void IsSameTitle_NeedsASharedKind_AndEverySharedIdToMatch()
    {
        var one = new Dictionary<string, string> { ["imdb_id"] = "tt1" };

        Assert.True(RestoredTitleRules.IsSameTitle(one, new Dictionary<string, string> { ["imdb_id"] = "TT1", ["tmdb_id"] = "9" }));
        Assert.False(RestoredTitleRules.IsSameTitle(one, new Dictionary<string, string> { ["imdb_id"] = "tt2" }));
        Assert.False(RestoredTitleRules.IsSameTitle(one, new Dictionary<string, string> { ["tmdb_id"] = "9" }));
    }

    [Fact]
    public void CompareIds_AnEditionIdThatDisagrees_IsAnotherEdition_NotAWrongTitle()
    {
        var hardcover = new Dictionary<string, string> { ["wikidata_qid"] = "Q1", ["isbn_13"] = "111" };
        var ebook = new Dictionary<string, string> { ["wikidata_qid"] = "Q1", ["isbn_13"] = "222" };
        var sameBook = new Dictionary<string, string> { ["wikidata_qid"] = "Q1", ["isbn_13"] = "111" };
        var otherBook = new Dictionary<string, string> { ["wikidata_qid"] = "Q2", ["isbn_13"] = "111" };

        Assert.Equal(RestoredIdVerdict.OtherEdition, RestoredTitleRules.CompareIds(hardcover, ebook));
        Assert.Equal(RestoredIdVerdict.SameEdition, RestoredTitleRules.CompareIds(hardcover, sameBook));
        Assert.Equal(RestoredIdVerdict.WrongTitle, RestoredTitleRules.CompareIds(hardcover, otherBook));
    }

    [Fact]
    public void CompareIds_WithNoTitleIdInCommon_AnEditionIdDecides()
    {
        var one = new Dictionary<string, string> { ["isbn_13"] = "111" };

        Assert.Equal(RestoredIdVerdict.SameEdition, RestoredTitleRules.CompareIds(one, new Dictionary<string, string> { ["isbn_13"] = "111", ["goodreads_id"] = "9" }));
        Assert.Equal(RestoredIdVerdict.WrongTitle, RestoredTitleRules.CompareIds(one, new Dictionary<string, string> { ["isbn_13"] = "222" }));
        Assert.Equal(RestoredIdVerdict.WrongTitle, RestoredTitleRules.CompareIds(one, new Dictionary<string, string> { ["goodreads_id"] = "9" }));
    }

    [Fact]
    public void ChooseFile_AnotherEditionOfTheSameTitle_IsADifferentCut_EvenWithTheSameRunningTime()
    {
        var backup = new BookmarkPackTitle(new Dictionary<string, string> { ["wikidata_qid"] = "Q1", ["isbn_13"] = "111" }, null, "Hardcover");
        var live = new BookmarkPackTitle(new Dictionary<string, string> { ["wikidata_qid"] = "Q1", ["isbn_13"] = "222" }, null, "Ebook");

        Assert.Equal(RestoredTitleOutcome.DifferentCut, RestoredTitleRules.ChooseFile([(backup, live)]).Outcome);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "  ", true)]
    [InlineData("Director's Cut", " director's cut ", true)]
    [InlineData("Director's Cut", "Theatrical", false)]
    [InlineData("Director's Cut", null, false)]
    public void SameEditionLabel_IgnoresCaseAndSpacing_AndTreatsBlankAsBlank(string? backup, string? live, bool expected)
    {
        Assert.Equal(expected, RestoredTitleRules.SameEditionLabel(backup, live));
    }
}
