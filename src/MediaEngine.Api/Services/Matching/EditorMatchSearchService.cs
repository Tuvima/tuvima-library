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
    EditorMatchVerdictCalculator calculator)
{
    /// <summary>The sentence shown for an item no provider matches.</summary>
    public const string NoProviderText = "No provider matches this kind of item. The details you enter stand on their own.";

    private const int MaxCandidates = 10;

    /// <summary>What the Match tab needs to open its search; null when no such item is in the Library.</summary>
    public async Task<EditorMatchSetupDto?> SetupAsync(Guid entityId, string kind, CancellationToken ct = default)
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
            Providers = plan.Providers,
            Fields = plan.Fields
                .Select(field => new EditorMatchFieldDto
                {
                    Key = field,
                    Label = EditorMatchFieldIds.Label(field),
                    Value = read.Fields.TryGetValue(field, out var filled) ? filled.Value : string.Empty,
                    From = read.Fields.TryGetValue(field, out var from) ? from.From : null,
                })
                .ToList(),
            Evidence = read.Evidence,
        };
    }

    /// <summary>Runs a search; null when no such item is in the Library.</summary>
    public async Task<EditorMatchSearchResultDto?> SearchAsync(Guid entityId, EditorMatchSearchRequestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EditorMatchKinds.For(request.Kind) is null)
        {
            return new EditorMatchSearchResultDto { EntityId = entityId, Message = NoProviderText };
        }

        var read = await reader.ReadAsync(entityId, request.Kind, ct).ConfigureAwait(false);
        if (read is null)
        {
            return null;
        }

        var plan = Plan(request.Kind, read);
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
        if (!HasSearchWords(fields))
        {
            return Empty(entityId, provider.Name, read, "Enter something to search for.");
        }

        var kind = EditorMatchKinds.For(request.Kind)!;
        var asked = BuildRequest(request.Kind, kind, provider.Name, fields, read);
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
            if (!SameFields(plain, fields) && HasSearchWords(plain))
            {
                var second = await search.SearchAsync(entityId, BuildRequest(request.Kind, kind, provider.Name, plain, read), ct).ConfigureAwait(false);
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
        var results = Judge(request.Kind, response.RetailCandidates, read, fields);
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

    private sealed record SearchPlan(IReadOnlyList<EditorMatchProviderDto> Providers, IReadOnlyList<string> Fields);

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
        if (isVideo)
        {
            // A local video has nothing to look up unless a YouTube ID is known.
            if (!read.Evidence.Identifiers.Any(item => item.Kind == EditorMatchIdentifierKinds.YouTube))
            {
                return null;
            }

            fields = [EditorMatchFieldIds.ProviderId];
        }

        if (fields.Count == 0)
        {
            return null;
        }

        var listed = catalog.List(mapping.MediaType)
            .Where(info => !isVideo || string.Equals(info.Name, "youtube", StringComparison.OrdinalIgnoreCase))
            .Where(info => !IsTv(kind) || string.Equals(info.Name, "tvdb", StringComparison.OrdinalIgnoreCase))
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
        return new SearchPlan(providers, fields);
    }

    private static bool IsTv(string kind) => kind.StartsWith("tv_", StringComparison.OrdinalIgnoreCase);

    // The canonical search's own request: the boxes become the draft fields it scores against, and the words searched.
    private static ItemCanonicalSearchRequestDto BuildRequest(
        string kindId, EditorMatchKind kind, string provider, IReadOnlyDictionary<string, string> fields, EditorMatchLocalRead read)
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
                words = !string.IsNullOrWhiteSpace(Get(EditorMatchFieldIds.Isbn))
                    ? Get(EditorMatchFieldIds.Isbn)
                    : $"{Get(EditorMatchFieldIds.Title)} {Get(EditorMatchFieldIds.Author)}";
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
                words = Get(EditorMatchFieldIds.ProviderId);
                break;
        }

        if (string.IsNullOrWhiteSpace(words))
        {
            words = Get(EditorMatchFieldIds.ProviderId);
        }

        return new ItemCanonicalSearchRequestDto
        {
            MediaType = kind.MediaType,
            TargetKind = kind.TargetKind,
            TargetFieldGroup = kind.TargetFieldGroup,
            DraftFields = draft,
            QueryOverride = words.Trim(),
            SearchMode = "retail_only",
            MaxCandidates = MaxCandidates,
            ProviderName = provider,
        };
    }

    private static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string>? fields) =>
        (fields ?? new Dictionary<string, string>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);

    // A box with only a year, an issue number or a provider ID gives a search nothing to look for by itself, except an ID.
    private static bool HasSearchWords(IReadOnlyDictionary<string, string> fields) =>
        fields.Any(pair => pair.Key is not (EditorMatchFieldIds.Year or EditorMatchFieldIds.Issue) && !string.IsNullOrWhiteSpace(pair.Value));

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
        Verdict = source.Verdict,
        Reasons = source.Reasons,
        Editions = editions ?? [],
        Edition = source.Edition,
        IsSuggested = suggested,
    };

    private EditorMatchResultDto Judge(
        string kindId, ItemCanonicalRetailCandidateDto candidate, EditorMatchLocalRead read, IReadOnlyDictionary<string, string> fields)
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
                MatchFactNames.Title => calculator.Title(YourTitle(kindId, fields, evidence), candidate.Title),
                MatchFactNames.Year => calculator.Year(fields.GetValueOrDefault(EditorMatchFieldIds.Year) ?? evidence.Year, candidate.Year),
                MatchFactNames.Number => calculator.Number(
                    kindId == EditorKinds.Comic && int.TryParse(fields.GetValueOrDefault(EditorMatchFieldIds.Issue), NumberStyles.Integer, CultureInfo.InvariantCulture, out var issue)
                        ? issue
                        : evidence.Number,
                    Number(extra),
                    kindId == EditorKinds.Comic ? "Issue" : kindId == EditorKinds.TvSeason ? "Season" : "Episode"),
                MatchFactNames.Creator => calculator.Creator(
                    fields.GetValueOrDefault(EditorMatchFieldIds.Author) ?? fields.GetValueOrDefault(EditorMatchFieldIds.AuthorOrNarrator)
                        ?? fields.GetValueOrDefault(EditorMatchFieldIds.Artist) ?? evidence.Creator,
                    creator),
                MatchFactNames.Runtime => calculator.Runtime(evidence.MeasuredSeconds, seconds is null ? null : seconds / 60d),
                MatchFactNames.TrackLength => calculator.TrackLength(evidence.MeasuredSeconds, seconds),
                MatchFactNames.Identifier => calculator.Identifier(IdentifiersFor(evidence, fields), RecordIds(candidate)),
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
            Verdict = calculator.Verdict(checks),
            Reasons = [.. checks.Select(check => new EditorMatchReasonDto { Fact = check.Fact, State = check.State, Text = check.Text, Note = check.Note })],
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
        EditorMatchLocalEvidenceDto evidence, IReadOnlyDictionary<string, string> fields)
    {
        var found = evidence.Identifiers.ToList();
        if (fields.TryGetValue(EditorMatchFieldIds.Isbn, out var isbn) && !found.Any(item => item.Kind == EditorMatchIdentifierKinds.Isbn))
        {
            found.Add(new EditorMatchIdentifierDto { Kind = EditorMatchIdentifierKinds.Isbn, Value = isbn });
        }

        if (fields.TryGetValue(EditorMatchFieldIds.ProviderId, out var typed))
        {
            var kind = typed.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? EditorMatchIdentifierKinds.Imdb
                : typed.All(char.IsAsciiDigit) ? EditorMatchIdentifierKinds.Tmdb
                : null;
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

    private static string? Extra(IReadOnlyDictionary<string, string> extra, string key) =>
        extra.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

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
