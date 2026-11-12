using System.Globalization;
using System.Text;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// The Match tab's search (universal editor v5, 3c): which providers fit the item and what each is ready to do, the boxes of the kind
/// pre-filled from the file, and a search that returns results with a picture, title, year, creator and length, a Strong / Possible /
/// Check verdict with its reasons, and for books, comics and audiobooks the work's editions. The search itself is the Library's own
/// canonical retail search, so the same providers answer in the same way; this class only decides what to ask, and judges what comes
/// back against the file. Nothing is written, and an item the Library has no provider for (a course, a supporting file, a local video
/// with no YouTube ID) is told so rather than searched.
/// </summary>
public sealed class EditorMatchSearchService(
    EditorMatchLocalEvidenceReader reader,
    IEditorMatchProviderCatalog catalog,
    IEditorMatchCandidateSearch search,
    IEditorConfigurationSource editorConfiguration,
    EditorMatchVerdictCalculator calculator,
    IEditorMatchKindResolver kinds,
    IVideoIdentifyWriter videos)
{
    /// <summary>The sentence shown for an item no provider matches.</summary>
    public const string NoProviderText = "No provider matches this kind of item. The details you enter stand on their own.";

    /// <summary>The sentence shown when the only thing typed is an ISBN that is not 10 or 13 digits.</summary>
    public const string InvalidIsbnText = "That does not look like an ISBN. An ISBN has 10 or 13 digits.";

    private const int MaxCandidates = 10;

    /// <summary>
    /// What the Match tab needs to open its search; null when no such item is in the Library. The kind of item is decided here, from the
    /// same rules as the editor's header, never taken from the caller.
    /// </summary>
    public async Task<EditorMatchSetupDto?> SetupAsync(Guid entityId, CancellationToken ct = default)
    {
        var kind = await kinds.ResolveAsync(entityId, ct).ConfigureAwait(false);
        return kind is null ? null : await SetupForKindAsync(entityId, kind, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a search for the item, whatever kind of item it is; null when no such item is in the Library.</summary>
    public async Task<EditorMatchSearchResultDto?> SearchAsync(Guid entityId, EditorMatchSearchRequestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = await kinds.ResolveAsync(entityId, ct).ConfigureAwait(false);
        return kind is null ? null : await SearchForKindAsync(entityId, kind, request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Judges one record the person picked outside the result list (a TheTVDB episode, a MusicBrainz track) against the item's file, with
    /// the same facts, tolerances and bands a search result is judged by. Null when no such item is in the Library or its kind has no provider.
    /// </summary>
    public async Task<EditorMatchResultDto?> JudgeCandidateAsync(Guid entityId, ItemCanonicalRetailCandidateDto candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var kind = await kinds.ResolveAsync(entityId, ct).ConfigureAwait(false);
        if (kind is null || EditorMatchKinds.For(kind) is null)
        {
            return null;
        }

        var read = await reader.ReadAsync(entityId, kind, ct).ConfigureAwait(false);
        return read is null ? null : JudgeRead(kind, candidate, read);
    }

    /// <summary>Judges a record against an item whose file has already been read (the group Match reads every ticked file once).</summary>
    internal EditorMatchResultDto JudgeRead(string kindId, ItemCanonicalRetailCandidateDto candidate, EditorMatchLocalRead read) =>
        Judge(kindId, candidate, read, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), kindId == EditorKinds.TvEpisode ? (true, read.Evidence.EpisodeTitle) : (false, null));

    /// <summary>As <see cref="SetupAsync(Guid, CancellationToken)"/> for a kind already decided.</summary>
    public async Task<EditorMatchSetupDto?> SetupForKindAsync(Guid entityId, string kind, CancellationToken ct = default)
    {
        // A kind with no provider (a course, a supporting file, a Series) is told so without reading anything: the id may not even be a work.
        if (EditorMatchKinds.For(kind) is null)
        {
            return new EditorMatchSetupDto { EntityId = entityId, Kind = kind, NoProvider = true, NoProviderMessage = NoProviderText };
        }

        var read = await reader.ReadAsync(entityId, kind, ct).ConfigureAwait(false);
        if (read is null)
        {
            return null;
        }

        var plan = Plan(kind, read);
        if (plan is null)
        {
            return new EditorMatchSetupDto
            {
                EntityId = entityId,
                Kind = kind,
                FileName = read.FileName,
                NoProvider = true,
                NoProviderMessage = NoProviderText,
                Evidence = read.Evidence,
            };
        }

        return new EditorMatchSetupDto
        {
            EntityId = entityId,
            Kind = kind,
            FileName = read.FileName,
            // A local video with no known YouTube ID has no provider to offer on its own, but the paste field stays: a pasted link or ID is
            // looked up directly. Both the message and the field are sent; the Dashboard shows the message and keeps the field.
            NoProvider = plan.NeedsPastedId,
            NoProviderMessage = plan.NeedsPastedId ? NoProviderText : null,
            Providers = plan.Providers,
            Fields = plan.Fields
                .Select(field => new EditorMatchFieldDto
                {
                    Key = field,
                    Label = EditorMatchFieldIds.Label(field, kind),
                    Value = read.Fields.TryGetValue(field, out var filled) ? filled.Value : string.Empty,
                    From = read.Fields.TryGetValue(field, out var from) ? from.From : null,
                })
                .ToList(),
            Evidence = read.Evidence,
        };
    }

    /// <summary>As <see cref="SearchAsync(Guid, EditorMatchSearchRequestDto, CancellationToken)"/> for a kind already decided.</summary>
    public async Task<EditorMatchSearchResultDto?> SearchForKindAsync(
        Guid entityId, string kindId, EditorMatchSearchRequestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EditorMatchKinds.For(kindId) is null)
        {
            return new EditorMatchSearchResultDto { EntityId = entityId, Message = NoProviderText };
        }

        var read = await reader.ReadAsync(entityId, kindId, ct).ConfigureAwait(false);
        if (read is null)
        {
            return null;
        }

        var plan = Plan(kindId, read);
        if (plan is null)
        {
            return Empty(entityId, string.Empty, read, NoProviderText);
        }

        var providerName = string.IsNullOrWhiteSpace(request.Provider)
            ? plan.Providers.FirstOrDefault(provider => provider.IsDefault)?.Name ?? plan.Providers[0].Name
            : request.Provider.Trim();
        var provider = plan.Providers.FirstOrDefault(item => string.Equals(item.Name, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return Empty(entityId, providerName, read, "That provider does not match this kind of item. Choose another provider.");
        }

        if (provider.State != EditorMatchProviderStates.Ready)
        {
            return Empty(entityId, provider.Name, read, provider.State == EditorMatchProviderStates.NeedsKey
                ? $"{provider.DisplayName} needs a key before it can be searched. Add it in Settings, or choose another provider."
                : $"{provider.DisplayName} is switched off. Turn it on in Settings, or choose another provider.");
        }

        var fields = Clean(request.Fields);
        if (string.Equals(kindId, EditorKinds.Video, StringComparison.OrdinalIgnoreCase))
        {
            return await SearchVideoAsync(entityId, provider, fields, read, ct).ConfigureAwait(false);
        }

        // A typed ISBN is cleaned first (dashes and spaces taken out), so the provider and the verdict both see the bare digits.
        var isbnTyped = fields.TryGetValue(EditorMatchFieldIds.Isbn, out var typedIsbn);
        var isbn = isbnTyped ? NormaliseTypedIsbn(typedIsbn!) : null;
        if (isbn is not null)
        {
            fields[EditorMatchFieldIds.Isbn] = isbn;
        }

        var lookup = LookupFor(plan, provider, fields);
        if (!HasSearchWords(fields, lookup))
        {
            // An ISBN by itself is a search (D11): looked up where the provider can, otherwise a plain message.
            if (isbnTyped)
            {
                return Empty(entityId, provider.Name, read, isbn is null
                    ? InvalidIsbnText
                    : $"{provider.DisplayName} cannot search by ISBN. Add a title.");
            }

            return Empty(entityId, provider.Name, read, "Enter something to search for.");
        }

        var kind = EditorMatchKinds.For(kindId)!;
        var asked = BuildRequest(kindId, kind, provider.Name, fields, read, lookup);
        var outcome = await search.SearchAsync(entityId, asked, ct).ConfigureAwait(false);
        if (outcome.Response is null)
        {
            return Empty(entityId, provider.Name, read, outcome.Refusal ?? "The search could not be run.");
        }

        var retried = false;
        var response = outcome.Response;
        if (response.RetailCandidates.Count == 0 && response.ProviderErrors.Count == 0)
        {
            // Nothing found and nothing failed: try once more with the punctuation taken out ("Spider-Man: Homecoming" against "Spider Man Homecoming").
            var plain = WithoutPunctuation(fields);
            if (!SameFields(plain, fields) && HasSearchWords(plain, lookup))
            {
                var second = await search.SearchAsync(entityId, BuildRequest(kindId, kind, provider.Name, plain, read, lookup), ct).ConfigureAwait(false);
                if (second.Response is { RetailCandidates.Count: > 0 } found)
                {
                    response = found;
                    retried = true;
                }
            }
        }

        var failed = response.ProviderErrors.Count > 0
            ? new[] { new EditorMatchFailedProviderDto { Name = provider.Name, DisplayName = provider.DisplayName } }
            : [];
        var results = Judge(kindId, response.RetailCandidates, read, fields);
        return new EditorMatchSearchResultDto
        {
            EntityId = entityId,
            Provider = provider.Name,
            Query = response.Query,
            RetriedWithoutPunctuation = retried,
            Results = results,
            FailedProviders = failed,
            Message = results.Count > 0
                ? null
                : failed.Length > 0
                    ? $"{provider.DisplayName} did not answer. Try again, or search in another provider."
                    : "Nothing matches that. Change the words, or search in another provider.",
            Evidence = read.Evidence,
        };
    }

    private sealed record SearchPlan(
        IReadOnlyList<EditorMatchProviderDto> Providers,
        IReadOnlyList<string> Fields,
        IReadOnlyDictionary<string, RetailProviderInfo> Infos,
        bool NeedsPastedId = false);

    // The providers and boxes of this kind of item; null when no provider matches it.
    private SearchPlan? Plan(string kind, EditorMatchLocalRead read)
    {
        var mapping = EditorMatchKinds.For(kind);
        if (mapping is null || !string.Equals(mapping.MediaType, read.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fields = editorConfiguration.Current.KindFor(kind).MatchFields.ToList();
        var isVideo = string.Equals(kind, EditorKinds.Video, StringComparison.OrdinalIgnoreCase);
        var needsPastedId = false;
        if (isVideo)
        {
            // A video is looked up by its YouTube ID. With none known, the person can still paste a link or an ID.
            needsPastedId = !read.Evidence.Identifiers.Any(item => item.Kind == EditorMatchIdentifierKinds.YouTube);
            fields = [EditorMatchFieldIds.ProviderId];
        }

        if (fields.Count == 0)
        {
            return null;
        }

        // Which kinds a provider is offered for is the provider file's editor_match.kinds; none listed means every kind of its media types.
        var listed = catalog.List(mapping.MediaType)
            .Where(info => info.Kinds is not { Count: > 0 } || info.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
            .OrderBy(info => info.Order)
            .ToList();
        if (listed.Count == 0)
        {
            return null;
        }

        var first = listed.FirstOrDefault(info => info.DefaultForKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)) ?? listed[0];
        var providers = new[] { first }.Concat(listed.Where(info => info != first))
            .Select(info => new EditorMatchProviderDto
            {
                Name = info.Name,
                DisplayName = info.DisplayName,
                State = !info.Enabled ? EditorMatchProviderStates.Off : info.NeedsKey ? EditorMatchProviderStates.NeedsKey : EditorMatchProviderStates.Ready,
                IsDefault = info == first,
            })
            .ToList();
        return new SearchPlan(providers, fields, listed.ToDictionary(info => info.Name, StringComparer.OrdinalIgnoreCase), needsPastedId);
    }

    private static bool IsTv(string kind) => kind.StartsWith("tv_", StringComparison.OrdinalIgnoreCase);

    // The canonical search's own request: the boxes become the draft fields it scores against, and the words searched.
    private static ItemCanonicalSearchRequestDto BuildRequest(
        string kindId, EditorMatchKind kind, string provider, IReadOnlyDictionary<string, string> fields, EditorMatchLocalRead read,
        IReadOnlyDictionary<string, string> lookup)
    {
        string Get(string key) => fields.TryGetValue(key, out var value) ? value : string.Empty;
        var draft = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Put(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                draft[key] = value.Trim();
            }
        }

        string? words = null;
        switch (kindId)
        {
            case EditorKinds.Movie:
                Put(MetadataFieldConstants.Title, Get(EditorMatchFieldIds.Title));
                Put(MetadataFieldConstants.Year, Get(EditorMatchFieldIds.Year));
                words = Get(EditorMatchFieldIds.Title);
                break;
            case EditorKinds.Book:
                Put(MetadataFieldConstants.Title, Get(EditorMatchFieldIds.Title));
                Put(MetadataFieldConstants.Author, Get(EditorMatchFieldIds.Author));
                words = $"{Get(EditorMatchFieldIds.Title)} {Get(EditorMatchFieldIds.Author)}";
                break;
            case EditorKinds.Audiobook:
                Put(MetadataFieldConstants.Title, Get(EditorMatchFieldIds.Title));
                Put(MetadataFieldConstants.Author, Get(EditorMatchFieldIds.AuthorOrNarrator));
                words = $"{Get(EditorMatchFieldIds.Title)} {Get(EditorMatchFieldIds.AuthorOrNarrator)}";
                break;
            case EditorKinds.Comic:
                Put(MetadataFieldConstants.Series, Get(EditorMatchFieldIds.Series));
                Put(MetadataFieldConstants.SeriesPosition, Get(EditorMatchFieldIds.Issue));
                Put(MetadataFieldConstants.Year, Get(EditorMatchFieldIds.Year));
                words = $"{Get(EditorMatchFieldIds.Series)} {Get(EditorMatchFieldIds.Issue)}";
                break;
            case EditorKinds.TvShow:
            case EditorKinds.TvSeason:
            case EditorKinds.TvEpisode:
                Put(MetadataFieldConstants.ShowName, Get(EditorMatchFieldIds.Show));
                Put(MetadataFieldConstants.Year, Get(EditorMatchFieldIds.Year));
                if (kindId == EditorKinds.TvEpisode && read.Evidence.Number is { } episode)
                {
                    Put(MetadataFieldConstants.EpisodeNumber, episode.ToString(CultureInfo.InvariantCulture));
                }

                words = Get(EditorMatchFieldIds.Show);
                break;
            case EditorKinds.Album:
                Put(MetadataFieldConstants.Artist, Get(EditorMatchFieldIds.Artist));
                Put(MetadataFieldConstants.Album, Get(EditorMatchFieldIds.Album));
                words = $"{Get(EditorMatchFieldIds.Artist)} {Get(EditorMatchFieldIds.Album)}";
                break;
            case EditorKinds.Song:
                Put(MetadataFieldConstants.Artist, Get(EditorMatchFieldIds.Artist));
                Put(MetadataFieldConstants.Album, Get(EditorMatchFieldIds.Album));
                Put(MetadataFieldConstants.Title, Get(EditorMatchFieldIds.Song));
                words = $"{Get(EditorMatchFieldIds.Artist)} {Get(EditorMatchFieldIds.Song)}";
                break;
            default:
                break;
        }

        // A typed identifier is never search words: it either drives the provider's own id lookup (lookup) or it is evidence only.
        if (string.IsNullOrWhiteSpace(words) && lookup.Count > 0)
        {
            words = lookup.Values.First();
        }

        return new ItemCanonicalSearchRequestDto
        {
            MediaType = kind.MediaType,
            TargetKind = kind.TargetKind,
            TargetFieldGroup = kind.TargetFieldGroup,
            DraftFields = draft,
            QueryOverride = (words ?? string.Empty).Trim(),
            SearchMode = "retail_only",
            MaxCandidates = MaxCandidates,
            ProviderName = provider,
            LookupFields = lookup.Count == 0 ? null : new Dictionary<string, string>(lookup, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// A typed ISBN as bare digits: spaces and dashes taken out, an X check character kept for a 10 digit ISBN. Null when it is not
    /// 10 or 13 digits, or holds anything but digits, spaces, dashes and X.
    /// </summary>
    internal static string? NormaliseTypedIsbn(string typed) =>
        typed.All(ch => char.IsAsciiDigit(ch) || ch is ' ' or '-' or 'X' or 'x')
            ? EditorMatchLocalEvidenceReader.IsbnOf(typed)
            : null;

    private static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string>? fields) =>
        (fields ?? new Dictionary<string, string>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);

    // A year, an issue number, an ISBN or a provider ID gives a search nothing to look for by itself; only an identifier the provider can
    // look up directly (lookup) does. Otherwise the identifier is evidence for the verdict and nothing more.
    private static bool HasSearchWords(IReadOnlyDictionary<string, string> fields, IReadOnlyDictionary<string, string> lookup) =>
        lookup.Count > 0
        || fields.Any(pair => pair.Key is not (EditorMatchFieldIds.Year or EditorMatchFieldIds.Issue or EditorMatchFieldIds.ProviderId or EditorMatchFieldIds.Isbn)
                              && !string.IsNullOrWhiteSpace(pair.Value));

    // The typed identifiers this provider can look up directly, by the search field its lookup needs (editor_match.id_lookup).
    private static Dictionary<string, string> LookupFor(SearchPlan plan, EditorMatchProviderDto provider, IReadOnlyDictionary<string, string> fields)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idLookup = plan.Infos.TryGetValue(provider.Name, out var info) ? info.IdLookup : null;
        if (idLookup is not { Count: > 0 })
        {
            return found;
        }

        foreach (var (box, kindOfId) in new[] { (EditorMatchFieldIds.Isbn, EditorMatchIdentifierKinds.Isbn), (EditorMatchFieldIds.ProviderId, (string?)null) })
        {
            if (!fields.TryGetValue(box, out var typed) || string.IsNullOrWhiteSpace(typed))
            {
                continue;
            }

            // Something typed into the ISBN box that is not an ISBN is never sent to a lookup; it stays evidence only.
            if (kindOfId == EditorMatchIdentifierKinds.Isbn && NormaliseTypedIsbn(typed) is null)
            {
                continue;
            }

            var kindName = kindOfId ?? ClassifyId(typed, provider.Name);
            if (kindName is not null && idLookup.TryGetValue(kindName, out var field))
            {
                found[field] = typed.Trim();
            }
        }

        return found;
    }

    // What kind of identifier a typed Provider ID is: an IMDb ID, a MusicBrainz ID, or (digits) the chosen provider's own ID.
    internal static string? ClassifyId(string typed, string providerName)
    {
        var text = typed.Trim();
        if (text.Length >= 9 && text.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && text[2..].All(char.IsAsciiDigit))
        {
            return EditorMatchIdentifierKinds.Imdb;
        }

        if (Guid.TryParse(text, out _))
        {
            return EditorMatchIdentifierKinds.MusicBrainz;
        }

        return text.Length > 0 && text.All(char.IsAsciiDigit) ? providerName.ToLowerInvariant() : null;
    }

    private static Dictionary<string, string> WithoutPunctuation(IReadOnlyDictionary<string, string> fields) =>
        fields.ToDictionary(
            pair => pair.Key,
            pair => pair.Key is EditorMatchFieldIds.Year or EditorMatchFieldIds.Issue or EditorMatchFieldIds.ProviderId or EditorMatchFieldIds.Isbn
                ? pair.Value
                : StripPunctuation(pair.Value),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Letters and digits kept, every other mark taken out ("Spider-Man: Homecoming" is "Spider Man Homecoming").</summary>
    internal static string StripPunctuation(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool SameFields(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && string.Equals(pair.Value, other, StringComparison.Ordinal));

    private static EditorMatchSearchResultDto Empty(Guid entityId, string provider, EditorMatchLocalRead read, string message) => new()
    {
        EntityId = entityId,
        Provider = provider,
        Message = message,
        Evidence = read.Evidence,
    };

    // A video is looked up by its YouTube ID: the one the Library already knows, or a link or ID pasted into the box. One result comes back
    // (or none), judged on the identifier that was asked for and the title; Apply match stages it like every other match.
    private async Task<EditorMatchSearchResultDto> SearchVideoAsync(
        Guid entityId, EditorMatchProviderDto provider, IReadOnlyDictionary<string, string> fields, EditorMatchLocalRead read, CancellationToken ct)
    {
        fields.TryGetValue(EditorMatchFieldIds.ProviderId, out var pasted);
        if (string.IsNullOrWhiteSpace(pasted))
        {
            return Empty(entityId, provider.Name, read, "Paste a YouTube link or the video's 11-character ID.");
        }

        var outcome = await videos.LookupAsync(entityId, pasted, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            return Empty(entityId, provider.Name, read, outcome.Message ?? "The video could not be looked up.");
        }

        var info = outcome.Info!;
        var id = outcome.VideoId!;
        var year = info.Published?.Year.ToString(CultureInfo.InvariantCulture);
        var candidate = new ItemCanonicalRetailCandidateDto
        {
            CandidateId = $"youtube:{id}",
            ProviderId = provider.Name,
            ProviderName = provider.Name,
            ProviderItemId = id,
            Title = info.Title!,
            Year = year,
            Description = info.Description,
            CompositeScore = 1.0,
            RequiredFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [MetadataFieldConstants.Title] = info.Title! },
            SuggestedFields = VideoSuggested(info, year),
            ExtraFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["youtube_id"] = id },
        };
        var evidence = read.Evidence;
        var checks = new List<MatchFactCheck>
        {
            calculator.Title(evidence.FileTitle, info.Title),
            calculator.Identifier(
                [new EditorMatchIdentifierDto { Kind = EditorMatchIdentifierKinds.YouTube, Value = id }],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["youtube_id"] = id }),
        };
        var result = new EditorMatchResultDto
        {
            Candidate = candidate,
            ProviderName = provider.DisplayName,
            Title = info.Title!,
            Year = year,
            Creator = info.Channel,
            Description = info.Description,
            SourceUrl = SourceUrl(candidate, false),
            KindText = "Video",
            Verdict = calculator.Verdict(checks),
            Reasons = ReasonsOf(checks),
        };
        return new EditorMatchSearchResultDto
        {
            EntityId = entityId,
            Provider = provider.Name,
            Query = id,
            Results = [result],
            Evidence = read.Evidence,
        };
    }

    private static Dictionary<string, string> VideoSuggested(VideoOnlineInfo info, string? year)
    {
        var suggested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(info.Channel))
        {
            suggested[MetadataFieldConstants.Network] = info.Channel!;
        }

        if (!string.IsNullOrWhiteSpace(year))
        {
            suggested[MetadataFieldConstants.Year] = year!;
        }

        return suggested;
    }

    // ── Judging the results ──────────────────────────────────────────────────────────────────────────────────

    private List<EditorMatchResultDto> Judge(
        string kindId, IReadOnlyList<ItemCanonicalRetailCandidateDto> candidates, EditorMatchLocalRead read, IReadOnlyDictionary<string, string> fields)
    {
        var judged = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .Select(candidate => (Candidate: candidate, Result: Judge(kindId, candidate, read, fields)))
            .ToList();
        if (!EditorMatchKinds.HasEditions(kindId))
        {
            return [.. judged.OrderByDescending(item => item.Candidate.CompositeScore).Select(item => item.Result)];
        }

        // Books, comics and audiobooks: one row per work, with the editions the provider lists under it.
        var works = judged
            .GroupBy(item => WorkKey(item.Result), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Candidate.CompositeScore).ToList())
            .OrderByDescending(group => group[0].Candidate.CompositeScore)
            .ToList();
        return [.. works.Select(group => Work(group.Select(item => item.Result).ToList(), read))];
    }

    private static string WorkKey(EditorMatchResultDto result) =>
        new string(result.Title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray())
        + "|" + new string((result.Creator ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // The work is shown as its closest edition; the other editions are listed under it, the suggested one marked.
    private static EditorMatchResultDto Work(List<EditorMatchResultDto> editions, EditorMatchLocalRead read)
    {
        var suggested = editions
            .OrderByDescending(edition => edition.Reasons.Any(reason => reason.Fact == MatchFactNames.Identifier && reason.State == EditorMatchEvidenceStates.Agrees))
            .ThenByDescending(edition => edition.Reasons.Any(reason => reason.Fact == MatchFactNames.Year && reason.State == EditorMatchEvidenceStates.Agrees))
            .ThenByDescending(edition => !string.IsNullOrWhiteSpace(read.Evidence.Year) && edition.Year == read.Evidence.Year)
            .First();
        if (editions.Count == 1)
        {
            return suggested;
        }

        var listed = editions.Select(edition => Copy(edition, edition == suggested)).ToList();
        return Copy(suggested, suggested: false, editions: listed);
    }

    private static EditorMatchResultDto Copy(EditorMatchResultDto source, bool suggested, IReadOnlyList<EditorMatchResultDto>? editions = null) => new()
    {
        Candidate = source.Candidate,
        ProviderName = source.ProviderName,
        Title = source.Title,
        Year = source.Year,
        Creator = source.Creator,
        LengthText = source.LengthText,
        ImageUrl = source.ImageUrl,
        Description = source.Description,
        SourceUrl = source.SourceUrl,
        KindText = source.KindText,
        IsCollection = source.IsCollection,
        SeriesName = source.SeriesName,
        SeriesNumber = source.SeriesNumber,
        Verdict = source.Verdict,
        Reasons = source.Reasons,
        Editions = editions ?? [],
        Edition = source.Edition,
        IsSuggested = suggested,
    };

    private EditorMatchResultDto Judge(
        string kindId, ItemCanonicalRetailCandidateDto candidate, EditorMatchLocalRead read, IReadOnlyDictionary<string, string> fields,
        (bool Use, string? Title) yourTitle = default)
    {
        var extra = candidate.ExtraFields;
        var evidence = read.Evidence;
        var creator = candidate.Author ?? candidate.Director ?? Extra(extra, "artist");
        var (seconds, lengthText) = Length(kindId, extra);
        var isTv = IsTv(kindId);

        var checks = new List<MatchFactCheck>();
        foreach (var fact in calculator.FactsFor(kindId))
        {
            checks.Add(fact switch
            {
                MatchFactNames.Title => calculator.Title(yourTitle.Use ? yourTitle.Title : YourTitle(kindId, fields, evidence), candidate.Title),
                MatchFactNames.Year => calculator.Year(fields.GetValueOrDefault(EditorMatchFieldIds.Year) ?? evidence.Year, candidate.Year),
                MatchFactNames.Number when kindId == EditorKinds.TvEpisode => calculator.SeasonAndEpisode(
                    evidence.Season, evidence.Number, IntOf(candidate.RequiredFields, MetadataFieldConstants.SeasonNumber), Number(extra)),
                MatchFactNames.Number => calculator.Number(
                    kindId == EditorKinds.Comic && int.TryParse(fields.GetValueOrDefault(EditorMatchFieldIds.Issue), NumberStyles.Integer, CultureInfo.InvariantCulture, out var issue)
                        ? issue
                        : evidence.Number,
                    Number(extra),
                    kindId == EditorKinds.Comic ? "Issue" : kindId == EditorKinds.TvSeason ? "Season" : "Episode"),
                // An episode's show and a song's album: what the file says (a typed box, else what the Engine read from the name, tags and folders)
                // against what the record carries. A side that has nothing leaves the row unknown, never against the match.
                MatchFactNames.Show => calculator.Named(
                    MatchFactNames.Show, "Show",
                    fields.GetValueOrDefault(EditorMatchFieldIds.Show) ?? (read.Fields.TryGetValue(EditorMatchFieldIds.Show, out var fileShow) ? fileShow.Value : evidence.FileTitle),
                    candidate.RequiredFields.GetValueOrDefault(MetadataFieldConstants.ShowName)),
                MatchFactNames.Album => calculator.Named(
                    MatchFactNames.Album, "Album",
                    fields.GetValueOrDefault(EditorMatchFieldIds.Album) ?? (read.Fields.TryGetValue(EditorMatchFieldIds.Album, out var fileAlbum) ? fileAlbum.Value : null),
                    candidate.RequiredFields.GetValueOrDefault(MetadataFieldConstants.Album) ?? Extra(extra, MetadataFieldConstants.Album)),
                MatchFactNames.Creator => calculator.Creator(
                    fields.GetValueOrDefault(EditorMatchFieldIds.Author) ?? fields.GetValueOrDefault(EditorMatchFieldIds.AuthorOrNarrator)
                        ?? fields.GetValueOrDefault(EditorMatchFieldIds.Artist) ?? evidence.Creator,
                    creator),
                MatchFactNames.Runtime => calculator.Runtime(evidence.MeasuredSeconds, seconds is null ? null : seconds / 60d),
                MatchFactNames.TrackLength => calculator.TrackLength(evidence.MeasuredSeconds, seconds),
                MatchFactNames.Identifier => calculator.Identifier(IdentifiersFor(evidence, fields, candidate.ProviderId), RecordIds(candidate)),
                _ => new MatchFactCheck(fact, EditorMatchEvidenceStates.NotChecked, null, null, "Not checked"),
            });
        }

        return new EditorMatchResultDto
        {
            Candidate = candidate,
            ProviderName = candidate.ProviderName,
            Title = candidate.Title,
            Year = candidate.Year,
            Creator = creator,
            LengthText = lengthText,
            ImageUrl = candidate.PreviewUrl,
            Description = candidate.Description,
            SourceUrl = SourceUrl(candidate, isTv),
            KindText = KindText(kindId),
            IsCollection = calculator.IsCollection(candidate.Title, extra, SeriesNumber(extra)),
            SeriesName = Extra(extra, MetadataFieldConstants.Series),
            SeriesNumber = SeriesNumber(extra),
            Verdict = calculator.Verdict(checks),
            Reasons = ReasonsOf(checks),
            Edition = EditorMatchKinds.HasEditions(kindId)
                ? new EditorMatchEditionDto
                {
                    Year = candidate.Year,
                    Publisher = Extra(extra, "publisher"),
                    Format = Extra(extra, "format"),
                    Language = Extra(extra, "language"),
                }
                : null,
        };
    }

    // What the person typed is what the title is judged against (they may have corrected it); the file's own title when the box is empty.
    private static string? YourTitle(string kindId, IReadOnlyDictionary<string, string> fields, EditorMatchLocalEvidenceDto evidence)
    {
        var typed = kindId switch
        {
            EditorKinds.Comic => fields.GetValueOrDefault(EditorMatchFieldIds.Series),
            EditorKinds.TvShow or EditorKinds.TvSeason or EditorKinds.TvEpisode => fields.GetValueOrDefault(EditorMatchFieldIds.Show),
            EditorKinds.Album => fields.GetValueOrDefault(EditorMatchFieldIds.Album),
            EditorKinds.Song => fields.GetValueOrDefault(EditorMatchFieldIds.Song),
            _ => fields.GetValueOrDefault(EditorMatchFieldIds.Title),
        };
        return string.IsNullOrWhiteSpace(typed) ? evidence.FileTitle : typed;
    }

    // The identifiers found with the file, and one the person typed into the Provider ID or ISBN box.
    private static IReadOnlyCollection<EditorMatchIdentifierDto> IdentifiersFor(
        EditorMatchLocalEvidenceDto evidence, IReadOnlyDictionary<string, string> fields, string providerName)
    {
        var found = evidence.Identifiers.ToList();
        if (fields.TryGetValue(EditorMatchFieldIds.Isbn, out var isbn) && !found.Any(item => item.Kind == EditorMatchIdentifierKinds.Isbn))
        {
            found.Add(new EditorMatchIdentifierDto { Kind = EditorMatchIdentifierKinds.Isbn, Value = isbn });
        }

        if (fields.TryGetValue(EditorMatchFieldIds.ProviderId, out var typed))
        {
            var kind = ClassifyId(typed, providerName) switch
            {
                EditorMatchIdentifierKinds.Imdb => EditorMatchIdentifierKinds.Imdb,
                EditorMatchIdentifierKinds.MusicBrainz => EditorMatchIdentifierKinds.MusicBrainz,
                "tmdb" => EditorMatchIdentifierKinds.Tmdb,
                _ => null,
            };
            if (kind is not null && !found.Any(item => item.Kind == kind))
            {
                found.Add(new EditorMatchIdentifierDto { Kind = kind, Value = typed });
            }
        }

        return found;
    }

    // The identifiers a record carries, by the keys the comparison uses.
    private static Dictionary<string, string> RecordIds(ItemCanonicalRetailCandidateDto candidate)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in candidate.BridgeIds)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ids[key] = value;
            }
        }

        foreach (var key in new[] { "isbn", "isrc", "youtube_id", "imdb_id", "tmdb_id" })
        {
            if (Extra(candidate.ExtraFields, key) is { } value)
            {
                ids.TryAdd(key, value);
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.ProviderItemId))
        {
            var own = candidate.ProviderName.ToLowerInvariant() switch
            {
                "tmdb" => "tmdb_id",
                "youtube" => "youtube_id",
                _ => null,
            };
            if (own is not null)
            {
                ids.TryAdd(own, candidate.ProviderItemId);
            }
        }

        return ids;
    }

    // The compare table's rows: each fact with what the file and the record say, so the Dashboard never compares anything itself (v6, D9).
    private static List<EditorMatchReasonDto> ReasonsOf(IEnumerable<MatchFactCheck> checks) =>
        [.. checks.Select(check => new EditorMatchReasonDto
        {
            Fact = check.Fact,
            State = check.State,
            YourFile = string.IsNullOrWhiteSpace(check.Yours) ? null : check.Yours,
            Result = string.IsNullOrWhiteSpace(check.Theirs) ? null : check.Theirs,
            Text = check.Text,
            Note = check.Note,
        })];

    // The record's place in its series as written: "2" for 2 or 2.0, "2.5" kept.
    private static string? SeriesNumber(IReadOnlyDictionary<string, string> extra)
    {
        foreach (var key in new[] { MetadataFieldConstants.SeriesPosition, "issue_number", "issue" })
        {
            if (Extra(extra, key) is not { } text)
            {
                continue;
            }

            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? number.ToString("0.##", CultureInfo.InvariantCulture)
                : text;
        }

        return null;
    }

    private static string? Extra(IReadOnlyDictionary<string, string> extra, string key) =>
        extra.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int? IntOf(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static int? Number(IReadOnlyDictionary<string, string> extra)
    {
        foreach (var key in new[] { "issue_number", MetadataFieldConstants.SeriesPosition, "issue", MetadataFieldConstants.EpisodeNumber, "episode_number", MetadataFieldConstants.TrackNumber })
        {
            if (Extra(extra, key) is { } text
                && int.TryParse(text.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }

        return null;
    }

    // The record's own length when the provider gave one: minutes (a film), milliseconds (a track), or a clock or seconds (an audiobook).
    private static (double? Seconds, string? Text) Length(string kindId, IReadOnlyDictionary<string, string> extra)
    {
        double? seconds = null;
        if (Extra(extra, "runtime") is { } runtime && double.TryParse(runtime, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
        {
            seconds = minutes * 60;
        }
        else if (Extra(extra, "duration_ms") is { } ms && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var millis) && millis > 0)
        {
            seconds = millis / 1000d;
        }
        else if (Extra(extra, "duration") is { } duration)
        {
            if (duration.Contains(':') && TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var clock))
            {
                seconds = clock.TotalSeconds;
            }
            else if (double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0)
            {
                seconds = kindId is EditorKinds.Song or EditorKinds.Audiobook ? number : number * 60;
            }
        }

        if (seconds is not > 0)
        {
            return (null, null);
        }

        var text = kindId is EditorKinds.Song
            ? EditorMatchVerdictCalculator.ClockText(seconds)
            : $"{Math.Round(seconds.Value / 60d).ToString("0", CultureInfo.InvariantCulture)} min";
        return (seconds, text);
    }

    private static string KindText(string kindId) => kindId switch
    {
        EditorKinds.Movie => "Film",
        EditorKinds.Book => "Book",
        EditorKinds.Audiobook => "Audiobook",
        EditorKinds.Comic => "Comic issue",
        EditorKinds.TvShow => "Show",
        EditorKinds.TvSeason => "Season",
        EditorKinds.TvEpisode => "Episode",
        EditorKinds.Album => "Album",
        EditorKinds.Song => "Song",
        _ => "Video",
    };

    private static string? SourceUrl(ItemCanonicalRetailCandidateDto candidate, bool isTv)
    {
        var id = candidate.ProviderItemId;
        return candidate.ProviderName.ToLowerInvariant() switch
        {
            "tmdb" => OutsideLinks.Tmdb(id, isTv ? "TV" : "Movies"),
            "tvdb" => OutsideLinks.TvdbSeries(id),
            "openlibrary" => OutsideLinks.OpenLibraryWork(id),
            "apple_books" or "apple_api" => OutsideLinks.AppleBooks(id),
            "comicvine" => OutsideLinks.ComicVineIssue(id),
            "musicbrainz" => OutsideLinks.MusicBrainz("release-group", id) ?? OutsideLinks.MusicBrainz("recording", id) ?? OutsideLinks.MusicBrainz("release", id),
            "youtube" => string.IsNullOrWhiteSpace(id) ? null : OutsideLinks.WebPage($"https://www.youtube.com/watch?v={Uri.EscapeDataString(id)}"),
            _ => null,
        };
    }
}
