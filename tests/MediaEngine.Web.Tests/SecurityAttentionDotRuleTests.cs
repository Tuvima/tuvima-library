using MediaEngine.Web.Services.Navigation;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The small red dot on Security &amp; Devices in the Settings menu (settings-alignment D11): an account without a password works on the
/// whole home network, so having none is a normal setup and never raises it. It shows only when something really needs doing, which
/// is no password while outside access is on.
/// </summary>
public sealed class SecurityAttentionDotRuleTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void TheDot_ShowsOnlyForNoPasswordWithOutsideAccessOn(bool noPassword, bool outsideAccessOn, bool expected) =>
        Assert.Equal(expected, SecureAccountPrompt.SecurityNeedsAttention(noPassword, outsideAccessOn));

    [Theory]
    [InlineData("local-only", false)]
    [InlineData(" Local-Only ", false)]
    [InlineData("tailscale", true)]
    [InlineData("custom", true)]
    [InlineData("direct-only", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OutsideAccessIsOn_UnlessTheConnectionModeIsLocalOnly(string? mode, bool expected) =>
        Assert.Equal(expected, SecureAccountPrompt.OutsideAccessIsOn(mode));
}
