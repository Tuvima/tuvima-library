namespace MediaEngine.Web.Components.Shared;

/// <summary>How a summary count is coloured: by meaning (total in accent, switched on in green, needs attention in amber, nothing in grey).</summary>
public enum AppSummaryTone
{
    None,
    Accent,
    Ok,
    Warn,
    Mute,
}

/// <summary>One count in an <see cref="AppSummaryStrip"/>: the words, the number, an optional icon and the colour of its meaning.</summary>
public sealed record AppSummaryItem(string Label, string Value, string? Icon = null, AppSummaryTone Tone = AppSummaryTone.None);
