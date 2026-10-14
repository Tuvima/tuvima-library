using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Domain.Contracts;

public interface IViewProfileRepository
{
    Task<ViewProfilePolicy> GetPolicyAsync(Guid profileId, CancellationToken ct = default);
    Task<bool> SavePolicyAsync(ViewProfilePolicy policy, CancellationToken ct = default);
    Task<ViewProfilePreferences> GetPreferencesAsync(Guid profileId, CancellationToken ct = default);
    Task<bool> SavePreferencesAsync(ViewProfilePreferences preferences, CancellationToken ct = default);

    /// <summary>
    /// Remembers how this profile likes the Documents list ordered (null for the default order). It is saved on its own
    /// so that saving the scope or density never forgets it, and false when the profile does not exist.
    /// </summary>
    Task<bool> SaveDocumentsSortAsync(Guid profileId, string? sort, CancellationToken ct = default);
}
