using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>Stands in for the photo handling: nothing to move or delete, and it remembers who was prepared for removal.</summary>
internal sealed class NoProfilePhotos : IProfilePhotoDisposer
{
    public List<(Guid ProfileId, ProfilePhotoDisposition Photos)> Prepared { get; } = [];

    /// <summary>When true, each prepared person is reported as having had photos moved into the Shared Library.</summary>
    public bool ReportsPhotosMoved { get; set; }

    /// <summary>Runs inside each preparation, for a test that needs something to change between the photo step and the delete.</summary>
    public Action? OnPrepare { get; set; }

    public Task<ProfilePhotoPlan> PrepareAsync(
        Profile profile, ProfilePhotoDisposition photos, Guid actorProfileId, CancellationToken ct = default)
    {
        Prepared.Add((profile.Id, photos));
        OnPrepare?.Invoke();
        return Task.FromResult(new ProfilePhotoPlan([], null, profile.Id, ReportsPhotosMoved));
    }

    /// <summary>Every plan whose files were handed over to be deleted from disk.</summary>
    public List<ProfilePhotoPlan> Completed { get; } = [];

    public Task CompleteAsync(ProfilePhotoPlan plan, CancellationToken ct = default)
    {
        Completed.Add(plan);
        return Task.CompletedTask;
    }

    /// <summary>The people reported as holding personal photos (none unless a test adds some).</summary>
    public HashSet<Guid> ProfilesWithPhotos { get; } = [];

    public Task<bool> HasPersonalPhotosAsync(Guid profileId, CancellationToken ct = default) =>
        Task.FromResult(ProfilesWithPhotos.Contains(profileId));
}
