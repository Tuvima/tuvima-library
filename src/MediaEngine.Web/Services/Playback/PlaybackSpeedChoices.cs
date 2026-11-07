using System.Globalization;
using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// What the Speed panel offers (unified player v2 D15, mockup screen 5): five quick presets, a slider from the app's slowest to fastest supported
/// speed in 0.05 steps, and a value written the way the mockup writes it ("1.25×"). A preset counts as chosen only when the speed equals it.
/// </summary>
public static class PlaybackSpeedChoices
{
    public const double Minimum = PlaybackRatePolicy.Minimum;
    public const double Maximum = PlaybackRatePolicy.Maximum;
    public const double Step = 0.05d;
    public const double Normal = 1d;

    /// <summary>The quick presets, slowest first.</summary>
    public static IReadOnlyList<double> Presets { get; } = [0.75d, 1d, 1.25d, 1.5d, 2d];

    /// <summary>True when the speed is the preset (the slider snaps to thousandths, so a hair of tolerance keeps 1.25 equal to 1.2500000000000002).</summary>
    public static bool Matches(double rate, double preset) => Math.Abs(rate - preset) < 0.0005d;

    public static bool IsNormal(double rate) => Matches(rate, Normal);

    /// <summary>The speed as a person reads it: "1×", "1.25×", "0.75×".</summary>
    public static string Format(double rate) =>
        Math.Round(rate, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture) + "×";
}
