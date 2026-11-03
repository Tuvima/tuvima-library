using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the pop-over editor knows about the item it is on: it loads the editor field snapshot the Details tool uses and turns it into the
/// field presentations (so "editable", "locked provider fact" and "inherited" mean the same everywhere). It never saves: every change is
/// staged in the pending list and written only by Save changes after Review changes.
/// </summary>
public sealed class ItemFieldEditSession
{
    private readonly IEngineApiClient _api;
    private readonly ILogger? _logger;
    private MediaEditorContextDto? _context;
    private MediaEditorScopeDto? _scope;
    private EditorSubjectDto? _subject;
    private string? _mediaType;
    private Guid? _profileId;
    private IReadOnlyDictionary<string, string>? _profileOverrides;
    private ItemEditorPreferencesResponse? _preferences;
    private MediaEditorDetailsPresentation _presentation = new();
    private bool _suggestionsLoaded;

    public ItemFieldEditSession(IEngineApiClient api, ILogger? logger = null)
    {
        _api = api;
        _logger = logger;
    }

    /// <summary>Raised whenever what the editor shows changes (a load finished or failed).</summary>
    public event Action? Changed;

    public bool Loading { get; private set; }

    public bool Loaded { get; private set; }

    public string? LoadError { get; private set; }

    /// <summary>The asset types the active scope can hold artwork for, such as CoverArt, Background and Logo.</summary>
    public IReadOnlyList<string> ArtworkSlots => _scope?.ArtworkSlots ?? [];

    public bool CanEditArtwork => _scope?.CanEditArtwork == true;

    /// <summary>The item that owns the artwork of the active scope (a season's own poster belongs to the season, not the show).</summary>
    public Guid ArtworkEntityId => _scope?.ArtworkOwnerEntityId ?? _scope?.FieldEntityId ?? Guid.Empty;

    /// <summary>What kind of item owns the artwork (Work, Person, ...).</summary>
    public string ArtworkEntityKind => _scope?.ArtworkOwnerEntityKind ?? _scope?.FieldEntityKind ?? "Work";

    /// <summary>
    /// Where the item opened sits in the Engine's own answer: show, season and episode, or album and track, outermost first, each with the id
    /// its fields are staged under (spec D7). Empty for a movie, a book, a person or any type that has no such chain.
    /// </summary>
    public IReadOnlyList<EditTrailItem> ScopeChain
    {
        get
        {
            if (_context is null || _subject is not null)
            {
                return [];
            }

            var tv = string.Equals(_context.MediaType, "TV", StringComparison.OrdinalIgnoreCase);
            var music = string.Equals(_context.MediaType, "Music", StringComparison.OrdinalIgnoreCase);
            return _context.Scopes
                .OrderBy(scope => scope.Order)
                .Select(scope => (Scope: scope, Kind: scope.ScopeId.ToLowerInvariant() switch
                {
                    "series" when tv => EditTrailKinds.Show,
                    "season" when tv => EditTrailKinds.Season,
                    "episode" when tv => EditTrailKinds.Episode,
                    "album" when music => EditTrailKinds.Album,
                    "track" when music => EditTrailKinds.Track,
                    _ => null,
                }))
                .Where(entry => entry.Kind is not null && entry.Scope.FieldEntityId != Guid.Empty)
                .Select(entry => new EditTrailItem(
                    entry.Scope.FieldEntityId,
                    string.IsNullOrWhiteSpace(entry.Scope.DisplayTitle) ? entry.Scope.Label : entry.Scope.DisplayTitle,
                    entry.Kind!))
                .ToList();
        }
    }

    /// <summary>The id of the active scope (movie, series, season, episode, album...), which names the kind of item being edited.</summary>
    public string ScopeId => _scope?.ScopeId ?? string.Empty;

    /// <summary>The item the editor was opened on; the provider artwork gallery is asked about this one.</summary>
    public Guid EditorEntityId { get; private set; }

    /// <summary>The media type of the item being edited (Movies, TV, Books...).</summary>
    public string MediaType => _context?.MediaType ?? _mediaType ?? string.Empty;

    /// <summary>Whether Match and Identity applies to the active scope (the editor's own rule), so the banner only offers it where it works.</summary>
    public bool CanMatch => _subject is null && MediaEditorMatchRule.CanMatch(_context?.MediaType ?? _mediaType, _scope?.ScopeId);

    /// <summary>
    /// The kind of thing being edited when it is a person, Series, curated collection or Universe rather than a library title (the
    /// <see cref="EditorSubjectKinds"/> value); null for a title. Its fields, pictures and tools come from the Engine's subject answer.
    /// </summary>
    public string? SubjectKind => _subject?.Kind;

    /// <summary>Whether the Artwork tool may ask the item's providers for pictures. Only a library title has providers to ask.</summary>
    public bool ProvidersAvailable => _subject is null;

    public Guid EntityId => _scope?.FieldEntityId ?? Guid.Empty;

    /// <summary>Whether the active scope lets this person change its fields at all (a read-only library or file says no).</summary>
    public bool CanEditFields => _scope?.CanEditFields == true;

    public MediaEditorDetailsPresentation Presentation => _presentation;

    /// <summary>Existing library genres, offered while typing a genre.</summary>
    public IReadOnlyList<string> GenreSuggestions { get; private set; } = [];

    /// <summary>Existing library tags, offered while typing a tag.</summary>
    public IReadOnlyList<string> TagSuggestions { get; private set; } = [];

    /// <summary>Loads (or reloads) the editor field snapshot for the item shown on the page.</summary>
    public async Task LoadAsync(
        Guid editorEntityId,
        string? mediaType,
        string? initialScope,
        Guid? profileId,
        CancellationToken ct = default)
    {
        Loading = true;
        LoadError = null;
        _subject = null;
        Changed?.Invoke();
        try
        {
            _mediaType = mediaType;
            _profileId = profileId;
            EditorEntityId = editorEntityId;
            _context = await _api.GetMediaEditorContextAsync(editorEntityId, ct);
            if (_context is null)
            {
                LoadError = "This item's editable fields could not be loaded.";
                Loaded = false;
                return;
            }

            var scopeId = !string.IsNullOrWhiteSpace(initialScope) ? initialScope : _context.InitialScope;
            var scopes = _context.Scopes.OrderBy(scope => scope.Order).ToList();
            _scope = scopes.FirstOrDefault(scope => string.Equals(scope.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase))
                     ?? scopes.FirstOrDefault();

            _preferences = null;
            _profileOverrides = null;
            if (_scope is not null && profileId is { } profile)
            {
                _preferences = await _api.GetItemEditorPreferencesAsync(_scope.FieldEntityId, profile, ct);
                _profileOverrides = ProfileOverridesFrom(_preferences);
            }

            Loaded = _scope is not null;
            if (!Loaded)
            {
                LoadError = "This item has nothing editable here.";
            }

            Rebuild();
            // The library's genres and tags do not depend on the item: switching items (spec D7) reuses the lists, so it stays quick.
            if (!_suggestionsLoaded)
            {
                await LoadSuggestionsAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The page moved on while the snapshot was loading; there is nothing to show or report.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The editor could not load the fields for {EntityId}", editorEntityId);
            LoadError = "This item's editable fields could not be loaded.";
            Loaded = false;
        }
        finally
        {
            Loading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Loads a person, Series or curated collection (universal editor spec D17) in place of a library title.</summary>
    public Task LoadSubjectAsync(Guid id, CancellationToken ct = default) =>
        LoadSubjectCoreAsync(id, token => _api.GetEditorSubjectAsync(id, token), ct);

    /// <summary>Loads a Universe, found by its Wikidata ID (spec D17a), in place of a library title.</summary>
    public Task LoadUniverseSubjectAsync(string qid, CancellationToken ct = default) =>
        LoadSubjectCoreAsync(Guid.Empty, token => _api.GetUniverseEditorSubjectAsync(qid, token), ct);

    private async Task LoadSubjectCoreAsync(Guid requestedId, Func<CancellationToken, Task<EditorSubjectDto?>> read, CancellationToken ct)
    {
        Loading = true;
        LoadError = null;
        Changed?.Invoke();
        try
        {
            var subject = await read(ct);
            if (subject is null)
            {
                _subject = null;
                Loaded = false;
                LoadError = "This cannot be opened for editing right now.";
                return;
            }

            ApplySubject(subject);
            if (subject.Fields.Any(field => field.InputKind == EditorSubjectInputKinds.Chips))
            {
                await LoadSuggestionsAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The page moved on while the subject was loading; there is nothing to show or report.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The editor could not load {EntityId}", requestedId);
            _subject = null;
            LoadError = "This cannot be opened for editing right now.";
            Loaded = false;
        }
        finally
        {
            Loading = false;
            Changed?.Invoke();
        }
    }

    // A subject has no editor scopes of its own: a synthetic one carries what the staged editor reads from a scope (the id, the
    // picture owner and slots, whether fields and pictures can be changed), and the Engine's field list becomes the presentation.
    private void ApplySubject(EditorSubjectDto subject)
    {
        _subject = subject;
        _mediaType = subject.Kind;
        _preferences = null;
        _profileOverrides = null;
        EditorEntityId = subject.Id;
        _context = new MediaEditorContextDto
        {
            LaunchEntityId = subject.Id,
            LaunchEntityKind = subject.ArtworkEntityKind,
            MediaType = subject.Kind,
            InitialScope = subject.Kind,
        };
        _scope = new MediaEditorScopeDto
        {
            ScopeId = subject.Kind,
            Label = subject.Kind,
            FieldEntityId = subject.Id,
            FieldEntityKind = subject.ArtworkEntityKind,
            ArtworkOwnerEntityId = subject.Id,
            ArtworkOwnerEntityKind = subject.ArtworkEntityKind,
            DisplayTitle = subject.Title,
            CanEditFields = true,
            CanEditArtwork = subject.ArtworkSlots.Count > 0,
            ArtworkSlots = [.. subject.ArtworkSlots],
        };

        var canonicalName = subject.Kind == EditorSubjectKinds.Universe ? "canonical" : null;
        var rows = subject.Fields.Select(field => SubjectField(field, canonicalName)).ToList();
        var facts = subject.Facts.Select(fact => new MediaEditorDetailsFieldPresentation(
            fact.Label.ToLowerInvariant().Replace(' ', '_'), string.Empty, fact.Label, fact.Value, string.Empty, "text", "Provider",
            fact.SourceLabel, null, false, false, false, 3, fact.Value, fact.Value, false)).ToList();
        var heading = rows.FirstOrDefault();
        var synopsis = rows.Skip(1).FirstOrDefault(row => row.InputKind == "textarea");
        _presentation = new MediaEditorDetailsPresentation
        {
            Heading = heading,
            Synopsis = synopsis,
            Fields = rows.Where(row => row != heading && row != synopsis).ToList(),
            Facts = facts,
        };
        Loaded = true;
        LoadError = null;
    }

    private static MediaEditorDetailsFieldPresentation SubjectField(EditorSubjectFieldDto field, string? canonicalName) => new(
        field.Key,
        field.Key,
        field.Label,
        field.Value,
        string.Empty,
        field.InputKind == EditorSubjectInputKinds.TextArea ? "textarea" : "text",
        field.IsOverridden ? "Override" : "Canonical",
        field.SourceLabel,
        null,
        true,
        field.IsOverridden,
        field.InputKind == EditorSubjectInputKinds.Chips,
        1,
        field.Value,
        field.Value,
        false,
        CanonicalName: canonicalName);

    private async Task LoadSuggestionsAsync(CancellationToken ct)
    {
        try
        {
            var genres = _api.GetItemEditorSuggestionsAsync("genre", null, ct);
            var tags = _api.GetItemEditorSuggestionsAsync("tags", null, ct);
            await Task.WhenAll(genres, tags);
            GenreSuggestions = genres.Result;
            TagSuggestions = tags.Result;
            _suggestionsLoaded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Suggestions only help typing; the fields work without them.
            _logger?.LogDebug(ex, "Editor suggestions were unavailable");
            GenreSuggestions = [];
            TagSuggestions = [];
        }
    }

    /// <summary>Finds the first of the given keys that the active scope presents (editable or not).</summary>
    public MediaEditorDetailsFieldPresentation? Find(params string[] keys)
    {
        foreach (var key in keys)
        {
            var field = AllFields().FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>All presented fields once each: heading, synopsis, then the editor's primary and additional fields.</summary>
    public IReadOnlyList<MediaEditorDetailsFieldPresentation> AllFields() => EnumerateFields().ToList();

    /// <summary>
    /// What the Match tool starts from: the item, the kind of record it is looked up as and the details it holds now. Null until the
    /// fields have loaded.
    /// </summary>
    public EditorMatchSubject? MatchSubject()
    {
        if (!Loaded || _scope is null)
        {
            return null;
        }

        var rows = AllFields();
        string? Pick(params string[] keys) => keys
            .Select(key => Find(key))
            .Select(field => field?.DisplayValue ?? field?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        var draft = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var value = row.RawValue ?? row.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                draft[row.Key] = value;
            }
        }

        var overridden = rows
            .Where(row => row.CanRevert)
            .Select(row => string.IsNullOrWhiteSpace(row.OverrideKey) ? row.Key : row.OverrideKey)
            .ToList();
        var mediaType = ReviewTargetResolver.NormalizeMediaType(_context?.MediaType ?? _mediaType) switch
        {
            "Movie" => "Movies",
            "Audiobook" => "Audiobooks",
            var other => other,
        };
        var group = string.IsNullOrWhiteSpace(_scope.CanonicalTargetGroup) ? _scope.ScopeId : _scope.CanonicalTargetGroup;
        var title = Pick("title") ?? _scope.DisplayTitle;
        return new EditorMatchSubject(
            _scope.FieldEntityId,
            mediaType,
            _scope.ScopeId,
            group,
            title,
            Pick("artist", "author", "director", "creator"),
            Pick("album", "show_name", "series"),
            Pick("year", "original_release_year", "release_year", "publication_year"),
            draft,
            overridden);
    }

    /// <summary>The empty Tags row the editor offers when no tags exist yet and tags can be added.</summary>
    public MediaEditorDetailsFieldPresentation? EmptyTagsField => _presentation.CanAddTags
        ? new MediaEditorDetailsFieldPresentation(
            "custom_tags", "custom_tags", "Tags", string.Empty, "Label", "text", "Override", "Library override", null,
            true, false, true, 2, string.Empty, string.Empty, false)
        : null;

    private IEnumerable<MediaEditorDetailsFieldPresentation> EnumerateFields()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<MediaEditorDetailsFieldPresentation?> { _presentation.Heading, _presentation.Synopsis };
        ordered.AddRange(_presentation.Fields);
        ordered.AddRange(_presentation.AdditionalFields);
        ordered.AddRange(_presentation.Facts);
        foreach (var field in ordered)
        {
            if (field is not null && seen.Add(field.Key))
            {
                yield return field;
            }
        }
    }

    private void Rebuild()
    {
        if (_subject is not null)
        {
            // A subject's presentation comes whole from the Engine's answer; there is no scope snapshot to rebuild it from.
            return;
        }

        if (_scope is null || _context is null)
        {
            _presentation = new();
            return;
        }

        var scopes = _context.Scopes.ToDictionary(scope => scope.ScopeId, StringComparer.OrdinalIgnoreCase);
        var mediaType = !string.IsNullOrWhiteSpace(_context.MediaType) ? _context.MediaType : _mediaType;
        _presentation = MediaEditorDetailsPresenter.Build(_scope, scopes, mediaType, draft: null, _profileOverrides);
    }

    private static IReadOnlyDictionary<string, string>? ProfileOverridesFrom(ItemEditorPreferencesResponse? preferences) =>
        preferences?.DisplayOverrides
            .Where(pair => !MediaEditorTagRules.IsLibraryTagsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}
