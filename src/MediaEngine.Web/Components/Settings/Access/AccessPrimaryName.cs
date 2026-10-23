using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// Names the household's main sign-in the way a person's page shows it ("Hidden by Alex", "Follows Alex's access."): the name of
/// the profile that sign-in opens by default, else its email. Null when the Dashboard cannot tell which sign-in is meant.
/// </summary>
internal static class AccessPrimaryName
{
    public static string? For(IEnumerable<AccountAccessResponse> accounts, AccountAccessResponse? person)
    {
        if (person is null)
        {
            return null;
        }

        var list = accounts as IReadOnlyCollection<AccountAccessResponse> ?? accounts.ToList();
        if (person.GrantsInheritFromAccountId is { } primaryId)
        {
            return Label(list.FirstOrDefault(account => account.Id == primaryId));
        }

        // A sign-in with its own access: the household's main sign-in is its one administrator that follows nobody.
        if (person.HouseholdId is null)
        {
            return null;
        }

        var candidates = list
            .Where(account => account.HouseholdId == person.HouseholdId && account.Id != person.Id &&
                              account.HouseholdAdmin && account.GrantsInheritFromAccountId is null)
            .ToList();
        return candidates.Count == 1 ? Label(candidates[0]) : null;
    }

    private static string? Label(AccountAccessResponse? account)
    {
        if (account is null)
        {
            return null;
        }

        var name = account.ProfileGrants.FirstOrDefault(grant => grant.IsDefault)?.DisplayName;
        return !string.IsNullOrWhiteSpace(name) ? name : string.IsNullOrWhiteSpace(account.Email) ? null : account.Email;
    }
}
