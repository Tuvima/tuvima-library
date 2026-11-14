using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Playback;

/// <summary>What a graphics hardware answer looks like on a screen: a badge, one headline and the lines under it.</summary>
/// <param name="Badge">The short status word ("Working", "Using processor", "Processor", "Waiting").</param>
/// <param name="Tone">The badge colour.</param>
/// <param name="Headline">The one line that says what was found.</param>
/// <param name="Lines">Plain lines under the headline (what Library does instead, what to do next); empty in first-run setup.</param>
/// <param name="OffersTools">True when the fix is a newer video tool, so the card links to the Tools page.</param>
/// <param name="Detail">FFmpeg's own text behind Show details, or null.</param>
public sealed record GraphicsHardwareView(string Badge, AppUiTone Tone, string Headline, IReadOnlyList<string> Lines, bool OffersTools, string? Detail);

/// <summary>
/// The words for the graphics hardware answer, written once so Settings &gt; Playback &gt; Quality and the Graphics hardware row in first-run setup
/// say the same thing (playback-path spec D11, section 4).
/// </summary>
public static class GraphicsHardwareText
{
    /// <summary>The Quality card (mockup screens 2 and 3).</summary>
    public static GraphicsHardwareView ForCard(GraphicsHardwareDto hardware, IStringLocalizer<SharedStrings> L)
    {
        switch (hardware.Outcome)
        {
            case GraphicsHardwareOutcomes.Works:
                var lines = new List<string>();
                if (hardware.TestedAt is not null)
                {
                    lines.Add(L["GraphicsHardware_TestedJustNow"].Value);
                }

                if (hardware.ProcessorOnlyChosen)
                {
                    lines.Add(L["GraphicsHardware_ProcessorOnly"].Value);
                }

                return new GraphicsHardwareView(L["GraphicsHardware_BadgeWorking"].Value, AppUiTone.Success, Works(hardware, L), lines, false, null);

            case GraphicsHardwareOutcomes.Failed:
                return new GraphicsHardwareView(
                    L["GraphicsHardware_BadgeUsingProcessor"].Value,
                    AppUiTone.Warning,
                    string.IsNullOrWhiteSpace(hardware.CardName) ? L["GraphicsHardware_Found"].Value : L["GraphicsHardware_FoundNamed", hardware.CardName].Value,
                    [L["GraphicsHardware_FailedReason", Lowered(hardware.Reason)].Value, L["GraphicsHardware_FailedFallback"].Value],
                    true,
                    hardware.Detail);

            case GraphicsHardwareOutcomes.None:
                return new GraphicsHardwareView(
                    L["GraphicsHardware_BadgeProcessor"].Value, AppUiTone.Neutral, L["GraphicsHardware_None"].Value, [L["GraphicsHardware_NoneHint"].Value], false, null);

            default:
                return new GraphicsHardwareView(
                    L["GraphicsHardware_BadgeWaiting"].Value, AppUiTone.Neutral, L["GraphicsHardware_Unavailable"].Value, [], false, null);
        }
    }

    /// <summary>The one line of the Graphics hardware row in first-run setup (decision D11): the same three outcomes, no extra lines.</summary>
    public static GraphicsHardwareView ForSetupRow(GraphicsHardwareDto? hardware, IStringLocalizer<SharedStrings> L)
    {
        switch (hardware?.Outcome)
        {
            case GraphicsHardwareOutcomes.Works:
                return new GraphicsHardwareView(L["GraphicsHardware_BadgeWorking"].Value, AppUiTone.Success, Works(hardware, L), [], false, null);

            case GraphicsHardwareOutcomes.Failed:
                return new GraphicsHardwareView(
                    L["GraphicsHardware_BadgeUsingProcessor"].Value, AppUiTone.Warning,
                    L["GraphicsHardware_SetupFailed", Lowered(hardware.Reason)].Value, [], false, hardware.Detail);

            case GraphicsHardwareOutcomes.None:
                return new GraphicsHardwareView(
                    L["GraphicsHardware_BadgeProcessor"].Value, AppUiTone.Neutral, L["GraphicsHardware_SetupNone"].Value, [], false, null);

            default:
                // FFmpeg is not ready (or the Engine could not be asked yet): the row is filled in on its own once it is.
                return new GraphicsHardwareView(
                    L["GraphicsHardware_BadgeWaiting"].Value, AppUiTone.Neutral, L["GraphicsHardware_SetupWaiting"].Value, [], false, null);
        }
    }

    private static string Works(GraphicsHardwareDto hardware, IStringLocalizer<SharedStrings> L) =>
        L["GraphicsHardware_Works", hardware.CardName ?? hardware.Accelerator ?? L["GraphicsHardware_Title"].Value].Value;

    /// <summary>
    /// Starts the Engine's reason in lower case so it reads after a colon, unless it begins with a name or an acronym
    /// ("FFmpeg", "NVIDIA"), which keep their capitals.
    /// </summary>
    internal static string Lowered(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }

        var text = reason.Trim();
        return text.Length > 1 && char.IsUpper(text[0]) && char.IsLower(text[1])
            ? char.ToLowerInvariant(text[0]) + text[1..]
            : text;
    }
}
