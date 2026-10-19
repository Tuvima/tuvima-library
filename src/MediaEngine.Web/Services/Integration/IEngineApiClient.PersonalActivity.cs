using MediaEngine.Contracts.Activity;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /api/v1/activity/recent - the signed-in person's latest activity for the Overview strip, music collapsed into sessions. Picture addresses are Dashboard addresses, ready for an image tag.</summary>
    Task<PersonalActivityRecentDto?> GetPersonalActivityRecentAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>GET /api/v1/activity - one page of the Activity list under a scope and filters. Picture addresses are Dashboard addresses.</summary>
    Task<PersonalActivityOutcome<PersonalActivityPageDto>> GetPersonalActivityAsync(PersonalActivityRequest request, CancellationToken ct = default);

    /// <summary>GET /api/v1/activity/periods - how many items fall in each year and month under the same scope and filters, for the date rail.</summary>
    Task<PersonalActivityOutcome<PersonalActivityPeriodsDto>> GetPersonalActivityPeriodsAsync(PersonalActivityRequest request, CancellationToken ct = default);

    /// <summary>GET /api/v1/activity/summary - the signed-in person's lifetime totals. Null when the Engine could not be asked.</summary>
    Task<PersonalActivitySummaryDto?> GetPersonalActivitySummaryAsync(CancellationToken ct = default);

    /// <summary>GET /api/v1/activity/delete-preview - how many entries before the date a deletion in the scope would remove. Server administrators only.</summary>
    Task<PersonalActivityOutcome<PersonalActivityDeletePreviewDto>> PreviewPersonalActivityDeletionAsync(string scope, DateTimeOffset before, CancellationToken ct = default);

    /// <summary>DELETE /api/v1/activity - deletes the entries before the date in the scope. Server administrators only; the Engine logs it.</summary>
    Task<PersonalActivityOutcome<PersonalActivityDeleteResultDto>> DeletePersonalActivityAsync(string scope, DateTimeOffset before, CancellationToken ct = default);
}
