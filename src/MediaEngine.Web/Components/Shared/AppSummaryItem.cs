namespace MediaEngine.Web.Components.Shared;

/// <summary>One count in an <see cref="AppSummaryStrip"/>: the words, the number and an optional icon.</summary>
public sealed record AppSummaryItem(string Label, string Value, string? Icon = null);
