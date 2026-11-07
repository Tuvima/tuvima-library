namespace MediaEngine.Domain.Constants;

/// <summary>
/// Persisted values for <c>works.wikidata_status</c>. These are strings at the
/// storage/API boundary, so constants preserve the current wire format while
/// preventing new spelling variants in application code.
/// </summary>
public static class WorkWikidataStatus
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Missing = "missing";
    public const string Manual = "manual";
    public const string ProviderOnly = "provider_only";
    public const string AutoAligned = "auto_aligned";
    public const string UserConfirmed = "user_confirmed";
    public const string UserReplaced = "user_replaced";
    public const string UserRejected = "user_rejected";

    /// <summary>
    /// True when the Wikidata state of a Work is the Library's own doing (automatic alignment, a retail match) and not a person's: not locked, not
    /// from the user, and not a status a person set. Only such a state may be cleared or replaced by the automatic alignment.
    /// </summary>
    public static bool IsAutomationOwned(string? status, string? source, bool locked) =>
        !locked
        && !string.Equals(source, WorkWikidataMatchSource.User, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(status, UserConfirmed, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(status, UserReplaced, StringComparison.OrdinalIgnoreCase);
}

public static class WorkWikidataMatchSource
{
    public const string Retail = "retail";
    public const string User = "user";
}

public static class WorkIdentityMatchLevel
{
    public const string Work = "work";
    public const string Edition = "edition";
    public const string RetailOnly = "retail_only";
}
