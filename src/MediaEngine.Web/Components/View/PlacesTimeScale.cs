namespace MediaEngine.Web.Components.View;

/// <summary>One elapsed-day domain for histogram bounds, tick labels and range thumb centers.</summary>
public sealed class PlacesTimeScale
{
    public DateTimeOffset Start { get; }
    public int Days { get; }
    public PlacesTimeScale(DateTimeOffset earliest, DateTimeOffset latest)
    {
        Start = new DateTimeOffset(earliest.UtcDateTime.Date, TimeSpan.Zero);
        var end = new DateTimeOffset(latest.UtcDateTime.Date, TimeSpan.Zero);
        if (end <= Start)
        {
            if (Start.Year > 1)
            {
                Start = Start.AddDays(-1);
            }
            if (end.Year < 9999)
            {
                end = end.AddDays(1);
            }
        }
        Days = Math.Max(1, (int)(end - Start).TotalDays);
    }
    public int Index(DateTimeOffset date) => Math.Clamp((int)(date.UtcDateTime.Date - Start.UtcDateTime).TotalDays, 0, Days);
    public double Percent(int index) => Math.Clamp(index, 0, Days) * 100d / Days;
    public DateTimeOffset DateAt(int index) => Start.AddDays(Math.Clamp(index, 0, Days));
    public IEnumerable<int> Ticks()
    {
        if (Days <= 31)
        {
            return Enumerable.Range(0, Math.Min(4, Days) + 1)
                    .Select(i => (int)Math.Round(i * Days / (double)Math.Min(4, Days))).Distinct();
        }
        var end = DateAt(Days);
        var ticks = new List<int>();
        if (Days > 1461)
        {
            var step = Math.Max(1, (int)Math.Ceiling((end.Year - Start.Year + 1) / 5d));
            for (var year = Start.Year; year <= end.Year; year += step)
            {
                var date = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                if (date >= Start)
                {
                    ticks.Add(Index(date));
                }
            }
        }
        else
        {
            const int step = 1;
            var date = new DateTimeOffset(Start.Year, Start.Month, 1, 0, 0, 0, TimeSpan.Zero);
            if (date < Start)
            {
                date = date.AddMonths(1);
            }
            while (date <= end) { ticks.Add(Index(date)); if (date.Year == 9999 && date.Month + step > 12)
            {
                break;
            } date = date.AddMonths(step); }
        }
        if (ticks.Count == 0)
        {
            return [0, Days];
        }
        if (ticks[0] > 0)
        {
            ticks.Insert(0, 0);
        }
        return ticks;
    }
}
