using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

public sealed class ViewSmartRuleSummaryTests
{
    private static string Rule(string matchMode, params string[] conditions) =>
        "{\"version\":1,\"groups\":[{\"id\":\"g\",\"match_mode\":\"" + matchMode + "\",\"conditions\":[" + string.Join(",", conditions) + "]}]}";

    [Fact]
    public void AllMatchRules_AreReadAsPlainEnglishWithAnd()
    {
        var json = Rule("all", "{\"field\":\"media_type\",\"op\":\"eq\",\"value\":\"image\"}", "{\"field\":\"tags\",\"op\":\"contains\",\"value\":\"Lisbon\"}");

        Assert.Equal("Media type is image and Tags contains Lisbon.", ViewSmartRuleSummary.Describe(json));
    }

    [Fact]
    public void AnyMatchRules_AreJoinedWithOr()
    {
        var json = Rule("any", "{\"field\":\"place\",\"op\":\"eq\",\"value\":\"Beach\"}", "{\"field\":\"place\",\"op\":\"eq\",\"value\":\"Pool\"}");

        Assert.Equal("Place is Beach or Place is Pool.", ViewSmartRuleSummary.Describe(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoRuleText_SaysThereAreNoRulesYet(string? json) =>
        Assert.Equal(ViewSmartRuleSummary.NoRules, ViewSmartRuleSummary.Describe(json));

    [Fact]
    public void UnreadableRuleText_IsNeverShownToThePerson()
    {
        var said = ViewSmartRuleSummary.Describe("{ not json");

        Assert.Equal(ViewSmartRuleSummary.Unreadable, said);
        Assert.DoesNotContain("not json", said, StringComparison.Ordinal);
    }
}
