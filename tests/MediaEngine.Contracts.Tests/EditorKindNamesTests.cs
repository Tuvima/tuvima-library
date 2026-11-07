using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Contracts.Tests;

/// <summary>The editor's kind ids are wire values: the Contracts names the Dashboard reads and the Engine's own config keys are the same strings.</summary>
public sealed class EditorKindNamesTests
{
    [Fact]
    public void TheContractsKindNames_AreExactlyTheKindsTheEnginesEditorConfigIsKeyedBy()
    {
        Assert.Equal(EditorKinds.All, EditorKindNames.All);
    }

    [Fact]
    public void EveryKindName_IsListedOnce_AndIsAPlainSnakeCaseId()
    {
        Assert.Equal(EditorKindNames.All.Count, EditorKindNames.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(EditorKindNames.All, kind => Assert.Matches("^[a-z]+(_[a-z]+)*$", kind));
    }

    [Fact]
    public void TheWireValuesDidNotChange()
    {
        Assert.Equal("tv_show", EditorKindNames.TvShow);
        Assert.Equal("tv_season", EditorKindNames.TvSeason);
        Assert.Equal("tv_episode", EditorKindNames.TvEpisode);
        Assert.Equal("course_section", EditorKindNames.CourseSection);
        Assert.Equal("supporting_file", EditorKindNames.SupportingFile);
        Assert.Equal("view_gallery", EditorKindNames.ViewGallery);
    }
}
