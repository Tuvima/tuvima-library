namespace MediaEngine.Domain.Entities;

/// <summary>
/// A household: the people (profiles) who live together and the sign-ins (accounts) that open them.
/// A profile and an account each belong to exactly one household. A household holds up to
/// <see cref="MaximumProfiles"/> profiles.
/// </summary>
public sealed class Household
{
    /// <summary>The most profiles one household can hold.</summary>
    public const int MaximumProfiles = 8;

    /// <summary>The plain message shown when a household is full.</summary>
    public const string FullMessage = "A household can have up to 8 people.";

    public Household(Guid id, string name, DateTimeOffset createdAt, Guid? primaryAccountId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A household needs a name.", nameof(name));
        }

        Id = id;
        Name = name.Trim();
        CreatedAt = createdAt;
        PrimaryAccountId = primaryAccountId;
    }

    public Guid Id { get; }
    public string Name { get; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// The sign-in the server administrator created for this household. Its library and feature access is the most
    /// the household's administrator can hand out. <see langword="null"/> until the household has a main sign-in.
    /// </summary>
    public Guid? PrimaryAccountId { get; }

    /// <summary>The default name for the household an account starts, for example "Sam's household".</summary>
    public static string DefaultNameFor(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? "Household" : $"{displayName.Trim()}'s household";
}
