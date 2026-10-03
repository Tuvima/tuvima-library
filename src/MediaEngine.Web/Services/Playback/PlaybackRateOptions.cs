using System.Globalization;
using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Canonical playback-rate choices and invariant serialization for the player menus.
/// </summary>
public static class PlaybackRateOptions
{
    public const double MinimumRate = PlaybackRatePolicy.Minimum;
    public const double MaximumRate = PlaybackRatePolicy.Maximum;
    public const double ChoiceStep = 0.1d;

    private static readonly double[] FractionalChoices = [0.75d, 1.25d, 1.75d];

    public static IReadOnlyList<PlaybackRateOption> BuildChoices(double? currentRate = null)
    {
        var rates = new SortedSet<double>();
        for (var rate = 0.5m; rate <= 3m; rate += 0.1m)
        {
            rates.Add((double)rate);
        }

        foreach (var rate in FractionalChoices)
        {
            rates.Add(rate);
        }

        if (currentRate is { } current && IsValid(current))
        {
            rates.Add(current);
        }

        return rates.Select(rate => new PlaybackRateOption(
            rate,
            ToWireValue(rate),
            FormatLabel(rate))).ToArray();
    }

    public static bool IsValid(double rate) => PlaybackRatePolicy.IsValid(rate);

    /// <summary>Uses the round-trip representation so a current rate is never rounded in the wire value.</summary>
    public static string ToWireValue(double rate)
    {
        if (!IsValid(rate)) throw new ArgumentOutOfRangeException(nameof(rate));
        return rate.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Creates the compact user-facing value, for example <c>1.25x</c>.</summary>
    public static string FormatLabel(double rate)
    {
        if (!IsValid(rate)) throw new ArgumentOutOfRangeException(nameof(rate));
        var exact = ToWireValue(rate);
        if (!exact.Contains(".", StringComparison.Ordinal)) exact += ".0";
        return $"{exact}x";
    }

    public static bool TryParseWireValue(string? value, out double rate)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate)
            && IsValid(rate))
        {
            return true;
        }

        rate = default;
        return false;
    }
}

public sealed record PlaybackRateOption(double Rate, string WireValue, string Label);
