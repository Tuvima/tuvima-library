namespace MediaEngine.Domain.Contracts;

/// <summary>A derived, credential-free cache of the last check of configured provider access.</summary>
public sealed record ProviderConnectionCheck(
    string ProviderName,
    string Status,
    string Message,
    DateTimeOffset CheckedAt,
    int? ResponseTimeMs);

public interface IProviderConnectionCheckRepository
{
    Task<IReadOnlyList<ProviderConnectionCheck>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(ProviderConnectionCheck check, CancellationToken ct = default);
    Task DeleteAsync(string providerName, CancellationToken ct = default);
}
