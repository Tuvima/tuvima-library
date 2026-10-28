namespace MediaEngine.Domain.Contracts;

/// <summary>How one person wants the Home hero to behave. These only narrow what the hero rules choose from.</summary>
public sealed record ProfileHeroSettings(bool Enabled, int SlideCount, bool AllowWatch, bool AllowRead, bool AllowListen, bool ContinuedOnly)
{
    public const int MaxSlides = 5;

    /// <summary>Hero on, five slides, every type, in-progress items first and new items after.</summary>
    public static ProfileHeroSettings Default { get; } = new(true, MaxSlides, true, true, true, false);

    /// <summary>The same settings with the slide count kept inside what the hero can show.</summary>
    public ProfileHeroSettings Clamped() => this with { SlideCount = Math.Clamp(SlideCount, 1, MaxSlides) };
}

/// <summary>The Home hero settings each profile chose, so they follow the person to every device.</summary>
public interface IProfileHeroSettingsRepository
{
    /// <summary>The profile's settings, or <see cref="ProfileHeroSettings.Default"/> when it never changed them.</summary>
    Task<ProfileHeroSettings> GetAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Remembers (or replaces) the settings. Throws <see cref="KeyNotFoundException"/> when the profile does not exist.</summary>
    Task SaveAsync(Guid profileId, ProfileHeroSettings settings, CancellationToken ct = default);

    /// <summary>The ids of the plugins whose Home slides this profile switched off. A plugin that is not listed is on.</summary>
    Task<IReadOnlySet<string>> GetSwitchedOffPluginsAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Switches one plugin's Home slides on or off for the profile. Throws <see cref="KeyNotFoundException"/> when the profile does not exist.</summary>
    Task SetPluginSlidesAsync(Guid profileId, string pluginId, bool enabled, CancellationToken ct = default);
}
