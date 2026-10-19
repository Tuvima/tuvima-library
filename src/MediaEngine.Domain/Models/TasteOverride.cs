namespace MediaEngine.Domain.Models;

/// <summary>The groups of taste a person can edit.</summary>
public static class TasteGroups
{
    public const string Genre = "genre";
    public const string Mood = "mood";
    public const string Era = "era";

    public static bool IsKnown(string? group) => group is Genre or Mood or Era;
}

/// <summary>What a person did to one learned or typed taste value.</summary>
public static class TasteOverrideActions
{
    public const string Removed = "removed";
    public const string Added = "added";
}

/// <summary>One person's change to their taste: a value hidden (<c>removed</c>) or a value they typed (<c>added</c>).</summary>
public sealed record TasteOverride(Guid ProfileId, string Group, string Value, string Action);
