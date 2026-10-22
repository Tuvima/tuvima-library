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

    public Task<ProfilePhotoPlan> PrepareAsync(
        Profile profile, ProfilePhotoDisposition photos, Guid actorProfileId, CancellationToken ct = default)
    {
        Prepared.Add((profile.Id, photos));
        return Task.FromResult(new ProfilePhotoPlan([], null, profile.Id, ReportsPhotosMoved));
    }

    public Task CompleteAsync(ProfilePhotoPlan plan, CancellationToken ct = default) => Task.CompletedTask;
}
