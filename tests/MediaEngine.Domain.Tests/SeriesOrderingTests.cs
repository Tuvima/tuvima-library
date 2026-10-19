using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class SeriesOrderingTests
{
    private static SeriesOrderInput Member(
        string title,
        string? scope = SeriesMembershipScopeNames.MainSequence,
        double? manifest = null,
        double? own = null,
        string? date = null)
        => new(scope, manifest, own, date, title);

    private static string[] Titles(IEnumerable<SeriesOrderInput> members)
        => SeriesOrdering.Order(members, m => m).Select(m => m.Title!).ToArray();

    [Fact]
    public void ManifestOrdinal_BeatsTheWorksOwnPosition_ForMainSequenceMembers()
    {
        var ordered = Titles(
        [
            Member("second by the manifest", manifest: 2, own: 1),
            Member("first by the manifest", manifest: 1, own: 9),
        ]);

        Assert.Equal(["first by the manifest", "second by the manifest"], ordered);
    }

    [Fact]
    public void DecimalPositions_SortBetweenTheirWholeNeighbours_AndAreNeverRounded()
    {
        var ordered = Titles(
        [
            Member("Cibola Burn", own: 4),
            Member("Gods of Risk", own: 2.5),
            Member("Caliban's War", own: 2),
            Member("Leviathan Wakes", own: 1),
            Member("The Churn", own: 0.2),
        ]);

        Assert.Equal(["The Churn", "Leviathan Wakes", "Caliban's War", "Gods of Risk", "Cibola Burn"], ordered);
        Assert.Equal("2.5", SeriesOrdering.FormatNumber(2.5));
        Assert.Equal("2", SeriesOrdering.FormatNumber(2));
    }

    [Fact]
    public void ManifestOrdinal_IsIgnoredForMembersThatAreNotMainSequence()
    {
        var input = Member("novella", SeriesMembershipScopeNames.Supplementary, manifest: 1, own: 5.5);

        Assert.Equal(5.5, SeriesOrdering.SortValue(input));
    }

    [Fact]
    public void AlsoInThisWorldMembers_SortAfterTheNumberedRun_AndTheUnnumberedTail()
    {
        var ordered = Titles(
        [
            Member("collected", SeriesMembershipScopeNames.CollectedContent, own: 0.1),
            Member("unnumbered", own: null, date: "2020"),
            Member("numbered nine", own: 9),
            Member("broader", SeriesMembershipScopeNames.BroaderContext),
            Member("supplementary", SeriesMembershipScopeNames.Supplementary, own: 2.5),
            Member("numbered one", own: 1),
        ]);

        Assert.Equal(
            ["numbered one", "numbered nine", "unnumbered", "collected", "supplementary", "broader"],
            ordered);
    }

    [Fact]
    public void WithoutAPosition_MembersSortByPublicationDateThenTitle()
    {
        var ordered = Titles(
        [
            Member("B", date: "2012-05-01"),
            Member("A", date: "2012-05-01"),
            Member("C", date: "2009"),
            Member("D"),
        ]);

        Assert.Equal(["C", "A", "B", "D"], ordered);
    }

    [Fact]
    public void Sections_SplitTheSeriesRunFromAlsoInThisWorld()
    {
        Assert.Equal(SeriesSection.NumberedRun, SeriesOrdering.SectionOf(Member("a", own: 1)));
        Assert.Equal(SeriesSection.UnnumberedTail, SeriesOrdering.SectionOf(Member("b")));
        Assert.Equal(SeriesSection.UnnumberedTail, SeriesOrdering.SectionOf(Member("c", SeriesMembershipScopeNames.Unpositioned)));
        Assert.Equal(SeriesSection.AlsoInThisWorld, SeriesOrdering.SectionOf(Member("d", SeriesMembershipScopeNames.Supplementary, own: 1)));
        Assert.Equal(SeriesSection.AlsoInThisWorld, SeriesOrdering.SectionOf(Member("e", SeriesMembershipScopeNames.CollectedContent)));
        Assert.Equal(SeriesSection.AlsoInThisWorld, SeriesOrdering.SectionOf(Member("f", SeriesMembershipScopeNames.BroaderContext)));
    }

    [Fact]
    public void DisplayNumber_IsTheSourcesOwnText_ThenTheRetailPosition_ThenTheUnroundedDecimal()
    {
        Assert.Equal("1.5", SeriesOrdering.DisplayNumber("1.5", "9", 3));
        Assert.Equal("2.5", SeriesOrdering.DisplayNumber(null, "2.5", 3));
        Assert.Equal("5.5", SeriesOrdering.DisplayNumber(" ", null, 5.5));
        Assert.Null(SeriesOrdering.DisplayNumber(null, null, null));
    }

    [Fact]
    public void ABorrowedManifestOrdinal_IsDroppedWhenItsScopeIsAnotherContainer()
    {
        var borrowed = SeriesOrdering.BuildInput(SeriesMembershipScopeNames.MainSequence, 4, "QAnthology", "QSeries", 7, null, "x");
        var own = SeriesOrdering.BuildInput(SeriesMembershipScopeNames.MainSequence, 4, "QSeries", "QSeries", 7, null, "x");

        Assert.Equal(7, SeriesOrdering.SortValue(borrowed));
        Assert.Equal(4, SeriesOrdering.SortValue(own));
    }

    [Fact]
    public void Ordering_IsStable_ForEqualMembers()
    {
        var ordered = SeriesOrdering.Order(
            new[] { ("first", 1d), ("second", 1d), ("third", 1d) },
            m => new SeriesOrderInput(null, null, m.Item2, null, null));

        Assert.Equal(["first", "second", "third"], ordered.Select(m => m.Item1).ToArray());
    }
}
