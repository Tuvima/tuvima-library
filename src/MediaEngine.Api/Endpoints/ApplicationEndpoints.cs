using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Endpoints;

public static class ApplicationEndpoints
{
    public static IEndpointRouteBuilder MapApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/access/applications")
            .WithTags("Applications")
            .RequireAdministratorOrApplication(ApplicationPermissionIds.IdentityApplicationsWrite);

        group.MapGet("/permissions", async (
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteAsync(context, authorityResolver, service.GetPermissionDefinitionsAsync, ct))
            .WithName("ListApplicationPermissions")
            .WithSummary("List registered Application permissions and their current availability.")
            .Produces<IReadOnlyList<ApplicationPermissionDefinitionDto>>();

        group.MapGet("/presets", async (
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteAsync(context, authorityResolver, service.GetPermissionPresetsAsync, ct))
            .WithName("ListApplicationPermissionPresets")
            .WithSummary("List editable initial permission selections.")
            .Produces<IReadOnlyList<ApplicationPermissionPresetDto>>();

        group.MapGet("/", async (
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteAsync(context, authorityResolver, service.GetApplicationsAsync, ct))
            .WithName("ListApplications")
            .WithSummary("List Applications and their credential lifecycle summaries.")
            .Produces<IReadOnlyList<ApplicationResponse>>();

        group.MapGet("/{applicationId:guid}", async (
            Guid applicationId,
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteAsync(
                context,
                authorityResolver,
                (authority, token) => service.GetApplicationAsync(authority, applicationId, token),
                ct))
            .WithName("GetApplication")
            .WithSummary("Get one Application and its live permission grants.")
            .Produces<ApplicationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
            CreateApplicationRequest request,
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteAsync(
                context,
                authorityResolver,
                (authority, token) => service.CreateApplicationAsync(authority, request, token),
                ct))
            .RequireSecuredAccount()
            .WithName("CreateApplication")
            .WithSummary("Create an Application with validated permissions.")
            .Produces<ApplicationResponse>()
            .ProducesValidationProblem();

        group.MapPut("/{applicationId:guid}", async (
            Guid applicationId,
            UpdateApplicationRequest request,
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteAsync(
                context,
                authorityResolver,
                (authority, token) => service.UpdateApplicationAsync(authority, applicationId, request, token),
                ct))
            .WithName("UpdateApplication")
            .WithSummary("Update Application identity, type, enabled state, or administrator capability.")
            .Produces<ApplicationResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{applicationId:guid}", async (
            Guid applicationId,
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteNoContentAsync(
                context,
                authorityResolver,
                (authority, token) => service.DeleteApplicationAsync(authority, applicationId, token),
                ct))
            .WithName("DeleteApplication")
            .WithSummary("Delete an Application and revoke its credentials.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{applicationId:guid}/permissions", async (
            Guid applicationId,
            SetApplicationPermissionsRequest request,
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteAsync(
                context,
                authorityResolver,
                (authority, token) => service.ReplacePermissionsAsync(authority, applicationId, request, token),
                ct))
            .WithName("SetApplicationPermissions")
            .WithSummary("Replace an Application's permission grants.")
            .Produces<ApplicationResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{applicationId:guid}/client-bindings", async (
            Guid applicationId,
            SetApplicationClientBindingsRequest request,
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteAsync(
                context,
                authorityResolver,
                (authority, token) => service.ReplaceClientBindingsAsync(
                    authority,
                    applicationId,
                    request,
                    token),
                ct))
            .WithName("SetApplicationClientBindings")
            .WithSummary("Replace the registered native client identifiers for a UserClient Application.")
            .Produces<ApplicationResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{applicationId:guid}/credentials", async (
            Guid applicationId,
            CreateApplicationCredentialRequest request,
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteAsync(
                context,
                authorityResolver,
                (authority, token) => service.IssueCredentialAsync(authority, applicationId, request, token),
                ct))
            .RequireSecuredAccount()
            .WithName("IssueApplicationCredential")
            .WithSummary("Issue a one-time plaintext credential for an enabled Application.")
            .Produces<ApplicationCredentialIssuedResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{applicationId:guid}/credentials/{credentialId:guid}/rotate", async (
            Guid applicationId,
            Guid credentialId,
            RotateApplicationCredentialRequest request,
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteAsync(
                context,
                authorityResolver,
                (authority, token) => service.RotateCredentialAsync(
                    authority,
                    applicationId,
                    credentialId,
                    request,
                    token),
                ct))
            .RequireSecuredAccount()
            .WithName("RotateApplicationCredential")
            .WithSummary("Atomically revoke one credential and issue its replacement.")
            .Produces<ApplicationCredentialIssuedResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{applicationId:guid}/credentials/{credentialId:guid}", async (
            Guid applicationId,
            Guid credentialId,
            HttpContext context,
            IRequestAuthorityResolver authorityResolver,
            ApplicationAdministrationService service,
            CancellationToken ct) =>
            await ExecuteNoContentAsync(
                context,
                authorityResolver,
                (authority, token) => service.RevokeCredentialAsync(authority, applicationId, credentialId, token),
                ct))
            .WithName("RevokeApplicationCredential")
            .WithSummary("Revoke one Application credential without changing its permissions.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ExecuteAsync<T>(
        HttpContext context,
        IRequestAuthorityResolver authorityResolver,
        Func<RequestAuthority, CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        try
        {
            var authority = await authorityResolver.ResolveAsync(context, ct).ConfigureAwait(false);
            return Results.Ok(await operation(authority, ct).ConfigureAwait(false));
        }
        catch (ApplicationAdministrationException exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> ExecuteNoContentAsync(
        HttpContext context,
        IRequestAuthorityResolver authorityResolver,
        Func<RequestAuthority, CancellationToken, Task> operation,
        CancellationToken ct)
    {
        try
        {
            var authority = await authorityResolver.ResolveAsync(context, ct).ConfigureAwait(false);
            await operation(authority, ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (ApplicationAdministrationException exception)
        {
            return MapError(exception);
        }
    }

    internal static IResult MapError(ApplicationAdministrationException exception) =>
        exception.Error switch
        {
            ApplicationAdministrationError.Unauthorized => ApiErrors.Problem(
                StatusCodes.Status401Unauthorized,
                "Authentication required.",
                exception.Message),
            ApplicationAdministrationError.Forbidden => ApiErrors.Forbidden(exception.Message),
            ApplicationAdministrationError.NotFound => ApiErrors.NotFound(exception.Message),
            ApplicationAdministrationError.Conflict => ApiErrors.Conflict(exception.Message),
            ApplicationAdministrationError.Validation => ApiErrors.BadRequest(exception.Message),
            _ => throw new ArgumentOutOfRangeException(nameof(exception), exception.Error, null),
        };
}
