using MediaEngine.AI.EngineRoom;
using MediaEngine.Domain.Enums;

namespace MediaEngine.AI.Tests;

public sealed class AiAbilityCatalogTests
{
    [Theory]
    [InlineData(AiAbility.WriteTextQuick, AiModelRole.TextFast, false)]
    [InlineData(AiAbility.WriteTextCareful, AiModelRole.TextQuality, false)]
    [InlineData(AiAbility.WriteTextLong, AiModelRole.TextScholar, false)]
    [InlineData(AiAbility.ExtractStructured, AiModelRole.TextQuality, true)]
    [InlineData(AiAbility.Classify, AiModelRole.TextQuality, true)]
    public void TextAbilities_AreServedByTheTextModelRoleTheyNeed(AiAbility ability, AiModelRole role, bool requiresGrammar)
    {
        var info = AiAbilityCatalog.Get(ability);

        Assert.Equal(AiEngine.TextModel, info.Engine);
        Assert.Equal(role, info.Role);
        Assert.Equal(requiresGrammar, info.RequiresGrammar);
        Assert.True(info.IsAvailableNow);
    }

    [Fact]
    public void ReadTextFromImage_IsServedByTesseractWithNoModelRole()
    {
        var info = AiAbilityCatalog.Get(AiAbility.ReadTextFromImage);

        Assert.Equal(AiEngine.Tesseract, info.Engine);
        Assert.Null(info.Role);
        Assert.True(info.IsAvailableNow);
    }

    [Theory]
    [InlineData(AiAbility.TranscribeSpeech)]
    [InlineData(AiAbility.FindSimilar)]
    [InlineData(AiAbility.DescribeImage)]
    public void AbilitiesRecordedForLater_AreNotAvailableYet(AiAbility ability)
    {
        var info = AiAbilityCatalog.Get(ability);

        Assert.False(info.IsAvailableNow);
        Assert.Equal(AiEngine.None, info.Engine);
        Assert.Equal("Not available yet", AiAbilityCatalog.NotAvailableYet);
    }

    [Fact]
    public void EveryAbility_HasAnEntryAndAPlainName()
    {
        Assert.Equal(Enum.GetValues<AiAbility>().Length, AiAbilityCatalog.All.Count);
        Assert.Equal(
            [
                "Write text (quick)", "Write text (careful)", "Write text (long)", "Pull out structured facts", "Classify",
                "Read text from images", "Transcribe speech", "Find similar", "Describe image",
            ],
            Enum.GetValues<AiAbility>().OrderBy(ability => (int)ability).Select(ability => ability.ToDisplayName()));
    }

    [Theory]
    [InlineData("text_fast", AiAbility.WriteTextQuick)]
    [InlineData("text_quality", AiAbility.WriteTextCareful)]
    [InlineData("text_scholar", AiAbility.WriteTextLong)]
    [InlineData("text_cjk", AiAbility.WriteTextCareful)]
    [InlineData(" Text_Fast ", AiAbility.WriteTextQuick)]
    public void PluginRoleWords_MapToAbilities(string role, AiAbility expected)
    {
        Assert.True(AiAbilityCatalog.TryFromPluginRole(role, out var ability));
        Assert.Equal(expected, ability);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("vision")]
    [InlineData("nonsense")]
    public void UnknownOrFuturePluginRoleWords_MapToNothing(string role)
    {
        Assert.False(AiAbilityCatalog.TryFromPluginRole(role, out _));
    }

    [Fact]
    public void AbilityKeys_RoundTrip()
    {
        foreach (var ability in Enum.GetValues<AiAbility>())
        {
            Assert.True(AiVocabulary.TryParseAbility(ability.ToKey(), out var parsed));
            Assert.Equal(ability, parsed);
        }
    }
}
