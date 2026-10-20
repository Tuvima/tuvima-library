using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// The Documents bulk edit: one operation applied to up to 500 selected documents. Every document is checked on its
/// own (is it reachable, is it a document, may this person change it) and gets its own result, so one that is refused
/// or fails never stops or half-changes the others. Nothing here deletes a file: moving to the trash only marks the
/// document, and restoring only clears the mark.
/// </summary>
public sealed class ViewDocumentBulkService(
    IDocumentBulkRepository bulk,
    IDocumentFilingRepository filing,
    ILocalAssetRepository assets,
    IViewResourceAuthorizationService authorization,
    ILogger<ViewDocumentBulkService> logger)
{
    public const string SharedReason = "In the Shared Library: only filing rules can change it.";
    public const string SharedFilingReason = "Filed by a household administrator.";
    public const string NotYoursReason = "You can't change this document.";
    public const string NotAvailableReason = "This document is not available.";
    public const string ReadOnlySourceReason = "This document is in a read-only folder, so it can't be moved to Trash.";

    public async Task<ViewDocumentBulkResultDto> RunAsync(
        RequestAuthority authority, ViewCatalogueViewer viewer, ViewDocumentBulkRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var change = Validate(request, out var ids);
        var subjects = bulk.Describe(viewer, ids, ct).ToDictionary(subject => subject.ItemId);
        var results = new List<ViewDocumentBulkItemResultDto>(ids.Count);
        // Documents with a file in a linked (read-only) folder never go to Trash; one lookup covers the whole batch.
        var readOnly = request.Operation == ViewDocumentBulkOperations.MoveToTrash
            ? assets.GetReadOnlyItemIds(ids, ct)
            : new HashSet<Guid>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunOneAsync(authority, viewer, request.Operation, change, subjects[id], readOnly, ct).ConfigureAwait(false));
        }

        return new ViewDocumentBulkResultDto(
            request.Operation,
            results.Count,
            results.Count(result => result.Status == ViewDocumentBulkStatuses.Done),
            results.Count(result => result.Status == ViewDocumentBulkStatuses.Skipped),
            results.Count(result => result.Status == ViewDocumentBulkStatuses.Failed),
            results);
    }

    /// <summary>Checks the request shape and returns the change for the operations that go through the data store.</summary>
    internal static DocumentBulkChange? Validate(ViewDocumentBulkRequest request, out List<Guid> ids)
    {
        if (request.Operation is null || !ViewDocumentBulkOperations.All.Contains(request.Operation))
        {
            throw new ArgumentException($"The operation must be one of: {string.Join(", ", ViewDocumentBulkOperations.All)}.");
        }

        if (request.ItemIds is null || request.ItemIds.Count == 0)
        {
            throw new ArgumentException("Choose at least one document.");
        }

        if (request.ItemIds.Count > ViewDocumentBulkOperations.MaximumItems)
        {
            throw new ArgumentException($"A bulk edit can change at most {ViewDocumentBulkOperations.MaximumItems} documents at a time.");
        }

        if (request.ItemIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("Every document needs an id.");
        }

        ids = request.ItemIds.Distinct().ToList();
        var tagNames = (request.Tags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag)).ToList();
        var tagIds = (request.TagIds ?? []).Where(id => id != Guid.Empty).ToList();
        switch (request.Operation)
        {
            case ViewDocumentBulkOperations.AddTags:
                return tagNames.Count + tagIds.Count == 0
                    ? throw new ArgumentException("Choose at least one tag to add.")
                    : new DocumentBulkChange(DocumentBulkChangeKind.AddTags, tagNames, tagIds);
            case ViewDocumentBulkOperations.RemoveTags:
                return tagIds.Count == 0
                    ? throw new ArgumentException("Choose at least one tag to take off.")
                    : new DocumentBulkChange(DocumentBulkChangeKind.RemoveTags, TagIds: tagIds);
            case ViewDocumentBulkOperations.SetCorrespondent:
                return NamedChange(DocumentBulkChangeKind.SetCorrespondent, request.Correspondent, request.Clear, "a correspondent");
            case ViewDocumentBulkOperations.SetDocumentType:
                return NamedChange(DocumentBulkChangeKind.SetDocumentType, request.DocumentType, request.Clear, "a document type");
            case ViewDocumentBulkOperations.SetDocumentDate:
                return request.DocumentDate is null && !request.Clear
                    ? throw new ArgumentException("Choose a date, or ask to clear it.")
                    : new DocumentBulkChange(DocumentBulkChangeKind.SetDocumentDate, Date: request.Clear ? null : request.DocumentDate);
            default:
                return null;
        }
    }

    private static DocumentBulkChange NamedChange(DocumentBulkChangeKind kind, string? name, bool clear, string what) =>
        string.IsNullOrWhiteSpace(name) && !clear
            ? throw new ArgumentException($"Choose {what}, or ask to clear it.")
            : new DocumentBulkChange(kind, Name: clear ? null : name);

    private async Task<ViewDocumentBulkItemResultDto> RunOneAsync(
        RequestAuthority authority, ViewCatalogueViewer viewer, string operation, DocumentBulkChange? change,
        DocumentBulkSubject subject, IReadOnlySet<Guid> readOnly, CancellationToken ct)
    {
        var id = subject.ItemId;
        // An id outside the person's libraries gets the same answer as one that does not exist.
        if (!subject.Reachable)
        {
            return Skipped(id, NotAvailableReason);
        }

        if (!subject.IsDocument)
        {
            return Skipped(id, "Only documents can be organised this way.");
        }

        if (subject.IsShared)
        {
            // Shared Library documents are not edited in bulk. The one exception is Mark filed, which a household
            // or server administrator can use to empty the Shared Inbox.
            if (operation != ViewDocumentBulkOperations.MarkFiled)
            {
                return Skipped(id, SharedReason);
            }

            if (!ViewSharedInboxAccess.CanFile(authority))
            {
                return Skipped(id, SharedFilingReason);
            }
        }

        // A Shared Library document is not the administrator's own, so the ownership check applies to the rest only.
        if (!subject.IsShared)
        {
            var decision = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Asset, id, ViewResourceAction.Manage), ct)
                .ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return Skipped(id, NotYoursReason);
            }
        }

        try
        {
            switch (operation)
            {
                case ViewDocumentBulkOperations.MarkFiled:
                    if (!subject.IsInbox)
                    {
                        return Skipped(id, "Already filed.");
                    }

                    return await filing.FileFromInboxAsync(viewer, id, new FileInboxWrite(false, null, null, null, null), ct).ConfigureAwait(false)
                        ? Done(id) : Failed(id, "This document could not be filed.");
                case ViewDocumentBulkOperations.MoveToTrash:
                    if (subject.IsTrashed)
                    {
                        return Skipped(id, "Already in the trash.");
                    }

                    if (readOnly.Contains(id))
                    {
                        return new ViewDocumentBulkItemResultDto(
                            id, ViewDocumentBulkStatuses.Skipped, ReadOnlySourceReason, ViewDocumentBulkReasonCodes.ReadOnlySource);
                    }

                    return await assets.SetLifecycleStateAsync(id, LocalAssetLifecycleState.Trashed, ct).ConfigureAwait(false)
                        ? Done(id) : Failed(id, "This document could not be moved to the trash.");
                case ViewDocumentBulkOperations.Restore:
                    if (!subject.IsTrashed)
                    {
                        return Skipped(id, "Not in the trash.");
                    }

                    return await assets.SetLifecycleStateAsync(id, LocalAssetLifecycleState.Active, ct).ConfigureAwait(false)
                        ? Done(id) : Failed(id, "This document could not be restored.");
                default:
                    var result = await bulk.ApplyAsync(viewer, id, change!, ct).ConfigureAwait(false);
                    return result.Outcome switch
                    {
                        DocumentBulkOutcome.Done => Done(id),
                        DocumentBulkOutcome.Unchanged => Skipped(id, "Already like that."),
                        _ => Skipped(id, result.Reason ?? NotAvailableReason),
                    };
            }
        }
        catch (Exception exception) when (exception is ViewCatalogueAccessException or ViewCatalogueConflictException or ArgumentException)
        {
            // These carry a plain-language reason meant for the person (for example "That tag is not available.").
            return Failed(id, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One document going wrong must not stop the rest of the batch; it is reported and logged.
            logger.LogError(exception, "Bulk edit {Operation} failed for document {ItemId}", operation, id);
            return Failed(id, "Something went wrong with this document. It was left as it was.");
        }
    }

    private static ViewDocumentBulkItemResultDto Done(Guid id) => new(id, ViewDocumentBulkStatuses.Done);

    private static ViewDocumentBulkItemResultDto Skipped(Guid id, string reason) =>
        new(id, ViewDocumentBulkStatuses.Skipped, reason);

    private static ViewDocumentBulkItemResultDto Failed(Guid id, string reason) =>
        new(id, ViewDocumentBulkStatuses.Failed, reason);
}
