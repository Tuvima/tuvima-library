namespace MediaEngine.Domain.Entities;

/// <summary>
/// A household: the people (profiles) who live together and the users (sign-ins) that open them.
/// A profile belongs to exactly one household, and a user (sign-in) belongs to the household of the profile it opens. A household holds up to
/// <see cref="MaximumProfiles"/> profiles.
/// </summary>
public sealed class Household
{
    /// <summary>The most profiles one household can hold.</summary>
    public const int MaximumProfiles = 8;

    /// <summary>The plain message shown when a household is full.</summary>
    public const string FullMessage = "A household can have up to 8 people.";

    public Household(Guid id, string name, DateTimeOffset createdAt, bool isDefault = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A household needs a name.", nameof(name));
        }

        Id = id;
        Name = name.Trim();
        CreatedAt = createdAt;
        IsDefault = isDefault;
    }

    public Guid Id { get; }
    public string Name { get; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// True for the server administrator's original household: exactly one household has it, and it is never deleted.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>The default name for the household an account starts, for example "Sam's household".</summary>
    public static string DefaultNameFor(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? "Household" : $"{displayName.Trim()}'s household";
}
