using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Activity;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<PersonalActivityRecentDto?> GetPersonalActivityRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        var recent = await GetAsync<PersonalActivityRecentDto>(
            "GET /api/v1/activity/recent",
            "/api/v1/activity/recent",
            new Dictionary<string, string?> { ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ct: ct);
        return recent is null
            ? null
            : new PersonalActivityRecentDto([.. recent.Tiles.Select(PersonalActivityArtworkPath.ForBrowser)]);
    }

    public async Task<PersonalActivityOutcome<PersonalActivityPageDto>> GetPersonalActivityAsync(PersonalActivityRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await GetActivityOutcomeAsync<PersonalActivityPageDto>(
            "GET /api/v1/activity", BuildEndpointPath("/api/v1/activity", request.ToListQuery()), ct);
        if (!outcome.IsOk)
        {
            return outcome;
        }

        var page = outcome.Value!;
        return PersonalActivityOutcome<PersonalActivityPageDto>.Ok(
            page with { Items = [.. page.Items.Select(PersonalActivityArtworkPath.ForBrowser)] });
    }

    public Task<PersonalActivityOutcome<PersonalActivityPeriodsDto>> GetPersonalActivityPeriodsAsync(PersonalActivityRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetActivityOutcomeAsync<PersonalActivityPeriodsDto>(
            "GET /api/v1/activity/periods", BuildEndpointPath("/api/v1/activity/periods", request.ToFilterQuery()), ct);
    }

    public Task<PersonalActivitySummaryDto?> GetPersonalActivitySummaryAsync(CancellationToken ct = default) =>
        GetAsync<PersonalActivitySummaryDto>("GET /api/v1/activity/summary", "/api/v1/activity/summary", ct: ct);

    public Task<PersonalActivityOutcome<PersonalActivityDeletePreviewDto>> PreviewPersonalActivityDeletionAsync(string scope, DateTimeOffset before, CancellationToken ct = default) =>
        GetActivityOutcomeAsync<PersonalActivityDeletePreviewDto>(
            "GET /api/v1/activity/delete-preview", BuildEndpointPath("/api/v1/activity/delete-preview", DeletionQuery(scope, before)), ct);

    public Task<PersonalActivityOutcome<PersonalActivityDeleteResultDto>> DeletePersonalActivityAsync(string scope, DateTimeOffset before, CancellationToken ct = default) =>
        SendActivityOutcomeAsync<PersonalActivityDeleteResultDto>(
            "DELETE /api/v1/activity", () => new HttpRequestMessage(HttpMethod.Delete, BuildEndpointPath("/api/v1/activity", DeletionQuery(scope, before))), ct);

    private static Dictionary<string, string?> DeletionQuery(string scope, DateTimeOffset before) => new()
    {
        ["before"] = before.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["scope"] = string.Equals(scope, PersonalActivityRequest.MeScope, StringComparison.OrdinalIgnoreCase) ? null : scope,
    };

    private Task<PersonalActivityOutcome<T>> GetActivityOutcomeAsync<T>(string endpointLabel, string path, CancellationToken ct) =>
        SendActivityOutcomeAsync<T>(endpointLabel, () => new HttpRequestMessage(HttpMethod.Get, path), ct);

    // The Activity page has to tell a refusal that a PIN lifts apart from any other refusal, so these answers keep the status
    // instead of collapsing every failure to null. A refusal is an expected answer, not a failure of the Engine.
    private async Task<PersonalActivityOutcome<T>> SendActivityOutcomeAsync<T>(string endpointLabel, Func<HttpRequestMessage> build, CancellationToken ct)
    {
        try
        {
            using var message = build();
            using var response = await _http.SendAsync(message, ct);
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
                ClearFailure(endpointLabel);
                return value is null
                    ? PersonalActivityOutcome<T>.Failed(PersonalActivityOutcomeStatus.Unavailable)
                    : PersonalActivityOutcome<T>.Ok(value);
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.Forbidden:
                    var (code, detail) = await ReadProblemAsync(response, ct);
                    return PersonalActivityOutcome<T>.Failed(
                        string.Equals(code, ActivityUnlockRequiredCode, StringComparison.Ordinal)
                            ? PersonalActivityOutcomeStatus.UnlockRequired
                            : PersonalActivityOutcomeStatus.Forbidden,
                        detail);
                case HttpStatusCode.NotFound:
                    return PersonalActivityOutcome<T>.Failed(PersonalActivityOutcomeStatus.NotFound, (await ReadProblemAsync(response, ct)).Detail);
                case HttpStatusCode.BadRequest:
                    return PersonalActivityOutcome<T>.Failed(PersonalActivityOutcomeStatus.Invalid, (await ReadProblemAsync(response, ct)).Detail);
                default:
                    await RecordHttpFailureAsync(endpointLabel, response, ct);
                    return PersonalActivityOutcome<T>.Failed(PersonalActivityOutcomeStatus.Unavailable);
            }
        }
        catch (OperationCanceledException)
        {
            return PersonalActivityOutcome<T>.Failed(PersonalActivityOutcomeStatus.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            _logger.LogDebug(ex, "{Endpoint} failed", endpointLabel);
            RecordExceptionFailure(endpointLabel, ex);
            return PersonalActivityOutcome<T>.Failed(PersonalActivityOutcomeStatus.Unavailable);
        }
    }

    /// <summary>The Engine's machine-readable code for a refusal that entering the administrator PIN lifts.</summary>
    internal const string ActivityUnlockRequiredCode = "admin_unlock_required";

    private static async Task<(string? Code, string? Detail)> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            return (Text("code"), Text("detail"));
        }
        catch (JsonException)
        {
            // A refusal without a readable body is still a refusal; the status alone decides what the page shows.
            return (null, null);
        }
    }
}
