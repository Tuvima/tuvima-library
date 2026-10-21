using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Plugins.AiFeatures;

/// <summary>The plugin ids of the Local AI features. Each feature is an Official built-in plugin, on exactly when its plugin is on.</summary>
internal static class AiFeaturePluginIds
{
    public const string SmartLabelling = "tuvima.ai-smart-labelling";
    public const string TypeHelper = "tuvima.ai-type-helper";
    public const string SeriesOrder = "tuvima.ai-series-order";
    public const string VibeTags = "tuvima.ai-vibe-tags";
    public const string DescriptionInsights = "tuvima.ai-description-insights";
    public const string TasteSummary = "tuvima.ai-taste-summary";

    public static readonly IReadOnlyList<string> All =
    [
        SmartLabelling,
        TypeHelper,
        SeriesOrder,
        VibeTags,
        DescriptionInsights,
        TasteSummary,
    ];

    /// <summary>The keys a plugin's feature records its results under (empty for a plugin that records none).</summary>
    public static IReadOnlyList<string> ResultKeysFor(string pluginId) => pluginId switch
    {
        VibeTags => [AiFeatureKeys.Vibe],
        SeriesOrder => [AiFeatureKeys.SeriesAlignment],
        DescriptionInsights => [AiFeatureKeys.DescriptionIntelligence],
        _ => [],
    };

    /// <summary>The plugin behind a feature.</summary>
    public static string For(AiFeature feature) => feature switch
    {
        AiFeature.SmartLabeling => SmartLabelling,
        AiFeature.TypeLogic => TypeHelper,
        AiFeature.SeriesAlignment => SeriesOrder,
        AiFeature.VibeTags => VibeTags,
        AiFeature.DescriptionIntelligence => DescriptionInsights,
        AiFeature.TasteSummary => TasteSummary,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown Local AI feature."),
    };
}
