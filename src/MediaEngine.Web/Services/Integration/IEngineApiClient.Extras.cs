using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /api/v1/extras/{id} - the files behind one Extras card, for the Extras viewer. Null when it is gone or not allowed.</summary>
    Task<ExtraViewDto?> GetExtraAsync(string extraId, CancellationToken ct = default);
}
