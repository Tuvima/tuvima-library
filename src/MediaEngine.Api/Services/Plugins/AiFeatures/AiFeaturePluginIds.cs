using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Plugins.AiFeatures;

/// <summary>The plugin ids of the Local AI features. Each feature belongs to an Official built-in plugin and is on exactly when that plugin is on; two plugins carry two features each.</summary>
internal static class AiFeaturePluginIds
{
    public const string SmartFiling = "tuvima.ai-smart-filing";
    public const string SeriesOrder = "tuvima.ai-series-order";
    public const string DescriptionInsights = "tuvima.ai-description-insights";
    public const string TasteSummary = "tuvima.ai-taste-summary";

    public static readonly IReadOnlyList<string> All =
    [
        SmartFiling,
        SeriesOrder,
        DescriptionInsights,
        TasteSummary,
    ];

    /// <summary>The keys a plugin's feature records its results under (empty for a plugin that records none).</summary>
    public static IReadOnlyList<string> ResultKeysFor(string pluginId) => pluginId switch
    {
        SeriesOrder => [AiFeatureKeys.SeriesAlignment],
        DescriptionInsights => [AiFeatureKeys.DescriptionIntelligence, AiFeatureKeys.Vibe],
        _ => [],
    };

    /// <summary>The plugin behind a feature.</summary>
    public static string For(AiFeature feature) => feature switch
    {
        AiFeature.SmartLabeling or AiFeature.TypeLogic => SmartFiling,
        AiFeature.SeriesAlignment => SeriesOrder,
        AiFeature.VibeTags or AiFeature.DescriptionIntelligence => DescriptionInsights,
        AiFeature.TasteSummary => TasteSummary,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown Local AI feature."),
    };
}
