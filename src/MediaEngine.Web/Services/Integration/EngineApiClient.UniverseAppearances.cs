using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Universe;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // -- Universe "Also appears in" ---------------------------------------------

    /// <summary>Null on a real failure (logged); an empty result when the Engine says there is nothing visible (404).</summary>
    public async Task<EntityAppearancesDto?> GetEntityAppearancesAsync(
        string universeQid, string? entityQid, Guid? personId, CancellationToken ct = default, Guid? reachedWorkId = null)
    {
        // The Engine takes exactly one of the two; asking for neither or both is a mistake here, not a request.
        if ((string.IsNullOrWhiteSpace(entityQid)) == (personId is null))
        {
            _logger.LogWarning("GET /universe/{Qid}/also-appears-in needs exactly one of entity or person", universeQid);
            return null;
        }

        try
        {
            var subject = string.IsNullOrWhiteSpace(entityQid)
                ? $"person={personId!.Value:D}"
                : $"entity={Uri.EscapeDataString(entityQid.Trim())}";
            // The companion asks as someone standing at a Work; the Engine counts that place as reached.
            var reached = reachedWorkId is { } workId && workId != Guid.Empty ? $"&reachedWorkId={workId:D}" : string.Empty;
            using var response = await _http.GetAsync(
                $"universe/{Uri.EscapeDataString(universeQid)}/also-appears-in?{subject}{reached}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The Engine answers 404 for a subject the profile cannot see anything of (and for an unknown one). That is
                // "nothing else in your Library", not a failure, so it comes back as an empty result.
                return new EntityAppearancesDto
                {
                    UniverseQid = universeQid,
                    SubjectKind = personId is null ? EntityAppearanceSubjectKinds.Entity : EntityAppearanceSubjectKinds.Person,
                    SubjectQid = personId is null ? entityQid!.Trim() : null,
                    PersonId = personId,
                };
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<EntityAppearancesDto>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/also-appears-in failed", universeQid);
            LastError = ex.Message;
            return null;
        }
    }
}
