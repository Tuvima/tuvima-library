using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Domain.Contracts;

public interface IViewPersonalSpaceRepository
{
    Task<(int Sources, int Items)> GetInventoryAsync(CancellationToken ct = default);
    Task<ViewPersonalSpace?> GetByOwnerAsync(Guid ownerProfileId, CancellationToken ct = default);
    Task<ViewPersonalSpace?> GetByLibraryAsync(Guid libraryId, CancellationToken ct = default);
    Task<IReadOnlyList<ViewPersonalSpace>> GetAllAsync(CancellationToken ct = default);
    Task<ViewPersonalSpace> CreateAsync(Guid ownerProfileId, Guid libraryId, CancellationToken ct = default);
    Task<IReadOnlyList<ViewSource>> GetSourcesAsync(Guid personalSpaceId, CancellationToken ct = default);
    Task<ViewSource> UpsertSourceAsync(ViewSource source, CancellationToken ct = default);
    Task<bool> DeleteSourceAsync(Guid personalSpaceId, Guid sourceId, CancellationToken ct = default);
    /// <summary>Records how many files the folder's last full scan skipped because they do not fit its kind.</summary>
    Task SetSourceSkippedFileCountAsync(Guid personalSpaceId, Guid sourceId, int skippedFileCount, CancellationToken ct = default);
    Task<IReadOnlyList<ViewDevice>> GetDevicesAsync(Guid personalSpaceId, CancellationToken ct = default);
    Task<ViewDevice> UpsertDeviceAsync(ViewDevice device, CancellationToken ct = default);
}
