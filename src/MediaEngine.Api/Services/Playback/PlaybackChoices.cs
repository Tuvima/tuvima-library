using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The "Video quality and devices" choices one start is made under (playback-path spec D15), already cleaned: this person's four
/// profile choices and this device's three. Everything defaults to automatic, so a start with no choices behaves as it always did.
/// </summary>
/// <param name="VideoQuality">One of <c>Auto</c>, <c>High</c> (Highest), <c>Balanced</c>, <c>DataSaver</c>.</param>
/// <param name="MobileDataQuality">The same four, used when the connection is not the home network.</param>
/// <param name="ConvertOnlyWhenNeeded">Off lets the Engine prefer a prepared stream over playing a file as it is.</param>
/// <param name="AllowHdr">Off treats HDR as needing a picture conversion.</param>
/// <param name="MaxResolution">A picture height cap in pixels, or null for automatic.</param>
/// <param name="StereoOnly">True when the device asked for stereo sound only, so surround is never copied.</param>
public sealed record PlaybackChoices(
    string VideoQuality = PlaybackPreferenceValues.Auto,
    string MobileDataQuality = PlaybackPreferenceValues.Auto,
    bool ConvertOnlyWhenNeeded = true,
    bool AllowHdr = true,
    int? MaxResolution = null,
    bool StereoOnly = false)
{
    /// <summary>Nothing chosen: every option automatic.</summary>
    public static PlaybackChoices Automatic { get; } = new();

    public static PlaybackChoices From(WatchingSettingsDto? watching, DeviceVideoOptionsDto? device) => new(
        Clean(watching?.PreferredVideoQuality),
        Clean(watching?.MobileDataVideoQuality),
        watching?.ConvertOnlyWhenNeeded ?? true,
        watching?.AllowHdr ?? true,
        PlaybackChoiceRules.ParseMaxResolution(device?.MaxResolution),
        string.Equals(device?.SurroundSound, VideoOptionValues.SurroundStereo, StringComparison.OrdinalIgnoreCase));

    private static string Clean(string? quality) => PlaybackChoiceRules.IsQuality(quality)
        ? PlaybackChoiceRules.NormalizeQuality(quality!)
        : PlaybackPreferenceValues.Auto;
}

/// <summary>The stored values of the device options, so the list, the cleaning and the rules share one spelling.</summary>
public static class VideoOptionValues
{
    public const string Auto = "auto";
    public const string On = "on";
    public const string Off = "off";
    public const string SurroundPassthrough = "passthrough";
    public const string SurroundStereo = "stereo";

    /// <summary>The picture caps a device may choose, tallest first.</summary>
    public static readonly IReadOnlyList<int> ResolutionCaps = [2160, 1440, 1080, 720, 480];
}

/// <summary>The pure rules that turn the choices into inputs for the ladder and the stream plan, so both stay table-testable.</summary>
public static class PlaybackChoiceRules
{
    /// <summary>The tallest picture Balanced allows.</summary>
    public const int BalancedHeight = 720;

    /// <summary>The tallest picture Data saver allows.</summary>
    public const int DataSaverHeight = 480;

    public static bool IsQuality(string? value) => NormalizeQualityOrNull(value) is not null;

    /// <summary>The stored spelling of a quality (<c>Original</c> from before this list counts as Highest), or the input when unknown.</summary>
    public static string NormalizeQuality(string value) => NormalizeQualityOrNull(value) ?? value;

    private static string? NormalizeQualityOrNull(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" or "automatic" => PlaybackPreferenceValues.Auto,
        "high" or "highest" or "original" => PlaybackPreferenceValues.High,
        "balanced" => PlaybackPreferenceValues.Balanced,
        "datasaver" or "data-saver" => PlaybackPreferenceValues.DataSaver,
        _ => null,
    };

    /// <summary>
    /// The quality that applies: the person's mobile-data choice when the connection is not the home network, otherwise their video quality.
    /// </summary>
    public static string EffectiveQuality(PlaybackChoices choices, PlaybackConnectionContextDto connection) =>
        connection.ConnectionPath == PlaybackConnectionPaths.Local ? choices.VideoQuality : choices.MobileDataQuality;

    /// <summary>The tallest picture a quality allows, or null for no cap (Automatic and Highest).</summary>
    public static int? QualityHeightCap(string quality) => NormalizeQuality(quality) switch
    {
        PlaybackPreferenceValues.Balanced => BalancedHeight,
        PlaybackPreferenceValues.DataSaver => DataSaverHeight,
        _ => null,
    };

    /// <summary>A picture height cap from a stored device value; null for automatic or anything that is not one of the caps.</summary>
    public static int? ParseMaxResolution(string? value) =>
        int.TryParse(value, out var height) && VideoOptionValues.ResolutionCaps.Contains(height) ? height : null;

    /// <summary>The player's own height limit lowered by the device's Maximum resolution.</summary>
    public static int? CapPlayerHeight(int? playerMaxHeight, PlaybackChoices choices) =>
        choices.MaxResolution is not { } cap
            ? playerMaxHeight
            : playerMaxHeight is { } own ? Math.Min(own, cap) : cap;

    /// <summary>
    /// What the person's quality choice asks of a picture of <paramref name="sourceHeight"/>: the cap and the plain reason, or null when
    /// the picture already fits or the choice sets no cap.
    /// </summary>
    public static (int Height, string Reason)? QualityCap(PlaybackChoices choices, PlaybackConnectionContextDto connection, int? sourceHeight)
    {
        var quality = EffectiveQuality(choices, connection);
        if (QualityHeightCap(quality) is not { } cap || sourceHeight is not > 0 || sourceHeight <= cap)
        {
            return null;
        }

        var name = NormalizeQuality(quality) == PlaybackPreferenceValues.DataSaver ? "Data saver" : "Balanced quality";
        var place = connection.ConnectionPath == PlaybackConnectionPaths.Local ? string.Empty : " on mobile data";
        return (cap, $"{name}{place} keeps the picture at {cap}p");
    }

    /// <summary>True when the file's picture is HDR (HDR10 or HLG), from its transfer characteristic.</summary>
    public static bool IsHdr(MediaProbeResult? probe) => probe?.ColorTransfer?.Trim().ToLowerInvariant() is "smpte2084" or "arib-std-b67";
}
