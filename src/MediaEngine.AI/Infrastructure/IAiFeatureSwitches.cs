namespace MediaEngine.AI.Infrastructure;

/// <summary>
/// Which Local AI features are switched on. Each feature is an Official plugin, so a feature is on exactly when its
/// plugin is approved and switched on; nothing else holds an on/off flag for it.
/// </summary>
public interface IAiFeatureSwitches
{
    /// <summary>Whether the plugin behind <paramref name="feature"/> is on.</summary>
    bool IsOn(AiFeature feature);

    /// <summary>True when any AI feature plugin is on. This is what "Local AI is on" means for plugins asking for text abilities.</summary>
    bool AnyOn { get; }
}

public static class AiFeatureSwitchesExtensions
{
    /// <summary>
    /// True when any Local AI feature plugin or the audio pack is switched on. This is the meaning of "Local AI is on" for
    /// plugins asking for text abilities.
    /// </summary>
    public static bool IsLocalAiOn(this IAiFeatureSwitches switches, Configuration.AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AudioPackEnabled || switches.AnyOn;
    }
}

/// <summary>The words a vibe tag may be, per kind of media. They live in the vibe tags plugin's settings.</summary>
public interface IVibeVocabularySource
{
    Configuration.AiVibeVocabulary Current { get; }
}
