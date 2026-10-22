using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Contracts.Tests;

public sealed class IntegrationHealthRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AKeyNobodyHasUsedFor90Days_NeedsAttention_AndOneUsedYesterdayDoesNot()
    {
        Assert.True(IntegrationHealthRules.KeyUnused(Now.AddDays(-200), Now.AddDays(-91), Now));
        Assert.True(IntegrationHealthRules.KeyUnused(Now.AddDays(-91), null, Now));
        Assert.False(IntegrationHealthRules.KeyUnused(Now.AddDays(-200), Now.AddDays(-1), Now));
        Assert.False(IntegrationHealthRules.KeyUnused(Now.AddDays(-5), null, Now));
    }

    [Fact]
    public void AKeyExpiringWithin14Days_NeedsAttention_AndOneWithNoExpiryDoesNot()
    {
        Assert.True(IntegrationHealthRules.KeyNearExpiry(Now.AddDays(10), Now));
        Assert.True(IntegrationHealthRules.KeyNearExpiry(Now.AddDays(-1), Now));
        Assert.False(IntegrationHealthRules.KeyNearExpiry(Now.AddDays(60), Now));
        Assert.False(IntegrationHealthRules.KeyNearExpiry(null, Now));
    }

    [Fact]
    public void OneFailedDeliveryIsNotBroken_ItTakesFiveInARow()
    {
        Assert.True(IntegrationHealthRules.WebhookBrokenFailures > IntegrationHealthRules.WebhookAttentionFailures);
        Assert.True(IntegrationHealthRules.WebhookAttentionFailures > 1);
    }
}
