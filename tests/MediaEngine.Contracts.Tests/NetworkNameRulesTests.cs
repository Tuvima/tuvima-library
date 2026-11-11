using MediaEngine.Contracts.Setup;

namespace MediaEngine.Contracts.Tests;

public sealed class NetworkNameRulesTests
{
    [Theory]
    [InlineData("Shaya's Library", "shayas-library")]
    [InlineData("Shaya’s Library", "shayas-library")]
    [InlineData("  The  Family -- Server!  ", "the-family-server")]
    [InlineData("Café Films", "caf-films")]
    [InlineData("", "")]
    public void ToLabel_LowerCasesDropsApostrophesAndKeepsAsciiWordsHyphenated(string name, string expected) =>
        Assert.Equal(expected, NetworkNameRules.ToLabel(name));

    [Fact]
    public void ToLabel_CapsTheLabelAtSixtyThreeCharactersWithoutATrailingHyphen()
    {
        var label = NetworkNameRules.ToLabel(new string('a', 62) + " bbb");

        Assert.Equal(62, label.Length);
        Assert.False(label.EndsWith('-'));
    }

    [Theory]
    [InlineData("Shaya's Library", "shayas-library", true)]
    [InlineData("Shaya's Library", "tuvima", true)]
    [InlineData("Shaya's Library", "", true)]
    [InlineData("Shaya's Library", "den", false)]
    [InlineData("Shaya's Library", "shayas-library-2", false)]
    public void IsDerived_IsFalseOnlyWhenTheNetworkNameWasSetByHand(string name, string host, bool expected) =>
        Assert.Equal(expected, NetworkNameRules.IsDerived(name, host));
}
