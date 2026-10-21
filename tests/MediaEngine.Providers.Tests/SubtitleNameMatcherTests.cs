using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

public sealed class SubtitleNameMatcherTests
{
    private static readonly Guid Frodo = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Bilbo = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Mordor = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Shire = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid Gandalf = Guid.Parse("00000000-0000-0000-0000-000000000005");

    private static SubtitleEntityName Entity(Guid id, string label, string type = "Character", params string[] aliases) =>
        new(id, "Q" + id.ToString("N")[^4..], label, type, aliases);

    private static SubtitleLine Line(int index, long startMs, string text) => new(index, startMs, startMs + 2_000, text);

    [Fact]
    public void GivenNameAndPlaceNamedInLines_TagsEachAtItsTimeWindow()
    {
        var matcher = new SubtitleNameMatcher([Entity(Frodo, "Frodo Baggins"), Entity(Mordor, "Mordor", "Location")]);
        SubtitleLine[] lines =
        [
            Line(0, 1_000, "Good morning."),
            Line(1, 60_000, "Frodo, we must leave."),
            Line(2, 63_000, "Where do we go?"),
            Line(3, 70_000, "Into MORDOR, Frodo."),
            Line(4, 300_000, "One does not simply walk into Mordor."),
        ];

        var tags = matcher.Tag(lines);

        var frodo = Assert.Single(tags, tag => tag.EntityId == Frodo);
        Assert.Equal(60_000, frodo.WindowStartMs);
        Assert.Equal(72_000, frodo.WindowEndMs);
        Assert.Equal(1, frodo.FirstLineIndex);
        Assert.Equal(3, frodo.LastLineIndex);
        Assert.Equal(2, frodo.MentionCount);
        Assert.Equal("Frodo", frodo.MatchedText);

        var mordor = tags.Where(tag => tag.EntityId == Mordor).OrderBy(tag => tag.WindowStartMs).ToList();
        Assert.Equal(2, mordor.Count);
        Assert.Equal((70_000, 72_000), (mordor[0].WindowStartMs, mordor[0].WindowEndMs));
        Assert.Equal((300_000, 302_000), (mordor[1].WindowStartMs, mordor[1].WindowEndMs));
        Assert.Equal(tags.OrderBy(tag => tag.WindowStartMs).Select(tag => tag.WindowStartMs), tags.Select(tag => tag.WindowStartMs));
    }

    [Fact]
    public void MentionsFurtherApartThanTheGap_StartNewWindows()
    {
        var matcher = new SubtitleNameMatcher([Entity(Gandalf, "Gandalf")], windowGapMs: 10_000);

        var tags = matcher.Tag([Line(0, 0, "Gandalf!"), Line(1, 5_000, "Gandalf?"), Line(2, 40_000, "Gandalf.")]);

        Assert.Equal(2, tags.Count);
        Assert.Equal(2, tags[0].MentionCount);
        Assert.Equal(1, tags[1].MentionCount);
    }

    [Fact]
    public void AliasesAndSurnamesAndThePrefixMatch_ButOnlyWholeWords()
    {
        var matcher = new SubtitleNameMatcher(
        [
            Entity(Gandalf, "Gandalf the Grey", aliases: "Mithrandir"),
            Entity(Shire, "The Shire", "Location"),
            Entity(Frodo, "Frodo Baggins"),
        ]);

        var tags = matcher.Tag(
        [
            Line(0, 0, "Mithrandir has come."),
            Line(1, 4_000, "Back in Shire, it was quiet."),
            Line(2, 8_000, "Mr. Baggins's garden"),
            Line(3, 12_000, "Frodomatic is not a name."),
        ]);

        Assert.Contains(tags, tag => tag.EntityId == Gandalf && tag.MatchedText == "Mithrandir");
        Assert.Contains(tags, tag => tag.EntityId == Shire);
        Assert.Contains(tags, tag => tag.EntityId == Frodo && tag.FirstLineIndex == 2);
        Assert.DoesNotContain(tags, tag => tag.EntityId == Frodo && tag.FirstLineIndex == 3);
    }

    [Fact]
    public void ANameTwoEntitiesShare_IsNotMatched()
    {
        var matcher = new SubtitleNameMatcher([Entity(Frodo, "Frodo Baggins"), Entity(Bilbo, "Bilbo Baggins")]);

        var tags = matcher.Tag([Line(0, 0, "The Baggins family."), Line(1, 3_000, "Bilbo left. Frodo stayed.")]);

        Assert.DoesNotContain(tags, tag => tag.FirstLineIndex == 0);
        Assert.Contains(tags, tag => tag.EntityId == Bilbo);
        Assert.Contains(tags, tag => tag.EntityId == Frodo);
    }

    [Fact]
    public void CurlyApostrophesAndQualifiedLabels_Match()
    {
        var matcher = new SubtitleNameMatcher([Entity(Shire, "Shire (fictional place)", "Location"), Entity(Frodo, "Frodo Baggins")]);

        var tags = matcher.Tag([Line(0, 0, "The Shire’s fields; Frodo’s home.")]);

        Assert.Contains(tags, tag => tag.EntityId == Shire);
        Assert.Contains(tags, tag => tag.EntityId == Frodo);
    }

    [Fact]
    public void NothingToMatch_GivesNoTags()
    {
        Assert.Empty(new SubtitleNameMatcher([]).Tag([Line(0, 0, "Frodo")]));
        Assert.Empty(new SubtitleNameMatcher([Entity(Frodo, "Frodo Baggins")]).Tag([]));
    }

    [Fact]
    public void Signature_ChangesWhenTheEntitiesChange_AndNotWithTheirOrder()
    {
        var a = Entity(Frodo, "Frodo Baggins");
        var b = Entity(Mordor, "Mordor", "Location");

        Assert.Equal(SubtitleNameMatcher.Signature([a, b]), SubtitleNameMatcher.Signature([b, a]));
        Assert.NotEqual(SubtitleNameMatcher.Signature([a, b]), SubtitleNameMatcher.Signature([a]));
        Assert.NotEqual(SubtitleNameMatcher.Signature([a]), SubtitleNameMatcher.Signature([a with { Label = "Frodo of the Shire" }]));
    }
}
