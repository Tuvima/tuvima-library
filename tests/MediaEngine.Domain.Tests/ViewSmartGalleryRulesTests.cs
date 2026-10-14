using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Domain.Tests;

public sealed class ViewSmartGalleryRulesTests
{
    [Fact]
    public void ParsesVersionOneSnakeCaseGroupsAndNormalizesChoices()
    {
        var definition = ViewSmartGalleryRules.Parse("""
            {
              "version": 1,
              "groups": [
                {
                  "id": "photos",
                  "join_with_previous": "OR",
                  "match_mode": "ALL",
                  "conditions": [
                    { "field": "MEDIA_TYPE", "op": "EQ", "value": "image" },
                    { "field": "captured_date", "op": "between", "values": ["2026-01-01", "2026-12-31"] }
                  ]
                }
              ]
            }
            """);

        var group = Assert.Single(definition.Groups);
        Assert.Equal("all", group.MatchMode);
        Assert.Equal("or", group.JoinWithPrevious);
        Assert.Equal("media_type", group.Conditions[0].Field);
        Assert.Equal("eq", group.Conditions[0].Op);
    }

    [Theory]
    [InlineData("{\"version\":2,\"groups\":[{\"match_mode\":\"all\",\"join_with_previous\":\"or\",\"conditions\":[{\"field\":\"favorite\",\"op\":\"eq\",\"value\":\"true\"}]}]}")]
    [InlineData("{\"version\":1,\"groups\":[{\"match_mode\":\"all\",\"join_with_previous\":\"or\",\"conditions\":[{\"field\":\"title); DROP TABLE local_items;--\",\"op\":\"eq\",\"value\":\"x\"}]}]}")]
    [InlineData("{\"version\":1,\"groups\":[{\"match_mode\":\"all\",\"join_with_previous\":\"or\",\"conditions\":[{\"field\":\"media_type\",\"op\":\"like\",\"value\":\"%\"}]}]}")]
    [InlineData("{\"version\":1,\"groups\":[{\"match_mode\":\"all\",\"join_with_previous\":\"or\",\"conditions\":[{\"field\":\"captured_date\",\"op\":\"eq\",\"value\":\"08/23/2026\"}]}]}")]
    [InlineData("{not-json")]
    public void RejectsUnsupportedOrMalformedRules(string json)
    {
        Assert.Throws<ArgumentException>(() => ViewSmartGalleryRules.Parse(json));
    }

    [Fact]
    public void AcceptsEverySupportedViewFieldWithApplicableOperators()
    {
        var conditions = new[]
        {
            Rule("media_type", "in", values: ["image", "video"]),
            Rule("file_type", "contains", "jpeg"),
            Rule("orientation", "neq", "square"),
            Rule("duration", "gte", "12.5"),
            Rule("captured_date", "lt", "2026-08-24"),
            Rule("people", "known"),
            Rule("place", "unknown"),
            Rule("device", "eq", "phone"),
            Rule("tags", "in", values: ["family", "travel"]),
            Rule("favorite", "eq", "true"),
            Rule("owner", "contains", "shy"),
            Rule("source", "neq", "imports"),
        };
        var definition = CollectionRuleDefinition.SingleGroup(conditions);

        ViewSmartGalleryRules.Validate(definition);

        Assert.Equal(conditions.Length, definition.AllConditions.Count);
    }

    [Fact]
    public void AcceptsTheDocumentFieldsASavedViewUses()
    {
        var field = Guid.NewGuid();
        var conditions = new[]
        {
            Rule("media_type", "eq", "document"),
            Rule("tags", "all", values: ["a", "b"]),
            Rule("correspondent", "eq", Guid.NewGuid().ToString("D")),
            Rule("document_type", "in", values: ["Invoice", "Letter"]),
            Rule("document_date", "between", values: ["2026-01-01", "2026-03-31"]),
            Rule("document_date", "within", "last_30_days"),
            Rule("document_date", "unknown"),
            Rule("filing_state", "eq", "inbox"),
            Rule("text_contains", "contains", "electricity"),
            Rule(ViewSmartGalleryRules.CustomFieldKey(field), "between", values: ["10", "50"]),
            Rule(ViewSmartGalleryRules.CustomFieldKey(field), "contains", "PO-"),
        };
        var definition = CollectionRuleDefinition.SingleGroup(conditions);

        ViewSmartGalleryRules.Validate(definition);

        Assert.Equal(conditions.Length, definition.AllConditions.Count);
        Assert.True(ViewSmartGalleryRules.IsDocumentView(ViewSmartGalleryRules.Serialize(definition)));
    }

    [Theory]
    [InlineData("tags", "all", null)]
    [InlineData("correspondent", "gt", "x")]
    [InlineData("document_date", "within", "last_century")]
    [InlineData("document_date", "eq", "14/10/2026")]
    [InlineData("filing_state", "eq", "pending")]
    [InlineData("filing_state", "known", null)]
    [InlineData("text_contains", "eq", "x")]
    [InlineData("field:not-a-guid", "eq", "x")]
    [InlineData("field:00000000-0000-0000-0000-000000000000", "eq", "x")]
    public void RejectsDocumentConditionsThatAreIncompleteOrUnsafe(string field, string op, string? value)
    {
        var definition = CollectionRuleDefinition.SingleGroup([Rule(field, op, value)]);

        Assert.Throws<ArgumentException>(() => ViewSmartGalleryRules.Validate(definition));
    }

    [Fact]
    public void RejectsDocumentDatesOutOfOrderAndTooManyTags()
    {
        Assert.Throws<ArgumentException>(() => ViewSmartGalleryRules.Validate(CollectionRuleDefinition.SingleGroup(
            [Rule("document_date", "between", values: ["2026-12-31", "2026-01-01"])])));
        var tooMany = Enumerable.Range(0, ViewSmartGalleryRules.MaximumValuesPerCondition + 1).Select(index => $"tag{index}").ToArray();
        Assert.Throws<ArgumentException>(() => ViewSmartGalleryRules.Validate(CollectionRuleDefinition.SingleGroup(
            [Rule("tags", "all", values: tooMany)])));
    }

    [Theory]
    [InlineData("{\"version\":1,\"groups\":[{\"match_mode\":\"all\",\"join_with_previous\":\"and\",\"conditions\":[{\"field\":\"media_type\",\"op\":\"eq\",\"value\":\"document\"},{\"field\":\"tags\",\"op\":\"all\",\"values\":[\"a\"]}]}]}", true)]
    [InlineData("{\"version\":1,\"groups\":[{\"match_mode\":\"any\",\"join_with_previous\":\"and\",\"conditions\":[{\"field\":\"media_type\",\"op\":\"eq\",\"value\":\"document\"},{\"field\":\"favorite\",\"op\":\"eq\",\"value\":\"true\"}]}]}", false)]
    [InlineData("{\"version\":1,\"groups\":[{\"match_mode\":\"all\",\"join_with_previous\":\"and\",\"conditions\":[{\"field\":\"media_type\",\"op\":\"eq\",\"value\":\"image\"}]}]}", false)]
    [InlineData("{not-json", false)]
    [InlineData(null, false)]
    public void RecognisesASavedDocumentsViewOnlyWhenItCanMatchNothingElse(string? json, bool expected)
    {
        Assert.Equal(expected, ViewSmartGalleryRules.IsDocumentView(json));
    }

    private static CollectionRulePredicate Rule(string field, string op, string? value = null, string[]? values = null) =>
        new() { Field = field, Op = op, Value = value, Values = values };
}
