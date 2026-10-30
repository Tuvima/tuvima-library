using System.Net.Http.Json;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Integration;

// Profile-held access and roles (users, profiles and households): who looks after a household.
public sealed partial class DashboardIdentityClient
{
    /// <summary>
    /// Sets a profile's Household administrator and Server administrator roles (both values are the wanted state). A refusal keeps the
    /// Engine's plain reason in <c>Message</c>.
    /// </summary>
    public Task<DashboardAccessMutationResult> SetProfileRolesResultAsync(Guid profileId, SetProfileRolesRequest request, CancellationToken ct = default) =>
        SendProfileMutationAsync(HttpMethod.Put, $"/access/profiles/{profileId:D}/roles", request, ct);

    // This action carries only ids, role flags and lane names, so every Engine refusal sentence (including "only a server
    // administrator can ...") is safe to show as it is.
    private async Task<DashboardAccessMutationResult> SendProfileMutationAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return DashboardAccessMutationResult.Success();
            }

            if (await ApprovalResponses.TryReadAsync(response, logger, ct).ConfigureAwait(false) is { } approval)
            {
                return DashboardAccessMutationResult.FailureResult(
                    DashboardAccessMutationFailure.ApprovalRequired, response.StatusCode, approval: approval);
            }

            var failure = await ClassifyFailureAsync(response, ct).ConfigureAwait(false);
            var (message, code) = await ReadPlainProblemAsync(response, failure, ct, readAnyEngineSentence: true).ConfigureAwait(false);
            return DashboardAccessMutationResult.FailureResult(failure, response.StatusCode, message: message, code: code);
        }
        catch (HttpRequestException)
        {
            return DashboardAccessMutationResult.FailureResult(DashboardAccessMutationFailure.Transient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DashboardAccessMutationResult.FailureResult(DashboardAccessMutationFailure.Transient);
        }
    }
}
