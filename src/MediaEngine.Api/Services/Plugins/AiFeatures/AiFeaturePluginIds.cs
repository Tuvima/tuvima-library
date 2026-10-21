using MediaEngine.AI.Infrastructure;

namespace MediaEngine.Api.Services.Plugins.AiFeatures;

/// <summary>The plugin ids of the Local AI features. Each feature is an Official built-in plugin, on exactly when its plugin is on.</summary>
internal static class AiFeaturePluginIds
{
    public const string SmartLabelling = "tuvima.ai-smart-labelling";
    public const string TypeHelper = "tuvima.ai-type-helper";
    public const string SeriesOrder = "tuvima.ai-series-order";
    public const string VibeTags = "tuvima.ai-vibe-tags";
    public const string DescriptionInsights = "tuvima.ai-description-insights";

    public static readonly IReadOnlyList<string> All =
    [
        SmartLabelling,
        TypeHelper,
        SeriesOrder,
        VibeTags,
        DescriptionInsights,
    ];

    /// <summary>The plugin behind a feature.</summary>
    public static string For(AiFeature feature) => feature switch
    {
        AiFeature.SmartLabeling => SmartLabelling,
        AiFeature.TypeLogic => TypeHelper,
        AiFeature.SeriesAlignment => SeriesOrder,
        AiFeature.VibeTags => VibeTags,
        AiFeature.DescriptionIntelligence => DescriptionInsights,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown Local AI feature."),
    };
}
