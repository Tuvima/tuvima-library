namespace MediaEngine.Web.Components.Shared;

/// <summary>A bounded numeric series supplied to the native coverage chart.</summary>
public sealed class AppChartSeries<T>
{
    public string? Name { get; set; }
    public AppChartData<T> Data { get; set; } = new([]);
}

public sealed class AppChartData<T>(IEnumerable<T> values)
{
    public IReadOnlyList<T> Values { get; } = values.ToArray();
}

public sealed class AppDonutChartOptions
{
    public bool ShowLegend { get; set; }
    public bool ShowToolTips { get; set; }
    public double DonutRingRatio { get; set; } = 0.42;
    public string[] ChartPalette { get; set; } = ["#3F94F6", "#5E43D6"];
}
