using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>What happened when someone changed one video option.</summary>
public enum VideoOptionOutcome
{
    Saved,
    UnknownOption,
    InvalidValue,
    NoDevice,
}

/// <summary>
/// The ONE definition of the "Video quality and devices" list (playback-path spec D15): ids, labels, kinds, choices, defaults and which
/// layer keeps each. Every client draws the list the Engine serves from here, so a new client reads it instead of being rebuilt, and no
/// client can show a row another one lacks.
/// </summary>
public static class VideoOptionCatalog
{
    /// <summary>Every row's id in the order to draw them. The list never varies by client, person or device.</summary>
    public static IReadOnlyList<string> Ids { get; } =
    [
        VideoOptionIds.VideoQuality,
        VideoOptionIds.MobileDataQuality,
        VideoOptionIds.ConvertOnlyWhenNeeded,
        VideoOptionIds.AllowHdr,
        VideoOptionIds.MaxResolution,
        VideoOptionIds.HardwareDecoding,
        VideoOptionIds.SurroundSound,
        VideoOptionIds.DeviceAbilities,
        VideoOptionIds.CheckAgain,
    ];

    private static readonly IReadOnlyList<VideoOptionChoiceDto> QualityChoices =
    [
        Choice(PlaybackPreferenceValues.Auto, "Automatic"),
        Choice(PlaybackPreferenceValues.High, "Highest"),
        Choice(PlaybackPreferenceValues.Balanced, "Balanced"),
        Choice(PlaybackPreferenceValues.DataSaver, "Data saver"),
    ];

    private static readonly IReadOnlyList<VideoOptionChoiceDto> ResolutionChoices =
    [
        Choice(VideoOptionValues.Auto, "Automatic"),
        .. VideoOptionValues.ResolutionCaps.Select(height => Choice(height.ToString(System.Globalization.CultureInfo.InvariantCulture), height >= 2160 ? "4K" : $"{height}p")),
    ];

    private static readonly IReadOnlyList<VideoOptionChoiceDto> HardwareChoices =
    [
        Choice(VideoOptionValues.Auto, "Automatic"),
        Choice(VideoOptionValues.On, "On"),
        Choice(VideoOptionValues.Off, "Off"),
    ];

    private static readonly IReadOnlyList<VideoOptionChoiceDto> SurroundChoices =
    [
        Choice(VideoOptionValues.Auto, "Automatic"),
        Choice(VideoOptionValues.SurroundPassthrough, "Pass through"),
        Choice(VideoOptionValues.SurroundStereo, "Stereo only"),
    ];

    /// <summary>The list with the saved values of one person (profile rows) and one device (device rows).</summary>
    /// <param name="watching">The person's saved choices; defaults when null.</param>
    /// <param name="device">What the device reported, with its saved device choices; null for a device that has not reported.</param>
    public static VideoPlaybackOptionsDto Build(WatchingSettingsDto? watching, ClientCapabilitiesDto? device)
    {
        watching ??= new WatchingSettingsDto();
        var options = ClientCapabilitiesNormalizer.CleanOptions(device?.VideoOptions);
        var learned = device?.LearnedLimits.Where(limit => !string.IsNullOrWhiteSpace(limit.Summary))
            .Select(limit => $"Learned: {limit.Summary}").ToArray() ?? [];

        return new VideoPlaybackOptionsDto
        {
            Options =
            [
                Row(VideoOptionIds.VideoQuality, "Video quality", VideoOptionKinds.Choice, VideoOptionScopes.Profile, QualityChoices,
                    PlaybackChoiceRules.IsQuality(watching.PreferredVideoQuality) ? PlaybackChoiceRules.NormalizeQuality(watching.PreferredVideoQuality) : PlaybackPreferenceValues.Auto,
                    PlaybackPreferenceValues.Auto),
                Row(VideoOptionIds.MobileDataQuality, "Quality on mobile data", VideoOptionKinds.Choice, VideoOptionScopes.Profile, QualityChoices,
                    PlaybackChoiceRules.IsQuality(watching.MobileDataVideoQuality) ? PlaybackChoiceRules.NormalizeQuality(watching.MobileDataVideoQuality) : PlaybackPreferenceValues.Auto,
                    PlaybackPreferenceValues.Auto),
                Row(VideoOptionIds.ConvertOnlyWhenNeeded, "Convert only when needed", VideoOptionKinds.Toggle, VideoOptionScopes.Profile, [],
                    OnOff(watching.ConvertOnlyWhenNeeded), VideoOptionValues.On,
                    "Off lets Library prepare a stream even for a file your device could play as it is."),
                Row(VideoOptionIds.AllowHdr, "Allow HDR", VideoOptionKinds.Toggle, VideoOptionScopes.Profile, [],
                    OnOff(watching.AllowHdr), VideoOptionValues.On,
                    "Off converts HDR pictures so they look right on any screen."),
                Row(VideoOptionIds.MaxResolution, "Maximum resolution", VideoOptionKinds.Choice, VideoOptionScopes.Device, ResolutionChoices,
                    options.MaxResolution, VideoOptionValues.Auto),
                Row(VideoOptionIds.HardwareDecoding, "Use hardware decoding", VideoOptionKinds.Choice, VideoOptionScopes.Device, HardwareChoices,
                    options.HardwareDecoding, VideoOptionValues.Auto,
                    "For when this device's decoder misbehaves."),
                Row(VideoOptionIds.SurroundSound, "Surround sound", VideoOptionKinds.Choice, VideoOptionScopes.Device, SurroundChoices,
                    options.SurroundSound, VideoOptionValues.Auto),
                Row(VideoOptionIds.DeviceAbilities, "What this device can play", VideoOptionKinds.Summary, VideoOptionScopes.Device, [],
                    DeviceAbilityLine.Describe(device) ?? "Not measured yet", string.Empty),
                Row(VideoOptionIds.CheckAgain, "Check again", VideoOptionKinds.Action, VideoOptionScopes.Device, [], string.Empty, string.Empty,
                    "Measures what this device can play again."),
            ],
            Learned = learned,
        };
    }

    /// <summary>
    /// The choices a start is made under, from the person's saved values and what the device saved. Nothing saved means everything
    /// automatic.
    /// </summary>
    public static PlaybackChoices ChoicesFor(WatchingSettingsDto? watching, ClientCapabilitiesDto? device) =>
        PlaybackChoices.From(watching, device?.VideoOptions);

    /// <summary>Whether <paramref name="id"/> is a row kept per person.</summary>
    public static bool IsProfileOption(string id) =>
        id is VideoOptionIds.VideoQuality or VideoOptionIds.MobileDataQuality or VideoOptionIds.ConvertOnlyWhenNeeded or VideoOptionIds.AllowHdr;

    /// <summary>Whether <paramref name="id"/> is a row kept per device that can be changed.</summary>
    public static bool IsDeviceOption(string id) =>
        id is VideoOptionIds.MaxResolution or VideoOptionIds.HardwareDecoding or VideoOptionIds.SurroundSound;

    /// <summary>Puts one choice into a person's settings; false when the value is not one the list offers.</summary>
    public static bool TryApplyProfileOption(WatchingSettingsDto watching, string id, string value)
    {
        switch (id)
        {
            case VideoOptionIds.VideoQuality when PlaybackChoiceRules.IsQuality(value):
                watching.PreferredVideoQuality = PlaybackChoiceRules.NormalizeQuality(value);
                return true;
            case VideoOptionIds.MobileDataQuality when PlaybackChoiceRules.IsQuality(value):
                watching.MobileDataVideoQuality = PlaybackChoiceRules.NormalizeQuality(value);
                return true;
            case VideoOptionIds.ConvertOnlyWhenNeeded when TryOnOff(value, out var convert):
                watching.ConvertOnlyWhenNeeded = convert;
                return true;
            case VideoOptionIds.AllowHdr when TryOnOff(value, out var hdr):
                watching.AllowHdr = hdr;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The device's choices with one changed; null when the value is not one the list offers.</summary>
    public static DeviceVideoOptionsDto? TryApplyDeviceOption(DeviceVideoOptionsDto? current, string id, string value)
    {
        var clean = ClientCapabilitiesNormalizer.CleanOptions(current);
        var trimmed = value.Trim().ToLowerInvariant();
        return id switch
        {
            VideoOptionIds.MaxResolution when trimmed == VideoOptionValues.Auto || PlaybackChoiceRules.ParseMaxResolution(trimmed) is not null =>
                new DeviceVideoOptionsDto { MaxResolution = trimmed, HardwareDecoding = clean.HardwareDecoding, SurroundSound = clean.SurroundSound },
            VideoOptionIds.HardwareDecoding when trimmed is VideoOptionValues.Auto or VideoOptionValues.On or VideoOptionValues.Off =>
                new DeviceVideoOptionsDto { MaxResolution = clean.MaxResolution, HardwareDecoding = trimmed, SurroundSound = clean.SurroundSound },
            VideoOptionIds.SurroundSound when trimmed is VideoOptionValues.Auto or VideoOptionValues.SurroundPassthrough or VideoOptionValues.SurroundStereo =>
                new DeviceVideoOptionsDto { MaxResolution = clean.MaxResolution, HardwareDecoding = clean.HardwareDecoding, SurroundSound = trimmed },
            _ => null,
        };
    }

    private static bool TryOnOff(string value, out bool on)
    {
        on = false;
        switch (value.Trim().ToLowerInvariant())
        {
            case VideoOptionValues.On:
                on = true;
                return true;
            case VideoOptionValues.Off:
                return true;
            default:
                return false;
        }
    }

    private static string OnOff(bool on) => on ? VideoOptionValues.On : VideoOptionValues.Off;

    private static VideoOptionChoiceDto Choice(string value, string label) => new() { Value = value, Label = label };

    private static VideoOptionDto Row(
        string id, string label, string kind, string scope, IReadOnlyList<VideoOptionChoiceDto> choices, string value, string defaultValue, string? help = null) => new()
    {
        Id = id,
        Label = label,
        Kind = kind,
        Scope = scope,
        Choices = choices,
        Value = value,
        DefaultValue = defaultValue,
        Help = help,
    };
}

/// <summary>
/// Reads and saves the "Video quality and devices" values (playback-path spec D15): the person's four rows in their playback settings
/// and the device's three rows on the device record (or, for the Dashboard's browser, with the browser's report).
/// </summary>
public sealed class VideoOptionsService(IUserPlaybackSettingsService settings)
{
    /// <summary>The whole list with the saved values of <paramref name="profileId"/> and of the device that reported <paramref name="device"/>.</summary>
    public async Task<VideoPlaybackOptionsDto> BuildAsync(Guid? profileId, ClientCapabilitiesDto? device, CancellationToken ct = default) =>
        VideoOptionCatalog.Build(await LoadWatchingAsync(profileId, ct).ConfigureAwait(false), device);

    /// <summary>The choices a start is made under; automatic when nobody is signed in.</summary>
    public async Task<PlaybackChoices> ResolveChoicesAsync(Guid? profileId, ClientCapabilitiesDto? device, CancellationToken ct = default) =>
        VideoOptionCatalog.ChoicesFor(await LoadWatchingAsync(profileId, ct).ConfigureAwait(false), device);

    /// <summary>Saves one of the person's own rows. The value is checked against the list; nothing else in their settings changes.</summary>
    public async Task<VideoOptionOutcome> SaveProfileOptionAsync(Guid profileId, string id, string value, CancellationToken ct = default)
    {
        if (!VideoOptionCatalog.IsProfileOption(id))
        {
            return VideoOptionOutcome.UnknownOption;
        }

        var current = await settings.GetAsync(profileId, ct).ConfigureAwait(false);
        if (!VideoOptionCatalog.TryApplyProfileOption(current.Watching, id, value))
        {
            return VideoOptionOutcome.InvalidValue;
        }

        await settings.UpdateAsync(profileId, current, ct).ConfigureAwait(false);
        return VideoOptionOutcome.Saved;
    }

    private async Task<WatchingSettingsDto?> LoadWatchingAsync(Guid? profileId, CancellationToken ct)
    {
        if (profileId is not { } id || id == Guid.Empty)
        {
            return null;
        }

        try
        {
            return (await settings.GetAsync(id, ct).ConfigureAwait(false)).Watching;
        }
        catch (KeyNotFoundException)
        {
            // A profile that no longer exists has nothing saved; every option is automatic.
            return null;
        }
    }
}
