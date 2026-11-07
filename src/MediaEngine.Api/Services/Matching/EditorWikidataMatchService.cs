using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using Tuvima.Wikidata;
using Tuvima.Wikidata.Services;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// The Match tab's Wikidata side (universal editor v5, 3c "Wikidata"): search Wikidata, resolve a pasted link or item number, tell an item's own
/// record from the thing it belongs to, and show the alignment evidence. Every call to Wikidata goes through the Library's one reconciler (its own
/// rate limits and cache). The Engine decides the kind relation from the editor's kind of item and the entity's classes, using the lists in
/// <c>config/wikidata_match.json</c>; the Dashboard never does. A read only: nothing is written here.
/// </summary>
public sealed partial class EditorWikidataMatchService(
    ReconciliationService reconcile,
    EntityService entities,
    IEditorMatchKindResolver kinds,
    IMetadataEditorRepository launches,
    ICanonicalValueRepository canonicals,
    IExternalLookupGuard lookupGuard,
    WikidataMatchConfigurationProvider configuration,
    ILogger<EditorWikidataMatchService> logger)
{
    /// <summary>What the Engine knows about the item being edited, read once per call.</summary>
    private sealed record ItemFacts(string? Kind, string? Title, string? Year, string? Creator, string? Qid, IReadOnlyDictionary<string, string> Ids);

    /// <summary>One Wikidata item, read and described.</summary>
    private sealed record Described(EditorWikidataResultDto Result, WikidataEntityInfo Entity);

    /// <summary>Searches Wikidata by the words typed, or resolves what was pasted. Null when the item is not in the Library.</summary>
    public async Task<EditorWikidataSearchResultDto?> SearchAsync(Guid entityId, EditorWikidataSearchRequestDto request, CancellationToken ct = default)
    {
        var facts = await ReadItemAsync(entityId, ct).ConfigureAwait(false);
        if (facts is null)
        {
            return null;
        }

        if (await lookupGuard.BlocksEntityAsync(entityId, ct).ConfigureAwait(false))
        {
            return Empty(entityId, EditorWikidataMessageCodes.LookupsOff);
        }

        var rules = configuration.Current;
        var paste = request.Paste?.Trim();
        var query = request.Query?.Trim();
        try
        {
            if (!string.IsNullOrEmpty(paste))
            {
                if (ParseQid(paste) is not { } pasted)
                {
                    return Empty(entityId, EditorWikidataMessageCodes.NotAnItem);
                }

                var one = await DescribeAsync(facts, [pasted], rules, ct).ConfigureAwait(false);
                return one.Count == 0
                    ? Empty(entityId, EditorWikidataMessageCodes.ItemNotFound)
                    : new EditorWikidataSearchResultDto { EntityId = entityId, Results = [.. one.Select(item => item.Result)], FromPaste = true };
            }

            if (string.IsNullOrEmpty(query))
            {
                return Empty(entityId, EditorWikidataMessageCodes.EnterWords);
            }

            var found = await reconcile.ReconcileAsync(
                new ReconciliationRequest { Query = query, Limit = rules.SearchLimit }, ct).ConfigureAwait(false);
            var qids = found.Select(item => item.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Take(rules.SearchLimit).ToList();
            if (qids.Count == 0)
            {
                return Empty(entityId, EditorWikidataMessageCodes.NothingFound);
            }

            var described = await DescribeAsync(facts, qids, rules, ct).ConfigureAwait(false);
            return described.Count == 0
                ? Empty(entityId, EditorWikidataMessageCodes.NothingFound)
                : new EditorWikidataSearchResultDto { EntityId = entityId, Results = [.. described.Select(item => item.Result)] };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Wikidata not answering is an ordinary state of the world: the person sees "Wikidata did not answer", never the exception.
            logger.LogWarning(ex, "Wikidata did not answer the Match tab's search for item {EntityId}.", entityId);
            return Empty(entityId, EditorWikidataMessageCodes.ProviderSilent);
        }
    }

    /// <summary>
    /// The relation of one Wikidata item to the item being edited, read afresh from Wikidata. The edit session asks it before it saves a link, so a
    /// parent is refused whatever the Dashboard sent. Null when the item is not in the Library or Wikidata has no such item; throws when Wikidata does not answer.
    /// </summary>
    public async Task<EditorWikidataResultDto?> DescribeOneAsync(Guid entityId, string qid, CancellationToken ct = default)
    {
        var facts = await ReadItemAsync(entityId, ct).ConfigureAwait(false);
        if (facts is null)
        {
            return null;
        }

        var described = await DescribeAsync(facts, [qid], configuration.Current, ct).ConfigureAwait(false);
        return described.FirstOrDefault()?.Result;
    }

    /// <summary>What the item is linked to on Wikidata now, found by its stored QID. Null when the item is not in the Library.</summary>
    public async Task<EditorWikidataIdentityDto?> ReadIdentityAsync(Guid entityId, CancellationToken ct = default)
    {
        var facts = await ReadItemAsync(entityId, ct).ConfigureAwait(false);
        if (facts is null)
        {
            return null;
        }

        if (facts.Qid is null)
        {
            return new EditorWikidataIdentityDto { EntityId = entityId };
        }

        try
        {
            var described = await DescribeAsync(facts, [facts.Qid], configuration.Current, ct).ConfigureAwait(false);
            if (described.FirstOrDefault() is { } one)
            {
                return new EditorWikidataIdentityDto
                {
                    EntityId = entityId,
                    Qid = facts.Qid,
                    Label = one.Result.Label,
                    Description = one.Result.Description,
                    Type = one.Result.Type,
                    Year = one.Result.Year,
                    Url = one.Result.Url,
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Wikidata did not answer for the current identity {Qid} of item {EntityId}.", facts.Qid, entityId);
        }

        return new EditorWikidataIdentityDto { EntityId = entityId, Qid = facts.Qid, Url = OutsideLinks.Wikidata(facts.Qid), Offline = true };
    }

    /// <summary>
    /// Compares the provider match with the Wikidata item (shared identifiers first, then year, then title). The tab shows one amber line when
    /// they disagree and changes neither side. Null when the item is not in the Library; a Wikidata that does not answer is not a conflict.
    /// </summary>
    public async Task<EditorWikidataConflictDto?> CompareAsync(Guid entityId, EditorWikidataConflictRequestDto request, CancellationToken ct = default)
    {
        var facts = await ReadItemAsync(entityId, ct).ConfigureAwait(false);
        if (facts is null)
        {
            return null;
        }

        var qid = ParseQid(request.Qid);
        if (qid is null)
        {
            return new EditorWikidataConflictDto();
        }

        var rules = configuration.Current;
        IReadOnlyList<Described> described;
        try
        {
            described = await DescribeAsync(facts, [qid], rules, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Wikidata did not answer for the conflict check of {Qid} on item {EntityId}.", qid, entityId);
            return new EditorWikidataConflictDto();
        }

        if (described.FirstOrDefault() is not { } one)
        {
            return new EditorWikidataConflictDto();
        }

        var providerName = request.ProviderName?.Trim();
        var providerId = request.ProviderItemId?.Trim();
        var providerYear = YearOnly(request.ProviderYear);
        var providerTitle = request.ProviderTitle?.Trim();
        EditorWikidataConflictDto Say(bool conflict, string? reason) => new()
        {
            Conflict = conflict,
            Reason = reason,
            WikidataLabel = one.Result.Label,
            WikidataYear = one.Result.Year,
            WikidataType = one.Result.Type,
            ProviderName = providerName,
            ProviderYear = providerYear,
            ProviderTitle = providerTitle,
        };

        // 1. Identifiers: when Wikidata names the provider's record, they agree whatever else differs; when it names another record, they do not.
        if (!string.IsNullOrWhiteSpace(providerName) && !string.IsNullOrWhiteSpace(providerId))
        {
            var known = false;
            foreach (var rule in rules.Identifiers.Where(rule => string.Equals(rule.Provider, providerName, StringComparison.OrdinalIgnoreCase)))
            {
                var values = ValuesOf(one.Entity, rule.Properties);
                if (values.Count == 0)
                {
                    continue;
                }

                known = true;
                if (values.Any(value => SameId(value, providerId)))
                {
                    return Say(false, null);
                }
            }

            if (known)
            {
                return Say(true, EditorWikidataConflictReasons.Identifier);
            }
        }

        // 2. Year.
        if (int.TryParse(one.Result.Year, NumberStyles.None, CultureInfo.InvariantCulture, out var wikidataYear)
            && int.TryParse(providerYear, NumberStyles.None, CultureInfo.InvariantCulture, out var matchYear)
            && Math.Abs(wikidataYear - matchYear) > rules.YearTolerance)
        {
            return Say(true, EditorWikidataConflictReasons.Year);
        }

        // 3. Title: only when the two share nothing at all.
        if (!string.IsNullOrWhiteSpace(providerTitle) && !string.IsNullOrWhiteSpace(one.Result.Label)
            && !TitlesAgree(providerTitle, one.Entity))
        {
            return Say(true, EditorWikidataConflictReasons.Title);
        }

        return Say(false, null);
    }

    private static EditorWikidataSearchResultDto Empty(Guid entityId, string code) => new() { EntityId = entityId, MessageCode = code };

    // ── What the Engine knows about the item ───────────────────────────────────────────────────────────

    private async Task<ItemFacts?> ReadItemAsync(Guid entityId, CancellationToken ct)
    {
        var launch = await launches.ResolveEditorLaunchAsync(entityId, ct).ConfigureAwait(false);
        if (launch is null)
        {
            return null;
        }

        var kind = await kinds.ResolveAsync(entityId, ct).ConfigureAwait(false);
        var own = Latest(await canonicals.GetByEntityAsync(launch.WorkId, ct).ConfigureAwait(false));
        var root = launch.RootWorkId != Guid.Empty && launch.RootWorkId != launch.WorkId
            ? Latest(await canonicals.GetByEntityAsync(launch.RootWorkId, ct).ConfigureAwait(false))
            : own;

        // Ids the item stores: its own first; a parent's id (the show's, the album's) is the item's only when the item has none of its own.
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in configuration.Current.Identifiers)
        {
            if ((Value(own, rule.Key) ?? Value(root, rule.Key)) is { } id)
            {
                ids[rule.Key] = id;
            }
        }

        var year = Value(own, MetadataFieldConstants.Year) ?? YearOnly(Value(own, MetadataFieldConstants.AirDate));
        return new ItemFacts(
            kind,
            Value(own, MetadataFieldConstants.Title) ?? Value(own, MetadataFieldConstants.ShowName),
            YearOnly(year),
            Value(own, MetadataFieldConstants.Author) ?? Value(own, MetadataFieldConstants.Artist) ?? Value(own, MetadataFieldConstants.Director),
            // Only the item's own link: an episode is not linked to its show's Wikidata item, so the show's link is never shown as the episode's.
            Value(own, BridgeIdKeys.WikidataQid),
            ids);
    }

    // ── Reading and describing Wikidata items ──────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Described>> DescribeAsync(ItemFacts item, IReadOnlyList<string> qids, WikidataMatchConfiguration rules, CancellationToken ct)
    {
        var read = await entities.GetEntitiesAsync(qids, resolveEntityLabels: true, cancellationToken: ct).ConfigureAwait(false);
        var described = new List<Described>();
        foreach (var qid in qids)
        {
            if (read.TryGetValue(qid, out var entity) && entity is not null)
            {
                described.Add(new Described(Describe(item, entity, rules), entity));
            }
        }

        return described;
    }

    private static EditorWikidataResultDto Describe(ItemFacts item, WikidataEntityInfo entity, WikidataMatchConfiguration rules)
    {
        var classes = ClassesOf(entity, rules);
        var type = classes.Select(cls => cls.Words).FirstOrDefault(words => !string.IsNullOrWhiteSpace(words));
        var year = YearOf(entity, rules);
        var creator = LabelOf(entity, rules.CreatorProperties);
        var series = LabelOf(entity, rules.SeriesProperties);
        var relation = RelationOf(item.Kind, classes.Select(cls => cls.Qid), rules);
        var parent = relation == EditorWikidataRelations.Parent;
        return new EditorWikidataResultDto
        {
            Qid = entity.Id,
            Label = string.IsNullOrWhiteSpace(entity.Label) ? entity.Id : entity.Label,
            Description = string.IsNullOrWhiteSpace(entity.Description) ? null : entity.Description,
            Type = type,
            Year = year,
            Fact = year ?? creator ?? series,
            Relation = relation,
            CanApply = !parent,
            Reason = parent ? EditorWikidataMessageCodes.ParentNotAllowed : null,
            Url = OutsideLinks.Wikidata(entity.Id),
            Evidence = BuildEvidence(item, entity, year, creator, rules),
        };
    }

    /// <summary>Direct when a class of the item itself is among the entity's, else Parent when a class of the thing it belongs to is, else Other.</summary>
    internal static string RelationOf(string? kind, IEnumerable<string> classQids, WikidataMatchConfiguration rules)
    {
        if (string.IsNullOrWhiteSpace(kind) || !rules.Kinds.TryGetValue(kind, out var lists))
        {
            return EditorWikidataRelations.Other;
        }

        var held = classQids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (held.Overlaps(lists.Direct))
        {
            return EditorWikidataRelations.Direct;
        }

        return held.Overlaps(lists.Parent) ? EditorWikidataRelations.Parent : EditorWikidataRelations.Other;
    }

    private static List<(string Qid, string? Words)> ClassesOf(WikidataEntityInfo entity, WikidataMatchConfiguration rules)
    {
        var classes = new List<(string, string?)>();
        if (!entity.Claims.TryGetValue(rules.TypeProperty, out var claims))
        {
            return classes;
        }

        foreach (var claim in claims)
        {
            var qid = claim.Value?.EntityId ?? claim.Value?.RawValue;
            if (string.IsNullOrWhiteSpace(qid))
            {
                continue;
            }

            var words = rules.TypeWords.TryGetValue(qid, out var known) ? known : claim.Value?.EntityLabel?.Trim().ToLowerInvariant();
            classes.Add((qid, words));
        }

        return classes;
    }

    private static string? YearOf(WikidataEntityInfo entity, WikidataMatchConfiguration rules)
    {
        foreach (var property in rules.YearProperties)
        {
            if (!entity.Claims.TryGetValue(property, out var claims))
            {
                continue;
            }

            var years = claims.Select(claim => YearFromTime(claim.Value?.RawValue)).Where(year => year is not null).Select(year => year!.Value).ToList();
            if (years.Count > 0)
            {
                return years.Min().ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static string? LabelOf(WikidataEntityInfo entity, IEnumerable<string> properties)
    {
        foreach (var property in properties)
        {
            if (entity.Claims.TryGetValue(property, out var claims)
                && claims.Select(claim => claim.Value?.EntityLabel).FirstOrDefault(label => !string.IsNullOrWhiteSpace(label)) is { } label)
            {
                return label;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ValuesOf(WikidataEntityInfo entity, IEnumerable<string> properties) =>
    [
        .. properties
            .Where(property => entity.Claims.ContainsKey(property))
            .SelectMany(property => entity.Claims[property])
            .Select(claim => claim.Value?.RawValue)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()),
    ];

    // ── Alignment evidence ─────────────────────────────────────────────────────────────────────────────

    private static List<EditorWikidataEvidenceDto> BuildEvidence(ItemFacts item, WikidataEntityInfo entity, string? year, string? creator, WikidataMatchConfiguration rules)
    {
        var rows = new List<EditorWikidataEvidenceDto>();

        // Identifiers the item holds: a tick when Wikidata holds the same one, a cross when it holds another, "not checked" when it holds none.
        foreach (var rule in rules.Identifiers)
        {
            if (!item.Ids.TryGetValue(rule.Key, out var yours))
            {
                continue;
            }

            var theirs = ValuesOf(entity, rule.Properties);
            var state = theirs.Count == 0
                ? EditorWikidataEvidenceStates.NotChecked
                : theirs.Any(value => SameId(value, yours)) ? EditorWikidataEvidenceStates.Agrees : EditorWikidataEvidenceStates.Differs;
            rows.Add(new EditorWikidataEvidenceDto
            {
                Fact = EditorWikidataEvidenceFacts.Identifier,
                Label = rule.Label,
                State = state,
                Yours = yours,
                Wikidata = theirs.FirstOrDefault(),
            });
        }

        rows.Add(new EditorWikidataEvidenceDto
        {
            Fact = EditorWikidataEvidenceFacts.Title,
            State = Compare(item.Title, entity.Label, () => TitlesAgree(item.Title!, entity)),
            Yours = item.Title,
            Wikidata = entity.Label,
        });
        rows.Add(new EditorWikidataEvidenceDto
        {
            Fact = EditorWikidataEvidenceFacts.Year,
            State = Compare(item.Year, year, () => int.TryParse(item.Year, NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                && int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var b)
                && Math.Abs(a - b) <= rules.YearTolerance),
            Yours = item.Year,
            Wikidata = year,
        });
        rows.Add(new EditorWikidataEvidenceDto
        {
            Fact = EditorWikidataEvidenceFacts.Creator,
            State = Compare(item.Creator, creator, () => Contains(Normalize(item.Creator!), Normalize(creator!))),
            Yours = item.Creator,
            Wikidata = creator,
        });
        return rows;
    }

    private static string Compare(string? yours, string? theirs, Func<bool> agree) =>
        string.IsNullOrWhiteSpace(yours) || string.IsNullOrWhiteSpace(theirs)
            ? EditorWikidataEvidenceStates.NotChecked
            : agree() ? EditorWikidataEvidenceStates.Agrees : EditorWikidataEvidenceStates.Differs;

    // The title agrees with the label or any alias of the item, ignoring case, accents' absence and punctuation, or when one holds the other.
    private static bool TitlesAgree(string title, WikidataEntityInfo entity)
    {
        var wanted = Normalize(title);
        return new[] { entity.Label }.Concat(entity.Aliases ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Any(name => Contains(wanted, Normalize(name!)));
    }

    private static bool Contains(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

    private static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Two ids are the same when their letters and digits match (an ISBN with or without hyphens, an id with stray spaces).
    private static bool SameId(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    // ── Pasted links ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a Wikidata item number out of what was pasted: <c>Q123</c>, <c>q123</c>, a <c>wikidata.org/wiki/Q123</c> address (mobile, entity-data and
    /// <c>?title=Q123</c> forms included). Anything that is not one of these, including an address on another site, gives null.
    /// </summary>
    public static string? ParseQid(string? pasted)
    {
        var text = pasted?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var bare = BareQid().Match(text);
        if (bare.Success)
        {
            return "Q" + bare.Groups[1].Value;
        }

        if (!Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text, UriKind.Absolute, out var address)
            || !(address.Host.Equals("wikidata.org", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".wikidata.org", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var inAddress = QidInAddress().Match(Uri.UnescapeDataString(address.AbsolutePath + address.Query));
        return inAddress.Success ? "Q" + inAddress.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^[Qq]([1-9][0-9]*)$")]
    private static partial Regex BareQid();

    [GeneratedRegex(@"(?:^|[/=:])[Qq]([1-9][0-9]*)(?![0-9A-Za-z])")]
    private static partial Regex QidInAddress();

    // ── Small readers ──────────────────────────────────────────────────────────────────────────────────

    private static int? YearFromTime(string? time)
    {
        var match = TimeYear().Match(time ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
    }

    [GeneratedRegex(@"^[+]?([0-9]{3,4})-")]
    private static partial Regex TimeYear();

    private static string? YearOnly(string? text)
    {
        var trimmed = text?.Trim();
        return trimmed is { Length: >= 4 } && int.TryParse(trimmed.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static IReadOnlyDictionary<string, string> Latest(IEnumerable<CanonicalValue> values) =>
        values.Where(field => !string.IsNullOrWhiteSpace(field.Key))
            .GroupBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastScoredAt).First().Value, StringComparer.OrdinalIgnoreCase);

    private static string? Value(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
