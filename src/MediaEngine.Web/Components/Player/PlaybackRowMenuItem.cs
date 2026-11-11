namespace MediaEngine.Web.Components.Player;

/// <summary>One action of a list row's "..." menu (a Queue or History row): a stable key, its words, the glyph in front of it and whether a separator is drawn above it.</summary>
public sealed record PlaybackRowMenuItem(string Key, string Label, PlaybackUtilityGlyphKind Glyph, bool Disabled = false, bool DividerBefore = false);
