using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Integration;

// Users (sign-ins) for the server administrator's Users and Sign-in page: /access/users. A user is only a way to sign in; what a
// person may see and do lives on their profile (users, profiles and households spec). Every refusal keeps the Engine's own
// sentence so the page can show it as it is.
public sealed partial class DashboardIdentityClient
{
    /// <summary>Every user with the profile it opens and that profile's household. Empty when the Engine can't be reached.</summary>
    public async Task<List<UserSummaryResponse>> GetManagedUsersAsync(CancellationToken ct = default) =>
        await GetAsync<List<UserSummaryResponse>>("/access/users", ct).ConfigureAwait(false) ?? [];

    /// <summary>
    /// Makes a user (server administrator only): a sign-in for an existing profile that has none, or for a new profile.
    /// The answer carries the invitation (link and code) when that was the chosen sign-in method.
    /// </summary>
    public Task<DashboardAccessMutationResult<CreateUserResponse>> CreateUserResultAsync(CreateUserRequest request, CancellationToken ct = default) =>
        SendUserMutationAsync<CreateUserRequest, CreateUserResponse>(HttpMethod.Post, "/access/users", request, ct);

    /// <summary>Changes the email a person signs in with. Their profile and everything in it stay as they were.</summary>
    public Task<DashboardAccessMutationResult<UserSummaryResponse>> ChangeUserEmailResultAsync(Guid userId, string email, CancellationToken ct = default) =>
        SendUserMutationAsync<ChangeAccountEmailRequest, UserSummaryResponse>(
            HttpMethod.Put, $"/access/users/{userId:D}/email", new ChangeAccountEmailRequest(email), ct);

    /// <summary>
    /// What deleting the user does, for the confirmation. Nothing changes. The answer says whether a household would go with it
    /// and, if the delete would be refused, why.
    /// </summary>
    public async Task<DashboardAccessMutationResult<UserDeletionPreviewResponse>> GetUserDeletionPreviewResultAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            using var response = await Client.GetAsync($"/access/users/{userId:D}/deletion-preview", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await ReadMutationFailureAsync<UserDeletionPreviewResponse>(response, ct, includeMessage: true, readAnyEngineSentence: true).ConfigureAwait(false);
            }

            var value = await response.Content.ReadFromJsonAsync<UserDeletionPreviewResponse>(cancellationToken: ct).ConfigureAwait(false);
            return value is null
                ? DashboardAccessMutationResult<UserDeletionPreviewResponse>.FailureResult(DashboardAccessMutationFailure.InvalidResponse, response.StatusCode)
                : DashboardAccessMutationResult<UserDeletionPreviewResponse>.Success(value);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return DashboardAccessMutationResult<UserDeletionPreviewResponse>.FailureResult(DashboardAccessMutationFailure.InvalidResponse);
        }
        catch (HttpRequestException)
        {
            return DashboardAccessMutationResult<UserDeletionPreviewResponse>.FailureResult(DashboardAccessMutationFailure.Transient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DashboardAccessMutationResult<UserDeletionPreviewResponse>.FailureResult(DashboardAccessMutationFailure.Transient);
        }
    }

    /// <summary>
    /// Deletes the sign-in only. When the preview said the household goes too, <paramref name="confirmedProfileIds"/> is the list
    /// the person was shown (the Engine refuses and deletes nothing if what would go has changed since); their photos and videos, and the
    /// household's shared ones, are deleted with them (never moved). Screens open on the sign-in, or on a profile that went with it, close.
    /// </summary>
    public async Task<DashboardAccessMutationResult> DeleteUserResultAsync(
        Guid userId, IReadOnlyCollection<Guid> confirmedProfileIds, CancellationToken ct = default)
    {
        var confirmed = string.Join(',', confirmedProfileIds.Select(id => id.ToString("D")));
        var path = $"/access/users/{userId:D}?profiles={confirmed}";
        var result = await SendUserMutationAsync(HttpMethod.Delete, path, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            openScreens?.CloseWhere(screen => screen.AccountId == userId
                || (screen.ProfileId is { } profileId && confirmedProfileIds.Contains(profileId)));
        }

        return result;
    }

    private async Task<DashboardAccessMutationResult<TResponse>> SendUserMutationAsync<TRequest, TResponse>(
        HttpMethod method, string path, TRequest body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await ReadMutationFailureAsync<TResponse>(response, ct, includeMessage: true, readAnyEngineSentence: true).ConfigureAwait(false);
            }

            var value = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: ct).ConfigureAwait(false);
            return value is null
                ? DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.InvalidResponse, response.StatusCode)
                : DashboardAccessMutationResult<TResponse>.Success(value);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.InvalidResponse);
        }
        catch (HttpRequestException)
        {
            return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.Transient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DashboardAccessMutationResult<TResponse>.FailureResult(DashboardAccessMutationFailure.Transient);
        }
    }

    private async Task<DashboardAccessMutationResult> SendUserMutationAsync(HttpMethod method, string path, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? DashboardAccessMutationResult.Success()
                : await ReadMutationFailureAsync(response, ct, includeMessage: true, readAnyEngineSentence: true).ConfigureAwait(false);
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
