using MediaEngine.Domain.Enums;

namespace MediaEngine.AI.EngineRoom;

/// <summary>What serves one ability.</summary>
/// <param name="Role">The text model role, for abilities the text model serves; null otherwise.</param>
/// <param name="RequiresGrammar">True when the request must carry a grammar (structured output).</param>
/// <param name="IsAvailableNow">False for abilities recorded for later.</param>
public sealed record AiAbilityInfo(
    AiAbility Ability,
    AiEngine Engine,
    AiModelRole? Role,
    bool RequiresGrammar,
    bool IsAvailableNow)
{
    public string DisplayName => Ability.ToDisplayName();
}

/// <summary>
/// The one table that says which engine serves which ability, so callers never name a model. TextCjk and Audio get no
/// ability yet: the text roles share one model file and the speech model has no caller.
/// </summary>
public static class AiAbilityCatalog
{
    /// <summary>The reason given for abilities recorded for later.</summary>
    public const string NotAvailableYet = "Not available yet";

    private static readonly AiAbilityInfo[] Entries =
    [
        new(AiAbility.WriteTextQuick, AiEngine.TextModel, AiModelRole.TextFast, RequiresGrammar: false, IsAvailableNow: true),
        new(AiAbility.WriteTextCareful, AiEngine.TextModel, AiModelRole.TextQuality, RequiresGrammar: false, IsAvailableNow: true),
        new(AiAbility.WriteTextLong, AiEngine.TextModel, AiModelRole.TextScholar, RequiresGrammar: false, IsAvailableNow: true),
        new(AiAbility.ExtractStructured, AiEngine.TextModel, AiModelRole.TextQuality, RequiresGrammar: true, IsAvailableNow: true),
        new(AiAbility.Classify, AiEngine.TextModel, AiModelRole.TextQuality, RequiresGrammar: true, IsAvailableNow: true),
        new(AiAbility.ReadTextFromImage, AiEngine.Tesseract, Role: null, RequiresGrammar: false, IsAvailableNow: true),
        new(AiAbility.TranscribeSpeech, AiEngine.None, Role: null, RequiresGrammar: false, IsAvailableNow: false),
        new(AiAbility.FindSimilar, AiEngine.None, Role: null, RequiresGrammar: false, IsAvailableNow: false),
        new(AiAbility.DescribeImage, AiEngine.None, Role: null, RequiresGrammar: false, IsAvailableNow: false),
    ];

    public static IReadOnlyList<AiAbilityInfo> All => Entries;

    public static AiAbilityInfo Get(AiAbility ability) =>
        Entries.FirstOrDefault(entry => entry.Ability == ability)
        ?? throw new ArgumentOutOfRangeException(nameof(ability), ability, "Unknown AI ability.");

    /// <summary>The ability a plugin asks for when it names one of today's manifest role words.</summary>
    public static bool TryFromPluginRole(string role, out AiAbility ability)
    {
        switch (role.Trim().ToLowerInvariant())
        {
            case "text_fast":
                ability = AiAbility.WriteTextQuick;
                return true;
            case "text_quality":
            case "text_cjk":
                ability = AiAbility.WriteTextCareful;
                return true;
            case "text_scholar":
                ability = AiAbility.WriteTextLong;
                return true;
            default:
                ability = default;
                return false;
        }
    }
}
