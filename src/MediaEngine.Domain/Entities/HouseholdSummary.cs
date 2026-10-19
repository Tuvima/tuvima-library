namespace MediaEngine.Domain.Entities;

/// <summary>A household with how many people (profiles) and sign-ins (accounts) it holds.</summary>
public sealed record HouseholdSummary(Household Household, int ProfileCount, int AccountCount)
{
    /// <summary>True when nothing belongs to the household, so it can be removed without moving anything.</summary>
    public bool IsEmpty => ProfileCount == 0 && AccountCount == 0;
}

/// <summary>What removing a household did. Removing never moves or deletes a person or a sign-in.</summary>
public enum HouseholdDeleteOutcome
{
    Deleted,
    NotFound,

    /// <summary>The household still has at least one person or sign-in.</summary>
    NotEmpty,

    /// <summary>The household's Shared Library in View still holds shared photos or videos.</summary>
    HoldsSharedItems,
}
