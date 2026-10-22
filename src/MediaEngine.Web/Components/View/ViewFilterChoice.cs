namespace MediaEngine.Web.Components.View;

/// <summary>One chip in a <see cref="ViewFilterChipRow"/>: the profile or tag it stands for and the bare name it shows.</summary>
public sealed record ViewFilterChoice(Guid Id, string Text);
