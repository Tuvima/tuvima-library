using MediaEngine.Api.Services.Plugins;
using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>The Engine's single list of "what it provides" sentences (Plugins and Integrations screens v5 D53).</summary>
public sealed class PluginProvidesSentencesTests
{
    [Fact]
    public void EveryKindOnTheFixedList_HasOneNonEmptySentence_AndNoTwoKindsShareOne()
    {
        var sentences = PluginProvides.All.Select(PluginProvidesSentences.For).ToList();

        Assert.All(sentences, sentence => Assert.False(string.IsNullOrWhiteSpace(sentence)));
        Assert.Equal(sentences.Count, sentences.Distinct().Count());
    }

    [Fact]
    public void Cards_FollowTheStandardOrder_DropUnknownWords_AndCarryTheSentence()
    {
        var cards = PluginProvidesSentences.Cards(["Account sync", "Nonsense", "Identification"]);

        Assert.Equal(["Identification", "Account sync"], cards.Select(card => card.Kind));
        Assert.Equal(PluginProvidesSentences.For("Account sync"), cards[1].Sentence);
    }
}
