namespace MediaEngine.Domain.Services;

/// <summary>Validation rules for ordinary playback speed. Scan commands use their own policy.</summary>
public static class PlaybackRatePolicy
{
    public const double Minimum = 0.5d;
    public const double Maximum = 3d;
    public const double ScanMinimum = 1d;
    public const double ScanMaximum = 32d;

    public static bool IsValid(double rate) => double.IsFinite(rate) && rate >= Minimum && rate <= Maximum;

    public static bool IsValidScan(double rate) => double.IsFinite(rate) && rate >= ScanMinimum && rate <= ScanMaximum;

    public static double RequireValid(double rate, string? parameterName = null) =>
        IsValid(rate) ? rate : throw new ArgumentOutOfRangeException(parameterName ?? nameof(rate), rate,
            $"Playback speed must be finite and between {Minimum} and {Maximum}.");

    public static double RequireValidScan(double rate, string? parameterName = null) =>
        IsValidScan(rate) ? rate : throw new ArgumentOutOfRangeException(parameterName ?? nameof(rate), rate,
            $"Scan speed must be finite and between {ScanMinimum} and {ScanMaximum}.");
}
