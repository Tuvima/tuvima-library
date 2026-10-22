using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A hand-written spy for the activity ledger: it remembers every entry written and can be told to fail, so a test
/// can check what a service wrote and that a failed write never breaks the service.
/// </summary>
internal sealed class RecordingActivityLog : ISystemActivityRepository
{
    public List<SystemActivityEntry> Entries { get; } = [];

    public bool Fail { get; set; }

    public Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("The log is unavailable.");
        }

        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<long> CountAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(IReadOnlyList<string> actionTypes, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(Guid profileId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<(IReadOnlyList<SystemActivityEntry> Items, int Total)> QueryAsync(IReadOnlyList<string> actionTypes, string? search, int offset, int limit, string? mediaType, CancellationToken ct = default) => throw new NotSupportedException();
}
