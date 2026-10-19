namespace MediaEngine.Web.Components.Shared;

public sealed record TimelinePeriod(int Key, string Label, int Count, IReadOnlyList<TimelinePeriod>? Children = null);
public enum TimelineGrouping { Year, Decade }

/// <summary>One choice of a timeline grouping control that is not the Year / Decade pair, such as By day or By title on the Activity page.</summary>
public sealed record TimelineGroupingOption(string Key, string Label);
