using System.Globalization;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.LocalAssets.Filing;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.View;

/// <summary>The person asked for something their role does not allow, such as a Shared Library rule from a non-administrator.</summary>
public sealed class ViewDocumentRuleForbiddenException(string message) : Exception(message);

/// <summary>
/// A person's auto-filing rules: listing, checking, saving, ordering, trying out and applying them to existing
/// documents. A rule always belongs to the person who made it and covers either their own Personal Space or, for a
/// household administrator, the Shared Library of their household. Everything a rule points at (a tag, correspondent,
/// type or field) must be something the person can see, so a rule can never reveal or attach a name from someone
/// else's private documents.
/// </summary>
public sealed class ViewDocumentRuleService(
    IDocumentRuleRepository rules,
    IDocumentFilingRepository filing,
    IViewCatalogueRepository catalogue,
    IViewPersonalSpaceRepository spaces,
    IViewSharedLibraryRepository sharedLibraries,
    ViewStorageService storage,
    DocumentRulesQueue queue,
    ILogger<ViewDocumentRuleService> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _batchesRunning = new();

    public const int MaximumRules = 200;
    public const int MaximumNameLength = 120;
    public const int MaximumValueLength = 200;
    public const int MaximumParts = 20;
    public const int MaximumTestMatches = 10;
    private const int TestPageSize = 200;
    private const int TestMaximumChecked = 2000;

    private sealed record Scope(string Name, Guid? PersonalSpaceId, Guid LibraryId);

    public async Task<ViewDocumentRulesDto> ListAsync(RequestAuthority authority, ViewCatalogueViewer viewer, CancellationToken ct)
    {
        var profileId = RequireProfile(authority);
        var owned = await rules.ListOwnedAsync(profileId, ct).ConfigureAwait(false);
        var snapshot = catalogue.GetCatalogue(viewer, null, ct);
        var sources = new List<ViewDocumentRuleSourceDto>();
        var sourceNames = new Dictionary<Guid, string>();
        var personal = await spaces.GetByOwnerAsync(profileId, ct).ConfigureAwait(false);
        if (personal is not null)
        {
            foreach (var source in await rules.ListSourcesAsync(personal.LibraryId, ct).ConfigureAwait(false))
            {
                sources.Add(new ViewDocumentRuleSourceDto(source.Id, source.Name, ViewDocumentRuleScopes.Mine));
                sourceNames[source.Id] = source.Name;
            }
        }

        var canManageShared = CanManageShared(authority);
        if (canManageShared)
        {
            var shared = await sharedLibraries.FindForHouseholdAsync(authority.ActiveProfileHouseholdId!.Value, ct).ConfigureAwait(false);
            if (shared is not null)
            {
                foreach (var source in await rules.ListSourcesAsync(shared.LibraryId, ct).ConfigureAwait(false))
                {
                    sources.Add(new ViewDocumentRuleSourceDto(source.Id, source.Name, ViewDocumentRuleScopes.Shared));
                    sourceNames[source.Id] = source.Name;
                }
            }
        }

        return new ViewDocumentRulesDto(
            owned.Select(rule => ToDto(rule, snapshot, sourceNames)).ToList(), sources, canManageShared);
    }

    public async Task<ViewDocumentRuleDto> CreateAsync(
        RequestAuthority authority, ViewCatalogueViewer viewer, SaveViewDocumentRuleRequest request, CancellationToken ct)
    {
        var profileId = RequireProfile(authority);
        var scope = await ResolveScopeAsync(authority, request.Scope, ct).ConfigureAwait(false);
        if ((await rules.ListOwnedAsync(profileId, ct).ConfigureAwait(false)).Count >= MaximumRules)
        {
            throw new ArgumentException($"You can have at most {MaximumRules} rules.");
        }

        var rule = await BuildRuleAsync(Guid.Empty, profileId, scope, request, viewer, ct).ConfigureAwait(false);
        var created = await rules.CreateAsync(rule, ct).ConfigureAwait(false);
        return await MapAsync(created, viewer, scope, ct).ConfigureAwait(false);
    }

    public async Task<ViewDocumentRuleDto?> UpdateAsync(
        RequestAuthority authority, ViewCatalogueViewer viewer, Guid id, SaveViewDocumentRuleRequest request, CancellationToken ct)
    {
        var profileId = RequireProfile(authority);
        var existing = await rules.FindOwnedAsync(profileId, id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        // A rule keeps its scope: a personal rule cannot be turned into a household one, or the other way round.
        var scope = await ResolveScopeAsync(authority,
            existing.PersonalSpaceId is null ? ViewDocumentRuleScopes.Shared : ViewDocumentRuleScopes.Mine, ct).ConfigureAwait(false);
        var rule = await BuildRuleAsync(id, profileId, scope, request, viewer, ct).ConfigureAwait(false);
        var updated = await rules.ReplaceAsync(rule, ct).ConfigureAwait(false);
        return updated is null ? null : await MapAsync(updated, viewer, scope, ct).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(RequestAuthority authority, Guid id, CancellationToken ct) =>
        rules.DeleteOwnedAsync(RequireProfile(authority), id, ct);

    public Task<bool> ReorderAsync(RequestAuthority authority, IReadOnlyList<Guid> ruleIds, CancellationToken ct) =>
        rules.ReorderOwnedAsync(RequireProfile(authority), ruleIds, ct);

    /// <summary>Up to ten documents the described rule matches, drawn only from documents the person can open.</summary>
    public async Task<ViewDocumentRuleTestDto> TestAsync(
        RequestAuthority authority, ViewCatalogueViewer viewer, SaveViewDocumentRuleRequest request, CancellationToken ct)
    {
        var profileId = RequireProfile(authority);
        var scope = await ResolveScopeAsync(authority, request.Scope, ct).ConfigureAwait(false);
        var mode = (request.MatchMode ?? ViewDocumentRuleMatchModes.All).Trim().ToLowerInvariant();
        if (mode is not (ViewDocumentRuleMatchModes.All or ViewDocumentRuleMatchModes.Any))
        {
            throw new ArgumentException("Match mode must be all or any.");
        }

        // Only the conditions matter for a try-out; what the rule would do is not needed (or checked) here.
        var sources = (await rules.ListSourcesAsync(scope.LibraryId, ct).ConfigureAwait(false)).Select(source => source.Id).ToHashSet();
        var rule = new DocumentRule(Guid.Empty, profileId, scope.PersonalSpaceId, "Try-out", true, mode, 0, false,
            NormaliseConditions(request.Conditions ?? [], sources), []);
        var matches = new List<ViewDocumentRuleTestMatchDto>();
        var checkedCount = 0;
        var hasMore = false;
        for (var skip = 0; skip < TestMaximumChecked && !hasMore; skip += TestPageSize)
        {
            var page = await filing.GetSubjectsPageAsync(viewer, scope.LibraryId, skip, TestPageSize, ct).ConfigureAwait(false);
            foreach (var subject in page)
            {
                checkedCount++;
                if (!DocumentRuleEngine.Matches(rule, subject))
                {
                    continue;
                }

                if (matches.Count == MaximumTestMatches)
                {
                    hasMore = true;
                    break;
                }

                matches.Add(new ViewDocumentRuleTestMatchDto(
                    subject.ItemId, string.IsNullOrWhiteSpace(subject.Title) ? subject.FileName : subject.Title));
            }

            if (page.Count < TestPageSize)
            {
                break;
            }
        }

        return new ViewDocumentRuleTestDto(matches, checkedCount, hasMore);
    }

    /// <summary>Queues every document in the scopes the person has rules for, so their rules run on them again.</summary>
    public async Task<int> ApplyToExistingAsync(RequestAuthority authority, CancellationToken ct)
    {
        var profileId = RequireProfile(authority);
        var owned = await rules.ListOwnedAsync(profileId, ct).ConfigureAwait(false);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var queued = 0;
        if (owned.Any(rule => rule.PersonalSpaceId is not null && rule.Enabled))
        {
            var scope = await ResolveScopeAsync(authority, ViewDocumentRuleScopes.Mine, ct).ConfigureAwait(false);
            queued += await StartBatchAsync(scope.LibraryId, stamp, ct).ConfigureAwait(false);
        }

        if (owned.Any(rule => rule.PersonalSpaceId is null && rule.Enabled) && CanManageShared(authority))
        {
            var scope = await ResolveScopeAsync(authority, ViewDocumentRuleScopes.Shared, ct).ConfigureAwait(false);
            queued += await StartBatchAsync(scope.LibraryId, stamp, ct).ConfigureAwait(false);
        }

        return queued;
    }

    /// <summary>
    /// Queues a whole library in the background, so a click never waits on thousands of writes, and never lets a second
    /// click queue the same library while the first is still going. Returns how many documents will be queued.
    /// </summary>
    private async Task<int> StartBatchAsync(Guid libraryId, string stamp, CancellationToken ct)
    {
        var total = await filing.CountDocumentsAsync(libraryId, ct).ConfigureAwait(false);
        if (total > 0 && _batchesRunning.TryAdd(libraryId, true))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await queue.QueueLibraryAsync(libraryId, stamp, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A background batch has no caller to tell; log it so the person can press the button again.
                    logger.LogError(ex, "Queueing auto-filing rules for existing documents stopped early.");
                }
                finally
                {
                    _batchesRunning.TryRemove(libraryId, out _);
                }
            }, CancellationToken.None);
        }

        return total;
    }

    private static Guid RequireProfile(RequestAuthority authority) =>
        authority.ActiveProfileId is { } id && id != Guid.Empty
            ? id
            : throw new ViewDocumentRuleForbiddenException("Sign in as a person to use auto-filing rules.");

    private static bool CanManageShared(RequestAuthority authority) =>
        authority.IsEffectiveHouseholdAdministrator && authority.ActiveProfileHouseholdId is { } household && household != Guid.Empty;

    private async Task<Scope> ResolveScopeAsync(RequestAuthority authority, string? scope, CancellationToken ct)
    {
        var profileId = RequireProfile(authority);
        if (string.Equals(scope, ViewDocumentRuleScopes.Shared, StringComparison.OrdinalIgnoreCase))
        {
            if (!CanManageShared(authority))
            {
                throw new ViewDocumentRuleForbiddenException("Only a household administrator can make rules for the Shared Library.");
            }

            var shared = await sharedLibraries.EnsureForHouseholdAsync(authority.ActiveProfileHouseholdId!.Value, ct).ConfigureAwait(false);
            return new Scope(ViewDocumentRuleScopes.Shared, null, shared.LibraryId);
        }

        if (!string.IsNullOrWhiteSpace(scope) && !string.Equals(scope, ViewDocumentRuleScopes.Mine, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A rule covers either your own documents or the Shared Library.");
        }

        var space = await spaces.GetByOwnerAsync(profileId, ct).ConfigureAwait(false)
            ?? await storage.EnsurePersonalSpaceAsync(profileId, ct).ConfigureAwait(false);
        return new Scope(ViewDocumentRuleScopes.Mine, space.Id, space.LibraryId);
    }

    private async Task<DocumentRule> BuildRuleAsync(
        Guid id, Guid profileId, Scope scope, SaveViewDocumentRuleRequest request, ViewCatalogueViewer viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > MaximumNameLength || name.Any(char.IsControl))
        {
            throw new ArgumentException($"A rule needs a name of up to {MaximumNameLength} characters.");
        }

        var mode = (request.MatchMode ?? ViewDocumentRuleMatchModes.All).Trim().ToLowerInvariant();
        if (mode is not (ViewDocumentRuleMatchModes.All or ViewDocumentRuleMatchModes.Any))
        {
            throw new ArgumentException("Match mode must be all or any.");
        }

        var sources = (await rules.ListSourcesAsync(scope.LibraryId, ct).ConfigureAwait(false)).Select(source => source.Id).ToHashSet();
        var conditions = NormaliseConditions(request.Conditions ?? [], sources);
        var actions = NormaliseActions(request.Actions ?? [], viewer);
        if (actions.Count == 0 && !request.FilesAutomatically)
        {
            throw new ArgumentException("A rule needs something to do: add a tag, set a detail, or file the document.");
        }

        return new DocumentRule(id, profileId, scope.PersonalSpaceId, name, request.Enabled, mode, 0,
            request.FilesAutomatically, conditions, actions);
    }

    private static List<DocumentRuleCondition> NormaliseConditions(
        IReadOnlyList<ViewDocumentRuleConditionDto> conditions, HashSet<Guid> sourceIds)
    {
        if (conditions.Count == 0 || conditions.Count > MaximumParts)
        {
            throw new ArgumentException($"A rule needs between 1 and {MaximumParts} conditions.");
        }

        var result = new List<DocumentRuleCondition>(conditions.Count);
        foreach (var condition in conditions)
        {
            var kind = (condition.Kind ?? string.Empty).Trim().ToLowerInvariant();
            var value = (condition.Value ?? string.Empty).Trim();
            if (!ViewDocumentRuleConditionKinds.All.Contains(kind))
            {
                throw new ArgumentException($"A condition must be one of: {string.Join(", ", ViewDocumentRuleConditionKinds.All)}.");
            }

            if (value.Length == 0 || value.Length > MaximumValueLength || value.Any(char.IsControl))
            {
                throw new ArgumentException($"Each condition needs a value of up to {MaximumValueLength} characters.");
            }

            switch (kind)
            {
                case ViewDocumentRuleConditionKinds.Source:
                    if (!Guid.TryParse(value, out var sourceId) || !sourceIds.Contains(sourceId))
                    {
                        throw new ArgumentException("That source is not one of your sources.");
                    }

                    value = sourceId.ToString("D");
                    break;
                case ViewDocumentRuleConditionKinds.FileKind:
                    value = value.ToLowerInvariant();
                    if (!ViewDocumentFileKinds.All.Contains(value))
                    {
                        throw new ArgumentException($"A file kind must be one of: {string.Join(", ", ViewDocumentFileKinds.All)}.");
                    }

                    break;
            }

            result.Add(new DocumentRuleCondition(kind, value));
        }

        return result;
    }

    private List<DocumentRuleAction> NormaliseActions(IReadOnlyList<ViewDocumentRuleActionDto> actions, ViewCatalogueViewer viewer)
    {
        if (actions.Count > MaximumParts)
        {
            throw new ArgumentException($"A rule can have at most {MaximumParts} actions.");
        }

        var result = new List<DocumentRuleAction>(actions.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            var kind = (action.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (!ViewDocumentRuleActionKinds.All.Contains(kind))
            {
                throw new ArgumentException($"An action must be one of: {string.Join(", ", ViewDocumentRuleActionKinds.All)}.");
            }

            var target = action.RefId is { } refId && refId != Guid.Empty
                ? refId : throw new ArgumentException("Each action needs to point at a tag, correspondent, type or field.");
            var entryKind = kind switch
            {
                ViewDocumentRuleActionKinds.AddTag => ViewCatalogueKind.Tag,
                ViewDocumentRuleActionKinds.SetCorrespondent => ViewCatalogueKind.Correspondent,
                ViewDocumentRuleActionKinds.SetDocumentType => ViewCatalogueKind.DocumentType,
                _ => ViewCatalogueKind.Field,
            };

            // Guessing the id of something from another person's private documents is the same as an unknown id.
            var entry = catalogue.FindEntry(viewer, entryKind, target)
                ?? throw new ViewCatalogueAccessException("That tag, correspondent, type or field is not available.");
            if (kind is ViewDocumentRuleActionKinds.SetCorrespondent or ViewDocumentRuleActionKinds.SetDocumentType
                && !seen.Add(kind))
            {
                throw new ArgumentException("A rule can set one correspondent and one document type.");
            }

            if (!seen.Add($"{kind}:{target:N}"))
            {
                throw new ArgumentException("An action can only be given once in a rule.");
            }

            string? text = null;
            if (kind == ViewDocumentRuleActionKinds.SetField)
            {
                text = (action.TextValue ?? string.Empty).Trim();
                if (text.Length > MaximumValueLength)
                {
                    throw new ArgumentException($"A field value cannot be longer than {MaximumValueLength} characters.");
                }

                var write = DocumentRuleFieldValues.TryCreateWrite(target, entry.FieldKind ?? ViewDocumentFieldKinds.Text, text, out var error)
                    ?? throw new ArgumentException(error ?? "That value does not fit the field.");
                if (entry.FieldKind == ViewDocumentFieldKinds.Choice)
                {
                    text = entry.Choices.FirstOrDefault(choice => string.Equals(choice, write.Text, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"'{text}' is not one of the choices for this field.");
                }
            }

            result.Add(new DocumentRuleAction(kind, target, text));
        }

        return result;
    }

    private async Task<ViewDocumentRuleDto> MapAsync(DocumentRule rule, ViewCatalogueViewer viewer, Scope scope, CancellationToken ct)
    {
        var snapshot = catalogue.GetCatalogue(viewer, null, ct);
        var sources = (await rules.ListSourcesAsync(scope.LibraryId, ct).ConfigureAwait(false)).ToDictionary(source => source.Id, source => source.Name);
        return ToDto(rule, snapshot, sources);
    }

    private static ViewDocumentRuleDto ToDto(DocumentRule rule, ViewCatalogueSnapshot snapshot, IReadOnlyDictionary<Guid, string> sourceNames)
    {
        var tags = snapshot.Tags.ToDictionary(entry => entry.Id);
        var correspondents = snapshot.Correspondents.ToDictionary(entry => entry.Id);
        var types = snapshot.DocumentTypes.ToDictionary(entry => entry.Id);
        var fields = snapshot.Fields.ToDictionary(entry => entry.Id);
        return new ViewDocumentRuleDto(
            rule.Id, rule.Name, rule.Enabled, rule.MatchMode, rule.SortOrder, rule.FilesAutomatically,
            rule.PersonalSpaceId is null ? ViewDocumentRuleScopes.Shared : ViewDocumentRuleScopes.Mine,
            rule.Conditions.Select(condition => new ViewDocumentRuleConditionDto(
                condition.Kind, condition.Value,
                condition.Kind == ViewDocumentRuleConditionKinds.Source
                    && Guid.TryParse(condition.Value, out var sourceId) && sourceNames.TryGetValue(sourceId, out var sourceName)
                    ? sourceName : condition.Value)).ToList(),
            rule.Actions.Select(action =>
            {
                var name = action.RefId is not { } id ? null : action.Kind switch
                {
                    ViewDocumentRuleActionKinds.AddTag => tags.GetValueOrDefault(id)?.Name,
                    ViewDocumentRuleActionKinds.SetCorrespondent => correspondents.GetValueOrDefault(id)?.Name,
                    ViewDocumentRuleActionKinds.SetDocumentType => types.GetValueOrDefault(id)?.Name,
                    _ => fields.GetValueOrDefault(id)?.Name,
                };
                return new ViewDocumentRuleActionDto(action.Kind, action.RefId, action.TextValue, name, name is null);
            }).ToList());
    }
}
