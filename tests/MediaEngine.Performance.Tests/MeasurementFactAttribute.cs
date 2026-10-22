namespace MediaEngine.Performance.Tests;

/// <summary>
/// A measurement that prints before/after numbers rather than checking a budget. It takes minutes, so it runs only when
/// <c>TUVIMA_RUN_MEASUREMENTS=1</c> is set (for example <c>TUVIMA_RUN_MEASUREMENTS=1 dotnet test tests/MediaEngine.Performance.Tests
/// --filter "Category=Measurement"</c>); the budget guardrails beside it run on every build.
/// </summary>
public sealed class MeasurementFactAttribute : FactAttribute
{
    public MeasurementFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TUVIMA_RUN_MEASUREMENTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Measurement: set TUVIMA_RUN_MEASUREMENTS=1 to run it.";
        }
    }
}
