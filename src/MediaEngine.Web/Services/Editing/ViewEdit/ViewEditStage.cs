using System.Globalization;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Web.Services.Editing;

/// <summary>The kinds of file a View item has, as the Engine names them.</summary>
public static class ViewMediaKinds
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Document = "document";
}

/// <summary>How the date taken of the chosen photos or videos changes.</summary>
public enum ViewDateMode
{
    None,

    /// <summary>Every item gets this one date.</summary>
    Set,

    /// <summary>Every item's own date moves by the same amount (a camera with the wrong clock).</summary>
    Shift,

    /// <summary>Put back what the camera recorded ("Use original").</summary>
    Reset,
}

/// <summary>How the place of the chosen photos or videos changes.</summary>
public enum ViewPlaceMode
{
    None,
    Set,
    Reset,
}

/// <summary>A document field the editor stages (single-item identifying fields are never offered for a group).</summary>
public static class ViewDocumentFieldKeys
{
    public const string Title = "title";
    public const string Correspondent = "correspondent";
    public const string Type = "type";
    public const string Date = "date";
    public const string ArchiveNumber = "archive_number";
    public const string Notes = "notes";
}

/// <summary>
/// What the person has changed in the photos, videos and documents editor, held in memory until Save changes (spec photos-documents-editor
/// D3, D8). Pure: no calls to the Engine. It says which fields a group may take, counts the changes for the footer, writes the rows Review changes
/// shows, and builds the one request Save sends. Typing never reaches the Engine.
/// </summary>
public sealed class ViewEditStage
{
    private readonly Dictionary<string, (bool Set, string? Value)> _document = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ViewDocumentFieldValueRequest> _fields = [];

    public ViewEditStage(IReadOnlyList<LocalAssetDto> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
    }

    public IReadOnlyList<LocalAssetDto> Items { get; }

    public bool IsGroup => Items.Count > 1;

    /// <summary>True when every chosen item is a document.</summary>
    public bool IsDocuments => Items.Count > 0 && Items.All(IsDocument);

    /// <summary>True when every chosen item is a photo or a video.</summary>
    public bool IsMedia => Items.Count > 0 && Items.All(item => !IsDocument(item));

    public static bool IsDocument(LocalAssetDto item) => string.Equals(item.MediaKind, ViewMediaKinds.Document, StringComparison.Ordinal);

    public event Action? Changed;

    // ---- description (one item only) ----

    public bool DescriptionStaged { get; private set; }

    public string? Description { get; private set; }

    public bool CanEditDescription => !IsGroup && IsMedia;

    public void SetDescription(string? value)
    {
        if (!CanEditDescription)
        {
            return;
        }

        var original = Items[0].Description ?? string.Empty;
        var next = (value ?? string.Empty).Trim();
        DescriptionStaged = !string.Equals(original, next, StringComparison.Ordinal);
        Description = next.Length == 0 ? null : next;
        Raise();
    }

    // ---- tags and people ----

    public HashSet<string> TagsAdd { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> TagsRemove { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> PeopleAdd { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- Locks (universal editor v5 D25, the same locks as the library editor) ----
    // What is read from the file (Date taken, Place, and People on a single item) starts locked: the form shows it but does not take typing until
    // Unlock is pressed. A group's date is a shift and its people are "add", so only its Place starts locked. Unlocking stages nothing.
    public const string LockDate = "date";
    public const string LockPlace = "place";
    public const string LockPeople = "people";

    private readonly HashSet<string> _unlocked = new(StringComparer.Ordinal);

    public void Unlock(string key) => _unlocked.Add(key);

    public void Relock(string key) => _unlocked.Remove(key);

    /// <summary>True when the field starts locked for this stage and has not been unlocked or changed.</summary>
    public bool IsLocked(string key) => IsMedia && StartsLocked(key) && !_unlocked.Contains(key) && !IsChanged(key);

    /// <summary>True when the person has a staged change on the field (the lock gives way to "kept" and Revert).</summary>
    public bool IsChanged(string key) => key switch
    {
        LockDate => DateMode != ViewDateMode.None,
        LockPlace => PlaceMode != ViewPlaceMode.None,
        LockPeople => PeopleAdd.Count + PeopleRemove.Count > 0,
        _ => false,
    };

    private bool StartsLocked(string key) => key == LockPlace || (!IsGroup && key is LockDate or LockPeople);

    public HashSet<string> PeopleRemove { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tags on any chosen item, with how many items carry each (a group shows "on 12 of 40").</summary>
    public IReadOnlyList<(string Name, int Count)> CurrentTags() => Count(Items.SelectMany(item => item.Tags.Distinct(StringComparer.OrdinalIgnoreCase)));

    public IReadOnlyList<(string Name, int Count)> CurrentPeople() => Count(Items.SelectMany(item => (item.People ?? []).Distinct(StringComparer.OrdinalIgnoreCase)));

    /// <summary>What the tags will be after Save: the tags now, plus the added, minus the removed (a group shows the union).</summary>
    public IReadOnlyList<string> ShownTags() => Merge(CurrentTags().Select(t => t.Name), TagsAdd, TagsRemove);

    public IReadOnlyList<string> ShownPeople() => Merge(CurrentPeople().Select(t => t.Name), PeopleAdd, PeopleRemove);

    public void AddTag(string name) => Add(name, TagsAdd, TagsRemove, CurrentTags());

    public void RemoveTag(string name) => Remove(name, TagsAdd, TagsRemove, CurrentTags());

    public void AddPerson(string name) => Add(name, PeopleAdd, PeopleRemove, CurrentPeople());

    public void RemovePerson(string name) => Remove(name, PeopleAdd, PeopleRemove, CurrentPeople());

    public void RevertDescription()
    {
        DescriptionStaged = false;
        Description = null;
        Raise();
    }

    public void RevertTags()
    {
        TagsAdd.Clear();
        TagsRemove.Clear();
        Raise();
    }

    public void RevertPeople()
    {
        PeopleAdd.Clear();
        PeopleRemove.Clear();
        Raise();
    }

    // ---- date taken ----

    public ViewDateMode DateMode { get; private set; }

    public DateTimeOffset? DateValue { get; private set; }

    public TimeSpan DateShift { get; private set; }

    public void SetDate(DateTimeOffset value)
    {
        DateMode = ViewDateMode.Set;
        DateValue = value;
        DateShift = TimeSpan.Zero;
        Raise();
    }

    public void ShiftDate(TimeSpan amount)
    {
        if (amount == TimeSpan.Zero)
        {
            RevertDate();
            return;
        }

        DateMode = ViewDateMode.Shift;
        DateShift = amount;
        DateValue = null;
        Raise();
    }

    /// <summary>"Use original": put back what the camera recorded.</summary>
    public void UseOriginalDate()
    {
        DateMode = ViewDateMode.Reset;
        DateValue = null;
        DateShift = TimeSpan.Zero;
        Raise();
    }

    public void RevertDate()
    {
        DateMode = ViewDateMode.None;
        DateValue = null;
        DateShift = TimeSpan.Zero;
        Raise();
    }

    // ---- place ----

    public ViewPlaceMode PlaceMode { get; private set; }

    public double? Latitude { get; private set; }

    public double? Longitude { get; private set; }

    public string? PlaceName { get; private set; }

    public void SetPlace(double latitude, double longitude, string? name)
    {
        PlaceMode = ViewPlaceMode.Set;
        Latitude = latitude;
        Longitude = longitude;
        PlaceName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Raise();
    }

    public void UseOriginalPlace()
    {
        PlaceMode = ViewPlaceMode.Reset;
        Latitude = Longitude = null;
        PlaceName = null;
        Raise();
    }

    public void RevertPlace()
    {
        PlaceMode = ViewPlaceMode.None;
        Latitude = Longitude = null;
        PlaceName = null;
        Raise();
    }

    // ---- galleries ----

    public HashSet<Guid> GalleriesAdd { get; } = [];

    public HashSet<Guid> GalleriesRemove { get; } = [];

    public string? NewGalleryName { get; private set; }

    /// <summary>Names of the galleries staged to add or remove, for Review (filled by the editor from the gallery list).</summary>
    public Dictionary<Guid, string> GalleryNames { get; } = [];

    public void AddToGallery(Guid id, string name)
    {
        GalleryNames[id] = name;
        GalleriesRemove.Remove(id);
        GalleriesAdd.Add(id);
        Raise();
    }

    public void RemoveFromGallery(Guid id, string name)
    {
        GalleryNames[id] = name;
        GalleriesAdd.Remove(id);
        GalleriesRemove.Add(id);
        Raise();
    }

    public void CancelGallery(Guid id)
    {
        GalleriesAdd.Remove(id);
        GalleriesRemove.Remove(id);
        Raise();
    }

    public void CreateGallery(string? name)
    {
        NewGalleryName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Raise();
    }

    // ---- document fields ----

    public bool IsDocumentFieldStaged(string key) => _document.TryGetValue(key, out var entry) && entry.Set;

    public string? DocumentFieldValue(string key) => _document.TryGetValue(key, out var entry) ? entry.Value : null;

    /// <summary>True for a field a group may take (Correspondent, Type and Date); the identifying fields are single-item only.</summary>
    public bool CanEditDocumentField(string key) =>
        IsDocuments && (!IsGroup || key is ViewDocumentFieldKeys.Correspondent or ViewDocumentFieldKeys.Type or ViewDocumentFieldKeys.Date);

    public void SetDocumentField(string key, string? value)
    {
        if (!CanEditDocumentField(key))
        {
            return;
        }

        var next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var original = OriginalDocumentField(key);
        if (IsGroup ? next is null && original is null : string.Equals(original ?? string.Empty, next ?? string.Empty, StringComparison.Ordinal))
        {
            _document.Remove(key);
        }
        else
        {
            _document[key] = (true, next);
        }

        Raise();
    }

    public void RevertDocumentField(string key)
    {
        if (_document.Remove(key))
        {
            Raise();
        }
    }

    public void SetCustomField(ViewDocumentFieldValueRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _fields[value.FieldId] = value;
        Raise();
    }

    public void RevertCustomField(Guid fieldId)
    {
        if (_fields.Remove(fieldId))
        {
            Raise();
        }
    }

    public bool IsCustomFieldStaged(Guid fieldId) => _fields.ContainsKey(fieldId);

    public ViewDocumentFieldValueRequest? StagedCustomField(Guid fieldId) => _fields.GetValueOrDefault(fieldId);

    /// <summary>The value the single (or first) chosen document has now, as text, for a document field.</summary>
    public string? OriginalDocumentField(string key) => Items.Count == 0 || Items[0].DocumentInfo is not { } info ? null : key switch
    {
        ViewDocumentFieldKeys.Title => Items[0].Title,
        ViewDocumentFieldKeys.Correspondent => info.Correspondent?.Name,
        ViewDocumentFieldKeys.Type => info.DocumentType?.Name,
        ViewDocumentFieldKeys.Date => info.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ViewDocumentFieldKeys.ArchiveNumber => info.ArchiveNumber,
        ViewDocumentFieldKeys.Notes => info.Notes,
        _ => null,
    };

    /// <summary>True when the chosen documents do not all share this field's value (the editor then shows "Mixed, leave unchanged").</summary>
    public bool IsMixed(string key) => IsGroup && Items.Select(item => FieldOf(item, key) ?? string.Empty).Distinct(StringComparer.Ordinal).Count() > 1;

    private static string? FieldOf(LocalAssetDto item, string key) => item.DocumentInfo is not { } info ? null : key switch
    {
        ViewDocumentFieldKeys.Correspondent => info.Correspondent?.Name,
        ViewDocumentFieldKeys.Type => info.DocumentType?.Name,
        ViewDocumentFieldKeys.Date => info.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => null,
    };

    // ---- the whole ----

    /// <summary>How many things Save would do: one per changed field (tags, people, date, place, galleries and each document field count once).</summary>
    public int ChangeCount => Rows().Count;

    public bool HasChanges => ChangeCount > 0;

    /// <summary>Throws everything away.</summary>
    public void Clear()
    {
        DescriptionStaged = false;
        Description = null;
        TagsAdd.Clear();
        TagsRemove.Clear();
        PeopleAdd.Clear();
        PeopleRemove.Clear();
        DateMode = ViewDateMode.None;
        DateValue = null;
        DateShift = TimeSpan.Zero;
        PlaceMode = ViewPlaceMode.None;
        Latitude = Longitude = null;
        PlaceName = null;
        GalleriesAdd.Clear();
        GalleriesRemove.Clear();
        NewGalleryName = null;
        _document.Clear();
        _fields.Clear();
        Raise();
    }

    /// <summary>The one request Save sends: every staged field, for every chosen item.</summary>
    public ViewItemsEditRequest ToRequest()
    {
        ViewItemsDocumentEdit? document = null;
        if (IsDocuments && (_document.Count > 0 || _fields.Count > 0))
        {
            document = new ViewItemsDocumentEdit(
                SetTitle: IsDocumentFieldStaged(ViewDocumentFieldKeys.Title),
                Title: DocumentFieldValue(ViewDocumentFieldKeys.Title),
                SetCorrespondent: IsDocumentFieldStaged(ViewDocumentFieldKeys.Correspondent),
                Correspondent: DocumentFieldValue(ViewDocumentFieldKeys.Correspondent),
                SetDocumentType: IsDocumentFieldStaged(ViewDocumentFieldKeys.Type),
                DocumentType: DocumentFieldValue(ViewDocumentFieldKeys.Type),
                SetDocumentDate: IsDocumentFieldStaged(ViewDocumentFieldKeys.Date),
                DocumentDate: DateOnly.TryParse(DocumentFieldValue(ViewDocumentFieldKeys.Date), CultureInfo.InvariantCulture, out var date) ? date : null,
                SetArchiveNumber: IsDocumentFieldStaged(ViewDocumentFieldKeys.ArchiveNumber),
                ArchiveNumber: DocumentFieldValue(ViewDocumentFieldKeys.ArchiveNumber),
                SetNotes: IsDocumentFieldStaged(ViewDocumentFieldKeys.Notes),
                Notes: DocumentFieldValue(ViewDocumentFieldKeys.Notes),
                FieldValues: [.. _fields.Values]);
        }

        return new ViewItemsEditRequest(
            [.. Items.Select(item => item.Id)],
            SetDescription: DescriptionStaged,
            Description: Description,
            TagsAdd: TagsAdd.Count > 0 ? [.. TagsAdd] : null,
            TagsRemove: TagsRemove.Count > 0 ? [.. TagsRemove] : null,
            PeopleAdd: PeopleAdd.Count > 0 ? [.. PeopleAdd] : null,
            PeopleRemove: PeopleRemove.Count > 0 ? [.. PeopleRemove] : null,
            CapturedAt: DateMode == ViewDateMode.Set ? DateValue : null,
            CapturedShiftSeconds: DateMode == ViewDateMode.Shift ? (long)DateShift.TotalSeconds : null,
            CapturedReset: DateMode == ViewDateMode.Reset,
            Location: PlaceMode switch
            {
                ViewPlaceMode.Set => new UpdateLocalAssetLocationRequest(Latitude, Longitude, PlaceName),
                ViewPlaceMode.Reset => new UpdateLocalAssetLocationRequest(null, null, ResetToEmbedded: true),
                _ => null,
            },
            GalleryAddIds: GalleriesAdd.Count > 0 ? [.. GalleriesAdd] : null,
            GalleryRemoveIds: GalleriesRemove.Count > 0 ? [.. GalleriesRemove] : null,
            CreateGalleryName: NewGalleryName,
            Document: document);
    }

    /// <summary>The rows Review changes shows: one per changed field; a group's rows name the whole group ("40 photos").</summary>
    public IReadOnlyList<EditReviewRow> Rows()
    {
        var rows = new List<EditReviewRow>();
        var who = ItemLabel();
        var id = Items.Count == 1 ? Items[0].Id : Guid.Empty;

        void Add(string key, string what, string? before, string after, string? warning = null) =>
            rows.Add(new EditReviewRow("view:" + key, null, id, EditSessionChangeKinds.Details, EditReviewGroup.Metadata, who, what, before ?? EditReviewRows.AsItIsNow, after, warning));

        if (DescriptionStaged)
        {
            Add("description", "Description", Items[0].Description, Description ?? "(empty)");
        }

        if (TagsAdd.Count + TagsRemove.Count > 0)
        {
            Add("tags", "Tags", IsGroup ? null : Join(CurrentTags().Select(t => t.Name)), ListChange(TagsAdd, TagsRemove, ShownTags()));
        }

        if (PeopleAdd.Count + PeopleRemove.Count > 0)
        {
            Add("people", "People", IsGroup ? null : Join(CurrentPeople().Select(t => t.Name)), ListChange(PeopleAdd, PeopleRemove, ShownPeople()));
        }

        if (DateMode != ViewDateMode.None)
        {
            var before = IsGroup ? null : Items[0].CapturedAt?.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture);
            var after = DateMode switch
            {
                ViewDateMode.Set => DateValue?.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture) ?? "(none)",
                ViewDateMode.Shift => "Moved " + ShiftText(DateShift),
                _ => "What the camera recorded",
            };
            Add("date", "Date taken", before, after, DateMoveWarning());
        }

        if (PlaceMode != ViewPlaceMode.None)
        {
            Add("place", "Place", IsGroup ? null : Items[0].LocationName,
                PlaceMode == ViewPlaceMode.Set ? PlaceName ?? $"{Latitude:0.####}, {Longitude:0.####}" : "What the camera recorded");
        }

        if (GalleriesAdd.Count + GalleriesRemove.Count > 0 || NewGalleryName is not null)
        {
            var added = GalleriesAdd.Select(Name).Concat(NewGalleryName is null ? [] : [$"New gallery \"{NewGalleryName}\""]).ToList();
            var removed = GalleriesRemove.Select(Name).ToList();
            Add("galleries", "Galleries", null, string.Join("; ", new[]
            {
                added.Count > 0 ? "Add to " + string.Join(", ", added) : null,
                removed.Count > 0 ? "Remove from " + string.Join(", ", removed) : null,
            }.Where(part => part is not null)));
        }

        foreach (var key in new[]
                 {
                     ViewDocumentFieldKeys.Title, ViewDocumentFieldKeys.Correspondent, ViewDocumentFieldKeys.Type, ViewDocumentFieldKeys.Date,
                     ViewDocumentFieldKeys.ArchiveNumber, ViewDocumentFieldKeys.Notes,
                 })
        {
            if (IsDocumentFieldStaged(key))
            {
                Add("doc:" + key, DocumentLabel(key), IsGroup ? null : OriginalDocumentField(key), DocumentFieldValue(key) ?? "(empty)");
            }
        }

        foreach (var field in _fields.Values)
        {
            Add("field:" + field.FieldId.ToString("N"), "Field", null, field.TextValue ?? field.NumberValue?.ToString(CultureInfo.InvariantCulture)
                ?? field.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (field.BoolValue is { } flag ? (flag ? "Yes" : "No") : "(empty)"));
        }

        return rows;
    }

    /// <summary>The item or group Review and the header name: the file name until a photo has a description, "40 photos" for a group.</summary>
    public string ItemLabel()
    {
        if (Items.Count == 1)
        {
            return DisplayTitle(Items[0]);
        }

        return $"{Items.Count} {GroupNoun()}";
    }

    public string GroupNoun() => IsDocuments ? "documents" : Items.All(item => item.MediaKind == ViewMediaKinds.Video) ? "videos"
        : Items.All(item => item.MediaKind == ViewMediaKinds.Image) ? "photos" : "photos and videos";

    public static string DisplayTitle(LocalAssetDto item) =>
        !string.IsNullOrWhiteSpace(item.Title) ? item.Title! : !string.IsNullOrWhiteSpace(item.Description) && !IsDocument(item) ? item.Description! : item.FileName;

    private string Name(Guid id) => GalleryNames.TryGetValue(id, out var name) ? name : "a gallery";

    private static string DocumentLabel(string key) => key switch
    {
        ViewDocumentFieldKeys.Title => "Title",
        ViewDocumentFieldKeys.Correspondent => "Correspondent",
        ViewDocumentFieldKeys.Type => "Type",
        ViewDocumentFieldKeys.Date => "Date",
        ViewDocumentFieldKeys.ArchiveNumber => "Archive number",
        _ => "Notes",
    };

    /// <summary>Shifting dates can move photos onto another day of the Timeline; Review says how many.</summary>
    private string? DateMoveWarning()
    {
        var moved = 0;
        foreach (var item in Items)
        {
            if (item.CapturedAt is not { } was)
            {
                continue;
            }

            var next = DateMode switch
            {
                ViewDateMode.Shift => was + DateShift,
                ViewDateMode.Set => DateValue ?? was,
                _ => item.EmbeddedCapturedAt ?? was,
            };
            if (DateOnly.FromDateTime(was.DateTime) != DateOnly.FromDateTime(next.DateTime))
            {
                moved++;
            }
        }

        return moved == 0 ? null : moved == 1 ? "This moves 1 photo to another day in the Timeline." : $"This moves {moved} photos to other days in the Timeline.";
    }

    /// <summary>The new date of an item after this stage (what Review and tests compare to the Engine's answer).</summary>
    public DateTimeOffset? NewDateOf(LocalAssetDto item) => DateMode switch
    {
        ViewDateMode.Shift => item.CapturedAt is { } at ? at + DateShift : null,
        ViewDateMode.Set => DateValue,
        ViewDateMode.Reset => item.EmbeddedCapturedAt,
        _ => item.CapturedAt,
    };

    public static string ShiftText(TimeSpan amount)
    {
        var sign = amount < TimeSpan.Zero ? "back" : "forward";
        var abs = amount.Duration();
        var parts = new List<string>();
        if (abs.Days > 0) { parts.Add($"{abs.Days} {(abs.Days == 1 ? "day" : "days")}"); }
        if (abs.Hours > 0) { parts.Add($"{abs.Hours} {(abs.Hours == 1 ? "hour" : "hours")}"); }
        if (abs.Minutes > 0) { parts.Add($"{abs.Minutes} {(abs.Minutes == 1 ? "minute" : "minutes")}"); }
        return parts.Count == 0 ? "by no time" : $"{string.Join(" ", parts)} {sign}";
    }

    private static string ListChange(IEnumerable<string> added, IEnumerable<string> removed, IReadOnlyList<string> after)
    {
        var parts = new List<string>();
        var plus = added.ToList();
        var minus = removed.ToList();
        if (plus.Count > 0) { parts.Add("add " + string.Join(", ", plus)); }
        if (minus.Count > 0) { parts.Add("remove " + string.Join(", ", minus)); }
        return string.Join("; ", parts);
    }

    private static string Join(IEnumerable<string> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "None" : string.Join(", ", list);
    }

    private static List<(string, int)> Count(IEnumerable<string> names) =>
        [.. names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Select(group => (group.First(), group.Count())).OrderBy(pair => pair.Item1, StringComparer.OrdinalIgnoreCase)];

    private static List<string> Merge(IEnumerable<string> current, HashSet<string> add, HashSet<string> remove)
    {
        var list = current.Where(name => !remove.Contains(name)).ToList();
        list.AddRange(add.Where(name => !list.Contains(name, StringComparer.OrdinalIgnoreCase)));
        return list;
    }

    private void Add(string name, HashSet<string> add, HashSet<string> remove, IReadOnlyList<(string Name, int Count)> current)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            return;
        }

        remove.Remove(clean);
        // Adding something every chosen item already has changes nothing.
        if (!current.Any(entry => string.Equals(entry.Name, clean, StringComparison.OrdinalIgnoreCase) && entry.Count == Items.Count))
        {
            add.Add(clean);
        }

        Raise();
    }

    private void Remove(string name, HashSet<string> add, HashSet<string> remove, IReadOnlyList<(string Name, int Count)> current)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            return;
        }

        add.Remove(clean);
        if (current.Any(entry => string.Equals(entry.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            remove.Add(clean);
        }

        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
