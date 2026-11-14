namespace MediaEngine.Domain.Contracts;

public interface IProfileSequencePreferencesRepository
{
    Task<ProfileSequencePreference?> GetAsync(
        Guid profileId,
        string mediaType,
        string containerKey,
        CancellationToken ct = default);

    Task<ProfileSequencePreferenceSaveResult> SaveAsync(
        Guid profileId,
        string mediaType,
        string containerKey,
        bool showMissing,
        CancellationToken ct = default);
}

public sealed record ProfileSequencePreference(
    Guid ProfileId,
    string MediaType,
    string ContainerKey,
    bool ShowMissing,
    DateTimeOffset UpdatedAt);

public sealed record ProfileSequencePreferenceSaveResult(
    bool ProfileExists,
    ProfileSequencePreference? Preference);
