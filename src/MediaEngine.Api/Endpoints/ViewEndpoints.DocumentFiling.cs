using MediaEngine.Api.Http;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Documents Inbox and auto-filing rules: how many documents are waiting, filing one from the Inbox, and a
/// person's own rules. Every route here is closed to a profile that may not use Documents, exactly like the document
/// text route. Rules and the Inbox only ever touch the libraries the resolver approved for the request.
/// </summary>
public static partial class ViewEndpoints
{
    private const string SharedInboxFiledByAdministrator = "Filed by a household administrator.";

    private static void MapDocumentFiling(RouteGroupBuilder group)
    {
        group.MapGet("/documents/inbox-summary", async (string? scope, Guid? scopeProfileId,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, IDocumentFilingRepository filing, CancellationToken ct) =>
        {
            var (access, refusal) = await ResolveCatalogueAccessAsync(
                scope, scopeProfileId, true, identity, preferences, resolver, authorization, ct);
            return access is null
                ? refusal!
                : Results.Ok(new ViewDocumentInboxSummaryDto(filing.CountInbox(access.Viewer, ct)));
        }).WithName("GetViewDocumentInboxSummary").Produces<ViewDocumentInboxSummaryDto>();

        group.MapPost("/items/{id:guid}/inbox/file", async (Guid id, [FromBody] FileViewDocumentFromInboxRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, IDocumentFilingRepository filing,
            ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var decision = await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
            if (!decision.IsAllowed)
            {
                // Not the person's own document: a Shared Library document in their household's Inbox can still be
                // filed by a household or server administrator. Anyone else is told who files it.
                var authority = await identity.ResolveAuthorityAsync(ct);
                var shared = decision.Outcome == ViewAccessOutcome.Unauthenticated
                    ? decision
                    : await authorization.AuthorizeAsync(authority,
                        new ViewResourceRequest(ViewScopeRequest.Shared, ViewResourceKind.Asset, id), ct);
                if (!shared.IsAllowed)
                {
                    return Access(decision.Outcome);
                }

                if (!ViewSharedInboxAccess.CanFile(authority))
                {
                    return ApiErrors.Forbidden(SharedInboxFiledByAdministrator);
                }
            }

            try
            {
                var (access, refusal) = await ResolveCatalogueAccessAsync(
                    null, null, true, identity, preferences, resolver, authorization, ct);
                if (access is null)
                {
                    return refusal!;
                }

                var write = new FileInboxWrite(
                    request.AcceptSuggestions, request.DocumentDate, request.Correspondent, request.DocumentType, request.Tags,
                    request.Suggestions);
                if (!await filing.FileFromInboxAsync(access.Viewer, id, write, ct))
                {
                    return Missing();
                }

                return assets.Find(id, ct) is { DocumentInfo: { } info } ? Results.Ok(info) : Missing();
            }
            catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
                or ArgumentException)
            {
                return CatalogueFailure(exception);
            }
        }).WithName("FileViewDocumentFromInbox").Produces<LocalAssetDocumentInfoDto>();

        group.MapGet("/document-rules", async (
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, ViewDocumentRuleService service, CancellationToken ct) =>
            await RunRuleAsync(identity, preferences, resolver, authorization,
                async (access, token) => Results.Ok(await service.ListAsync(access.Authority, access.Viewer, token)), ct))
            .WithName("GetViewDocumentRules").Produces<ViewDocumentRulesDto>();

        group.MapPost("/document-rules", async ([FromBody] SaveViewDocumentRuleRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, ViewDocumentRuleService service, CancellationToken ct) =>
            await RunRuleAsync(identity, preferences, resolver, authorization,
                async (access, token) => Results.Ok(await service.CreateAsync(access.Authority, access.Viewer, request, token)), ct))
            .WithName("CreateViewDocumentRule").Produces<ViewDocumentRuleDto>();

        group.MapPut("/document-rules/order", async ([FromBody] ReorderViewDocumentRulesRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, ViewDocumentRuleService service, CancellationToken ct) =>
            await RunRuleAsync(identity, preferences, resolver, authorization,
                async (access, token) => await service.ReorderAsync(access.Authority, request.RuleIds ?? [], token)
                    ? Results.NoContent() : Missing(), ct))
            .WithName("ReorderViewDocumentRules").Produces(StatusCodes.Status204NoContent);

        group.MapPut("/document-rules/{id:guid}", async (Guid id, [FromBody] SaveViewDocumentRuleRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, ViewDocumentRuleService service, CancellationToken ct) =>
            await RunRuleAsync(identity, preferences, resolver, authorization,
                async (access, token) => await service.UpdateAsync(access.Authority, access.Viewer, id, request, token)
                    is { } updated ? Results.Ok(updated) : Missing(), ct))
            .WithName("UpdateViewDocumentRule").Produces<ViewDocumentRuleDto>();

        group.MapDelete("/document-rules/{id:guid}", async (Guid id,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, ViewDocumentRuleService service, CancellationToken ct) =>
            await RunRuleAsync(identity, preferences, resolver, authorization,
                async (access, token) => await service.DeleteAsync(access.Authority, id, token)
                    ? Results.NoContent() : Missing(), ct))
            .WithName("DeleteViewDocumentRule").Produces(StatusCodes.Status204NoContent);

        group.MapPost("/document-rules/test", async ([FromBody] SaveViewDocumentRuleRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, ViewDocumentRuleService service, CancellationToken ct) =>
            await RunRuleAsync(identity, preferences, resolver, authorization,
                async (access, token) => Results.Ok(await service.TestAsync(access.Authority, access.Viewer, request, token)), ct))
            .WithName("TestViewDocumentRule").Produces<ViewDocumentRuleTestDto>();

        group.MapPost("/document-rules/apply", async (
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, ViewDocumentRuleService service, CancellationToken ct) =>
            await RunRuleAsync(identity, preferences, resolver, authorization,
                async (access, token) => Results.Ok(
                    new ViewDocumentRulesApplyResultDto(await service.ApplyToExistingAsync(access.Authority, token))), ct))
            .WithName("ApplyViewDocumentRules").Produces<ViewDocumentRulesApplyResultDto>();
    }

    /// <summary>
    /// Opens a rules route: refuses a profile that may not use Documents, then runs the work. A rule can only name
    /// things the person can see, so a failure never says why a name was refused.
    /// </summary>
    private static async Task<IResult> RunRuleAsync(
        IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
        IViewResourceAuthorizationService authorization,
        Func<CatalogueAccess, CancellationToken, Task<IResult>> work, CancellationToken ct)
    {
        try
        {
            var (access, refusal) = await ResolveCatalogueAccessAsync(
                null, null, true, identity, preferences, resolver, authorization, ct);
            return access is null ? refusal! : await work(access, ct);
        }
        catch (ViewDocumentRuleForbiddenException exception)
        {
            return ApiErrors.Forbidden(exception.Message);
        }
        catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
            or ArgumentException)
        {
            return CatalogueFailure(exception);
        }
    }
}
