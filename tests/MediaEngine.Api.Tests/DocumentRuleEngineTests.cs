using MediaEngine.Api.Services.LocalAssets.Filing;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class DocumentRuleEngineTests
{
    private static readonly Guid SourceA = Guid.NewGuid();
    private static readonly Guid SourceB = Guid.NewGuid();

    private static DocumentRuleSubject Subject(
        string fileName = "scan-0042.pdf",
        string? text = "Invoice from Acme Water Company. Amount due 120.50 EUR.",
        string folder = @"C:\Users\me\Documents\Bills\2026",
        params Guid[] sources) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), false, "Scan", fileName, folder, text,
            sources.Length == 0 ? [SourceA] : sources, ViewDocumentFilingStates.Inbox, DateTimeOffset.UtcNow);

    private static DocumentRule Rule(
        string kind, string value, string mode = ViewDocumentRuleMatchModes.All, bool enabled = true,
        bool files = false, params DocumentRuleAction[] actions) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Rule", enabled, mode, 0, files,
            [new DocumentRuleCondition(kind, value)], actions);

    [Theory]
    [InlineData(ViewDocumentRuleConditionKinds.TextContains, "acme water", true)]
    [InlineData(ViewDocumentRuleConditionKinds.TextContains, "ACME WATER COMPANY", true)]
    [InlineData(ViewDocumentRuleConditionKinds.TextContains, "electric", false)]
    [InlineData(ViewDocumentRuleConditionKinds.FilenameContains, "SCAN-00", true)]
    [InlineData(ViewDocumentRuleConditionKinds.FilenameContains, "receipt", false)]
    [InlineData(ViewDocumentRuleConditionKinds.FolderContains, "documents/bills", true)]
    [InlineData(ViewDocumentRuleConditionKinds.FolderContains, @"Documents\Bills", true)]
    [InlineData(ViewDocumentRuleConditionKinds.FolderContains, "taxes", false)]
    [InlineData(ViewDocumentRuleConditionKinds.FileKind, "pdf", true)]
    [InlineData(ViewDocumentRuleConditionKinds.FileKind, "PDF", true)]
    [InlineData(ViewDocumentRuleConditionKinds.FileKind, "image", false)]
    [InlineData(ViewDocumentRuleConditionKinds.FileKind, "office", false)]
    public void EachConditionKindMatchesTheWayAPersonWouldExpect(string kind, string value, bool expected)
    {
        Assert.Equal(expected, DocumentRuleEngine.Matches(Rule(kind, value), Subject()));
    }

    [Theory]
    [InlineData("holiday.JPG", "image")]
    [InlineData("holiday.heic", "image")]
    [InlineData("report.docx", "office")]
    [InlineData("sheet.xlsx", "office")]
    [InlineData("notes.txt", null)]
    [InlineData("noextension", null)]
    public void FileKindsAreWorkedOutFromTheExtension(string fileName, string? kind)
    {
        Assert.Equal(kind, DocumentRuleEngine.FileKindOf(fileName));
        if (kind is not null)
        {
            Assert.True(DocumentRuleEngine.Matches(Rule(ViewDocumentRuleConditionKinds.FileKind, kind), Subject(fileName)));
        }
    }

    [Fact]
    public void ASourceConditionMatchesOnlyDocumentsFoundThroughThatSource()
    {
        var rule = Rule(ViewDocumentRuleConditionKinds.Source, SourceA.ToString("D"));
        Assert.True(DocumentRuleEngine.Matches(rule, Subject(sources: SourceA)));
        Assert.True(DocumentRuleEngine.Matches(rule, Subject(sources: [SourceB, SourceA])));
        Assert.False(DocumentRuleEngine.Matches(rule, Subject(sources: SourceB)));
        Assert.False(DocumentRuleEngine.Matches(Rule(ViewDocumentRuleConditionKinds.Source, "not-a-guid"), Subject()));
    }

    [Fact]
    public void ADocumentWithNoWordsNeverMatchesAWordsRuleButStillMatchesNameRules()
    {
        var photo = Subject(text: null);
        Assert.False(DocumentRuleEngine.Matches(Rule(ViewDocumentRuleConditionKinds.TextContains, "invoice"), photo));
        Assert.True(DocumentRuleEngine.Matches(Rule(ViewDocumentRuleConditionKinds.FileKind, "pdf"), photo));
    }

    [Fact]
    public void TextRulesAreNotPatterns()
    {
        // A star or a dot is just a character: there is no regex and no wildcard.
        Assert.False(DocumentRuleEngine.Matches(Rule(ViewDocumentRuleConditionKinds.TextContains, "Acme.*Company"), Subject()));
        Assert.False(DocumentRuleEngine.Matches(Rule(ViewDocumentRuleConditionKinds.TextContains, "^Invoice"), Subject()));
        Assert.True(DocumentRuleEngine.Matches(
            Rule(ViewDocumentRuleConditionKinds.TextContains, "120.50"), Subject()));
    }

    [Fact]
    public void LetterCaseIsIgnoredInEveryScript()
    {
        var subject = Subject(text: "Rechnung der Stadtwerke Köln — Straße");
        Assert.True(DocumentRuleEngine.Matches(Rule(ViewDocumentRuleConditionKinds.TextContains, "KÖLN"), subject));
        Assert.True(DocumentRuleEngine.Matches(Rule(ViewDocumentRuleConditionKinds.TextContains, "köln"), subject));
    }

    [Theory]
    [InlineData(ViewDocumentRuleMatchModes.All, false)]
    [InlineData(ViewDocumentRuleMatchModes.Any, true)]
    public void AllNeedsEveryConditionAndAnyNeedsJustOne(string mode, bool expected)
    {
        var rule = new DocumentRule(Guid.NewGuid(), Guid.NewGuid(), null, "Mixed", true, mode, 0, false,
            [
                new DocumentRuleCondition(ViewDocumentRuleConditionKinds.TextContains, "water"),
                new DocumentRuleCondition(ViewDocumentRuleConditionKinds.FilenameContains, "receipt"),
            ], []);
        Assert.Equal(expected, DocumentRuleEngine.Matches(rule, Subject()));
    }

    [Fact]
    public void ARuleWithNoConditionsMatchesNothing()
    {
        var rule = new DocumentRule(Guid.NewGuid(), Guid.NewGuid(), null, "Empty", true, "all", 0, true, [], []);
        Assert.False(DocumentRuleEngine.Matches(rule, Subject()));
        Assert.False(DocumentRuleEngine.Matches(rule with { MatchMode = "any" }, Subject()));
    }

    [Fact]
    public void RulesRunInOrder_TagsAreUnitedAndTheFirstRuleToSetASingleValueWins()
    {
        var tagA = Guid.NewGuid();
        var tagB = Guid.NewGuid();
        var firstCorrespondent = Guid.NewGuid();
        var secondCorrespondent = Guid.NewGuid();
        var billType = Guid.NewGuid();
        var field = Guid.NewGuid();
        var rules = new[]
        {
            Rule(ViewDocumentRuleConditionKinds.TextContains, "water", actions:
            [
                new DocumentRuleAction(ViewDocumentRuleActionKinds.AddTag, tagA, null),
                new DocumentRuleAction(ViewDocumentRuleActionKinds.SetCorrespondent, firstCorrespondent, null),
                new DocumentRuleAction(ViewDocumentRuleActionKinds.SetField, field, "first"),
            ]),
            Rule(ViewDocumentRuleConditionKinds.FileKind, "pdf", actions:
            [
                new DocumentRuleAction(ViewDocumentRuleActionKinds.AddTag, tagB, null),
                new DocumentRuleAction(ViewDocumentRuleActionKinds.AddTag, tagA, null),
                new DocumentRuleAction(ViewDocumentRuleActionKinds.SetCorrespondent, secondCorrespondent, null),
                new DocumentRuleAction(ViewDocumentRuleActionKinds.SetDocumentType, billType, null),
                new DocumentRuleAction(ViewDocumentRuleActionKinds.SetField, field, "second"),
            ]),
            Rule(ViewDocumentRuleConditionKinds.FilenameContains, "never", actions:
                [new DocumentRuleAction(ViewDocumentRuleActionKinds.SetDocumentType, Guid.NewGuid(), null)]),
        };

        var plan = DocumentRuleEngine.Plan(rules, Subject());

        Assert.Equal([tagA, tagB], plan.TagIds);
        Assert.Equal(firstCorrespondent, plan.CorrespondentId);
        Assert.Equal(billType, plan.DocumentTypeId);
        var value = Assert.Single(plan.Fields);
        Assert.Equal("first", value.Text);
        Assert.False(plan.FileNow);
    }

    [Fact]
    public void ADisabledRuleDoesNothingAndAnyMatchingRuleCanFileTheDocument()
    {
        var tag = Guid.NewGuid();
        var disabled = Rule(ViewDocumentRuleConditionKinds.FileKind, "pdf", enabled: false,
            actions: [new DocumentRuleAction(ViewDocumentRuleActionKinds.AddTag, tag, null)]);
        Assert.Equal(DocumentRulePlan.Empty.TagIds, DocumentRuleEngine.Plan([disabled], Subject()).TagIds);

        var filing = Rule(ViewDocumentRuleConditionKinds.FileKind, "pdf", files: true);
        Assert.True(DocumentRuleEngine.Plan([filing], Subject()).FileNow);
        Assert.False(DocumentRuleEngine.Plan([filing], Subject("photo.jpg")).FileNow);
    }

    [Fact]
    public void PlanningIsRepeatable()
    {
        var tag = Guid.NewGuid();
        var rules = new[]
        {
            Rule(ViewDocumentRuleConditionKinds.TextContains, "invoice",
                actions: [new DocumentRuleAction(ViewDocumentRuleActionKinds.AddTag, tag, null)]),
        };
        var subject = Subject();
        var first = DocumentRuleEngine.Plan(rules, subject);
        var second = DocumentRuleEngine.Plan(rules, subject);
        Assert.Equal(first.TagIds, second.TagIds);
        Assert.Equal(first.FileNow, second.FileNow);
    }
}
