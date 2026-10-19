using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Security;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

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

        group.MapPut("/{applicationId:guid}", UpdateApplicationAsync)
            .WithName("UpdateApplication")
            .WithSummary("Update Application identity, type, enabled state, or administrator capability. Switching an Application back on while it holds live keys, or giving its keys more permissions, needs its approval hash (409 approval_required otherwise).")
            .Produces<ApplicationResponse>()
            .Produces<ApprovalRequiredResponse>(StatusCodes.Status409Conflict)
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

        group.MapPut("/{applicationId:guid}/permissions", SetPermissionsAsync)
            .WithName("SetApplicationPermissions")
            .WithSummary("Replace an Application's permission grants. Adding permissions needs the approval hash (409 approval_required otherwise); removing them needs nothing.")
            .Produces<ApplicationResponse>()
            .Produces<ApprovalRequiredResponse>(StatusCodes.Status409Conflict)
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

        group.MapPost("/{applicationId:guid}/credentials", IssueCredentialAsync)
            .RequireSecuredAccount()
            .WithName("IssueApplicationCredential")
            .WithSummary("Issue a one-time plaintext credential for an enabled Application. Every key needs its approval hash (409 approval_required otherwise).")
            .Produces<ApplicationCredentialIssuedResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<ApprovalRequiredResponse>(StatusCodes.Status409Conflict)
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

    /// <summary>
    /// Issues a key. Every key is new, so every key needs the person to have seen what it can do: the body must carry the
    /// current statement's hash. The approval is recorded under the new credential's id once it exists; if the record cannot
    /// be written the key is revoked again, so no key exists without a record.
    /// </summary>
    internal static async Task<IResult> IssueCredentialAsync(
        Guid applicationId,
        CreateApplicationCredentialRequest request,
        HttpContext context,
        IRequestAuthorityResolver authorityResolver,
        ApplicationAdministrationService service,
        [FromServices] ApprovalService approvals,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        try
        {
            var authority = await authorityResolver.ResolveAsync(context, ct).ConfigureAwait(false);
            // Reading the application also proves the caller may manage applications, and that it exists.
            var application = await service.GetApplicationAsync(authority, applicationId, ct).ConfigureAwait(false);
            // A disabled application is refused by the issue call itself with its own conflict, so it is not asked about.
            var preview = application.IsEnabled ? approvals.PreviewCredential(application, request.ExpiresAt) : null;
            if (preview is not null && !ApprovalService.HashMatches(request.ApprovalHash, preview.StatementHash))
            {
                return ApiErrors.ApprovalRequired([preview]);
            }

            var issued = await service.IssueCredentialAsync(authority, applicationId, request, ct).ConfigureAwait(false);
            if (preview is null)
            {
                // The integration was switched on between the read above and the issue, so nobody has seen what this key
                // can do. Look at it as it is now: without the matching hash the key is revoked again and the person is asked.
                var current = await service.GetApplicationAsync(authority, applicationId, ct).ConfigureAwait(false);
                preview = approvals.PreviewCredential(current, request.ExpiresAt);
                if (!ApprovalService.HashMatches(request.ApprovalHash, preview.StatementHash))
                {
                    await service.RevokeCredentialAsync(authority, applicationId, issued.Credential.Id, CancellationToken.None).ConfigureAwait(false);
                    return ApiErrors.ApprovalRequired([preview]);
                }
            }

            await RecordOrUndoAsync(
                approvals,
                authority,
                preview,
                ApprovalKinds.ApplicationCredential,
                issued.Credential.Id.ToString("D"),
                undo: () => service.RevokeCredentialAsync(authority, applicationId, issued.Credential.Id, CancellationToken.None),
                loggers.CreateLogger(typeof(ApplicationEndpoints).FullName!),
                ct).ConfigureAwait(false);
            return Results.Ok(issued);
        }
        catch (ApplicationAdministrationException exception)
        {
            return MapError(exception);
        }
    }

    /// <summary>
    /// Replaces an integration's permissions. Giving its existing keys more permissions needs the approval hash of the
    /// statement the person saw; taking permissions away needs nothing. The approval is recorded under the application's
    /// id once the change is saved; if the record cannot be written the change is put back, so no widening exists without
    /// a record.
    /// </summary>
    internal static async Task<IResult> SetPermissionsAsync(
        Guid applicationId,
        SetApplicationPermissionsRequest request,
        HttpContext context,
        IRequestAuthorityResolver authorityResolver,
        ApplicationAdministrationService service,
        [FromServices] ApprovalService approvals,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        try
        {
            var authority = await authorityResolver.ResolveAsync(context, ct).ConfigureAwait(false);
            // Reading the application also proves the caller may manage applications, and that it exists.
            var before = await service.GetApplicationAsync(authority, applicationId, ct).ConfigureAwait(false);
            var preview = approvals.PreviewApplicationPermissions(before, request.PermissionIds);
            if (preview is not null && !ApprovalService.HashMatches(request.ApprovalHash, preview.StatementHash))
            {
                return ApiErrors.ApprovalRequired([preview]);
            }

            var updated = await service.ReplacePermissionsAsync(authority, applicationId, request, ct).ConfigureAwait(false);
            if (preview is not null)
            {
                await RecordOrUndoAsync(
                    approvals,
                    authority,
                    preview,
                    ApprovalKinds.Application,
                    applicationId.ToString("D"),
                    undo: () => service.ReplacePermissionsAsync(authority, applicationId, new SetApplicationPermissionsRequest(before.PermissionIds), CancellationToken.None),
                    loggers.CreateLogger(typeof(ApplicationEndpoints).FullName!),
                    ct).ConfigureAwait(false);
            }

            return Results.Ok(updated);
        }
        catch (ApplicationAdministrationException exception)
        {
            return MapError(exception);
        }
    }

    /// <summary>
    /// Updates an integration. Switching it back on while it holds live keys, or giving its keys more permissions (for
    /// example by making it an administrator), needs the approval hash; disabling it or narrowing it needs nothing. Recorded
    /// under the application's id, and put back if the record cannot be written.
    /// </summary>
    internal static async Task<IResult> UpdateApplicationAsync(
        Guid applicationId,
        UpdateApplicationRequest request,
        HttpContext context,
        IRequestAuthorityResolver authorityResolver,
        ApplicationAdministrationService service,
        [FromServices] ApprovalService approvals,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        try
        {
            var authority = await authorityResolver.ResolveAsync(context, ct).ConfigureAwait(false);
            var before = await service.GetApplicationAsync(authority, applicationId, ct).ConfigureAwait(false);
            var preview = await approvals.PreviewApplicationUpdateAsync(before, request, ct).ConfigureAwait(false);
            if (preview is not null && !ApprovalService.HashMatches(request.ApprovalHash, preview.StatementHash))
            {
                return ApiErrors.ApprovalRequired([preview]);
            }

            var updated = await service.UpdateApplicationAsync(authority, applicationId, request, ct).ConfigureAwait(false);
            if (preview is not null)
            {
                var restore = new UpdateApplicationRequest(
                    before.Name, before.Description, before.ApplicationType, before.IsEnabled, before.IsAdministrator);
                await RecordOrUndoAsync(
                    approvals,
                    authority,
                    preview,
                    ApprovalKinds.Application,
                    applicationId.ToString("D"),
                    undo: () => service.UpdateApplicationAsync(authority, applicationId, restore, CancellationToken.None),
                    loggers.CreateLogger(typeof(ApplicationEndpoints).FullName!),
                    ct).ConfigureAwait(false);
            }

            return Results.Ok(updated);
        }
        catch (ApplicationAdministrationException exception)
        {
            return MapError(exception);
        }
    }

    /// <summary>
    /// Writes the approval record for a change that has just been made. If the record cannot be written, the change is
    /// undone so nothing stays switched on without a record; if the undo fails too, that is logged (the application and
    /// the change only, never a key) and the original failure is the one that reaches the caller.
    /// </summary>
    private static async Task RecordOrUndoAsync(
        ApprovalService approvals,
        RequestAuthority authority,
        ApprovalPreviewResponse preview,
        string itemKind,
        string itemId,
        Func<Task> undo,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var actor = await approvals.ResolveActorAsync(authority, ct).ConfigureAwait(false);
            await approvals.RecordAsync(preview, itemKind, itemId, actor, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                await undo().ConfigureAwait(false);
            }
            catch (Exception undoFailure)
            {
                logger.LogError(
                    undoFailure,
                    "The {ItemKind} change for {ItemId} could not be put back after its approval record failed to save",
                    itemKind, itemId);
            }

            throw;
        }
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
