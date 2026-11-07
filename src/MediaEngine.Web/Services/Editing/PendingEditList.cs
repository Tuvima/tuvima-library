using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The changes staged while editing, held in memory until Save changes (universal editor spec D3 and section 5). One entry per
/// item and field, per item and artwork slot, or per item and identity, relationship or file move. Staging the same thing again
/// replaces the earlier entry, so the list always says what Save would do. Nothing here is stored: closing the browser loses it,
/// which the unsaved-changes guard is there to prevent.
/// </summary>
public sealed class PendingEditList
{
    private readonly List<EditSessionChange> _changes = [];
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _before = new(StringComparer.Ordinal);

    public event Action? Changed;

    public IReadOnlyList<EditSessionChange> Changes => _changes;

    public int Count => _changes.Count;

    public bool IsEmpty => _changes.Count == 0;

    /// <summary>How many things Save would do: one per staged field, and one per artwork, identity, relationship or file-move entry.</summary>
    public int ChangeCount => _changes.Sum(change => change.Kind switch
    {
        EditSessionChangeKinds.Details => Math.Max(1, change.Fields.Count),
        EditSessionChangeKinds.Bookmarks => Math.Max(1, change.Bookmarks?.Ops.Count ?? 0),
        _ => 1,
    });

    /// <summary>Stages one details field for an item, replacing an earlier value staged for the same field.</summary>
    public void SetField(Guid entityId, string key, string? value, string? before = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var change = Find(EditSessionChangeKinds.Details, entityId);
        var fields = change?.Fields.Where(f => !SameKey(f.Key, key)).ToList() ?? [];
        fields.Add(new EditSessionFieldChange { Key = key.Trim(), Value = value });
        var next = new EditSessionChange
        {
            Id = change?.Id ?? NewId(),
            Kind = EditSessionChangeKinds.Details,
            EntityId = entityId,
            Fields = fields,
        };
        Replace(change, next);
        RememberBefore(RowKey(next.Id, key.Trim()), before);
    }

    /// <summary>Takes one staged details field back out. The item's entry goes when its last field does.</summary>
    public bool ClearField(Guid entityId, string key)
    {
        var change = Find(EditSessionChangeKinds.Details, entityId);
        if (change is null || !change.Fields.Any(f => SameKey(f.Key, key)))
        {
            return false;
        }

        var fields = change.Fields.Where(f => !SameKey(f.Key, key)).ToList();
        if (fields.Count == 0)
        {
            Remove(change.Id);
            return true;
        }

        Replace(change, new EditSessionChange { Id = change.Id, Kind = change.Kind, EntityId = entityId, Fields = fields });
        _before.Remove(RowKey(change.Id, key.Trim()));
        return true;
    }

    /// <summary>The staged value for a details field, or false when nothing is staged for it.</summary>
    public bool TryGetField(Guid entityId, string key, out string? value)
    {
        var field = Find(EditSessionChangeKinds.Details, entityId)?.Fields.FirstOrDefault(f => SameKey(f.Key, key));
        value = field?.Value;
        return field is not null;
    }

    /// <summary>
    /// Stages an artwork choice for one slot of an item (target is the picked picture), replacing an earlier choice for it.
    /// <paramref name="label"/> is an optional name for the picture, shown in Review changes; it is never sent to the Engine.
    /// </summary>
    public void SetArtwork(Guid entityId, string slot, string targetId, string? label = null, string? before = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var existing = _changes.FirstOrDefault(c =>
            c.Kind == EditSessionChangeKinds.Artwork && c.EntityId == entityId && string.Equals(c.Slot, slot, StringComparison.OrdinalIgnoreCase));
        Replace(existing, new EditSessionChange
        {
            Id = existing?.Id ?? NewId(),
            Kind = EditSessionChangeKinds.Artwork,
            EntityId = entityId,
            Slot = slot,
            TargetId = targetId,
        }, label, before: before);
    }

    /// <summary>
    /// Stages an identity, relationship or file-move change; one per item and kind, a later one replacing the earlier.
    /// <paramref name="label"/> is an optional name for the chosen record or destination, shown in Review changes; it is never sent to the Engine.
    /// <paramref name="note"/> is an optional plain warning Review shows on the row (for example "Not on this album"); also never sent.
    /// </summary>
    public void SetTarget(string kind, Guid entityId, string? targetId, string? token = null, string? label = null, string? note = null, string? before = null)
    {
        if (kind is EditSessionChangeKinds.Details or EditSessionChangeKinds.Artwork or EditSessionChangeKinds.ExtraMove or EditSessionChangeKinds.VideoOrder or EditSessionChangeKinds.ThingAdd || !EditSessionChangeKinds.IsKnown(kind))
        {
            throw new ArgumentException("Use SetField, SetArtwork, SetExtraMove, SetVideoOrder or AddThing for details, artwork, Extras, video-order and added-thing changes.", nameof(kind));
        }

        var existing = Find(kind, entityId);
        Replace(existing, new EditSessionChange
        {
            Id = existing?.Id ?? NewId(),
            Kind = kind,
            EntityId = entityId,
            TargetId = targetId,
            Token = token,
        }, label, note, before);
    }

    /// <summary>
    /// Stages moving one Extra to an episode (<paramref name="extraId"/>, with <see cref="EditSessionExtraMove.ToEpisodeToken"/>) or one
    /// episode to Extras (<paramref name="extraId"/> null, token <see cref="EditSessionExtraMove.ToExtrasToken"/>). Unlike the other
    /// kinds there can be several per item, one for each Extra, so they are told apart by <see cref="EditSessionChange.TargetId"/>.
    /// </summary>
    public void SetExtraMove(Guid entityId, string? extraId, string token, string? label = null, string? before = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var existing = FindExtraMove(entityId, extraId);
        Replace(existing, new EditSessionChange
        {
            Id = existing?.Id ?? NewId(),
            Kind = EditSessionChangeKinds.ExtraMove,
            EntityId = entityId,
            TargetId = extraId,
            Token = token,
        }, label, before: before);
    }

    /// <summary>
    /// Stages what a person chose for one file in "What is this file?" (universal editor v4 D20): an Extra of its own title, an Extra of
    /// another title, or ignore. One per item: choosing again replaces the earlier choice. <paramref name="label"/> says what it becomes
    /// ("Trailer · Season 1") and <paramref name="before"/> names the file, both for Review changes only.
    /// </summary>
    public void SetFileChoice(Guid entityId, ExtraFileChoice choice, string label, string? before = null)
    {
        ArgumentNullException.ThrowIfNull(choice);
        SetExtraMove(entityId, null, EditSessionExtraMove.ToFileToken(choice), label, before);
    }

    /// <summary>The staged "What is this file?" choice of one item, or null.</summary>
    public EditSessionChange? FindFileChoice(Guid entityId) =>
        FindExtraMove(entityId, null) is { } change && EditSessionExtraMove.IsFileChoice(change.Token) ? change : null;

    /// <summary>
    /// Stages the order of the videos of one series or section (universal editor v3.1), replacing an earlier order staged for it.
    /// <paramref name="label"/> names the series for Review; <paramref name="before"/> says how it read before. Neither is sent to the Engine.
    /// </summary>
    public void SetVideoOrder(Guid parentId, IEnumerable<Guid> orderedIds, string? label = null, string? before = null)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        var existing = Find(EditSessionChangeKinds.VideoOrder, parentId);
        Replace(existing, new EditSessionChange
        {
            Id = existing?.Id ?? NewId(),
            Kind = EditSessionChangeKinds.VideoOrder,
            EntityId = parentId,
            OrderedIds = [.. orderedIds],
        }, label, before: before);
    }

    /// <summary>
    /// Stages adding a character, place, faction or other thing by hand to a Universe (universal editor v4, WP11). Unlike the other kinds
    /// there can be several for one Universe, told apart by an id made up here. <paramref name="label"/> names the kind ("Faction") for Review
    /// and is never sent to the Engine.
    /// </summary>
    public EditSessionChange AddThing(Guid universeId, string name, string kindKey, string? description, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(kindKey);
        var fields = new List<EditSessionFieldChange>
        {
            new() { Key = EditSessionThingAdd.NameKey, Value = name.Trim() },
            new() { Key = EditSessionThingAdd.KindKey, Value = kindKey.Trim() },
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            fields.Add(new EditSessionFieldChange { Key = EditSessionThingAdd.DescriptionKey, Value = description.Trim() });
        }

        var change = new EditSessionChange
        {
            Id = NewId(),
            Kind = EditSessionChangeKinds.ThingAdd,
            EntityId = universeId,
            TargetId = Guid.NewGuid().ToString("N"),
            Fields = fields,
        };
        Replace(null, change, label);
        return change;
    }

    /// <summary>The things staged to be added to a Universe, in the order they were staged.</summary>
    public IReadOnlyList<EditSessionChange> ThingAdds(Guid universeId) =>
        [.. _changes.Where(change => change.Kind == EditSessionChangeKinds.ThingAdd && change.EntityId == universeId)];

    /// <summary>
    /// Stages one bookmark operation on a title's Everyone's bookmarks (universal editor v5 D37). A title has one bookmarks change holding its
    /// operations in the order they were made; a later operation can cancel or replace an earlier one (see <see cref="EditorBookmarkStaging"/>).
    /// <paramref name="label"/> is the line Review changes shows for the operation ("Added to Everyone's: 12:04 Opening"); it is never sent to the Engine.
    /// </summary>
    public void StageBookmarkOp(Guid workId, EditorBookmarkOp op, string label, string? before = null)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var staged = op with { Id = string.IsNullOrWhiteSpace(op.Id) ? NewId() : op.Id };
        var existing = Find(EditSessionChangeKinds.Bookmarks, workId);
        var changeId = existing?.Id ?? NewId();
        var ops = EditorBookmarkStaging.Stage(existing?.Bookmarks?.Ops ?? [], staged);
        _labels[changeId + ":" + staged.Id] = label.Trim();
        if (!string.IsNullOrWhiteSpace(before))
        {
            _before[changeId + ":" + staged.Id] = before.Trim();
        }

        ReplaceBookmarks(existing, changeId, workId, ops);
    }

    /// <summary>Takes one staged bookmark operation back out (Review's "take this change back out"). The title's change goes when its last operation does.</summary>
    public bool ClearBookmarkOp(Guid workId, string opId)
    {
        var existing = Find(EditSessionChangeKinds.Bookmarks, workId);
        if (existing?.Bookmarks is not { } staged || !staged.Ops.Any(op => string.Equals(op.Id, opId, StringComparison.Ordinal)))
        {
            return false;
        }

        ReplaceBookmarks(existing, existing.Id, workId, EditorBookmarkStaging.Without(staged.Ops, opId));
        return true;
    }

    /// <summary>The bookmark operations staged for one title, in the order they were made.</summary>
    public IReadOnlyList<EditorBookmarkOp> BookmarkOps(Guid workId) =>
        Find(EditSessionChangeKinds.Bookmarks, workId)?.Bookmarks?.Ops ?? [];

    /// <summary>The Review line staged for one bookmark operation, or null.</summary>
    public string? BookmarkLabel(string changeId, string opId) => _labels.GetValueOrDefault(changeId + ":" + opId);

    private void ReplaceBookmarks(EditSessionChange? existing, string changeId, Guid workId, IReadOnlyList<EditorBookmarkOp> ops)
    {
        // Labels of operations that no longer exist (cancelled by a later one) go with them.
        var kept = ops.Select(op => changeId + ":" + op.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _labels.Keys.Where(key => key.StartsWith(changeId + ":", StringComparison.Ordinal) && !kept.Contains(key)).ToList())
        {
            _labels.Remove(key);
        }

        foreach (var key in _before.Keys.Where(key => key.StartsWith(changeId + ":", StringComparison.Ordinal) && !kept.Contains(key)).ToList())
        {
            _before.Remove(key);
        }

        if (ops.Count == 0)
        {
            if (existing is not null)
            {
                _changes.Remove(existing);
                Changed?.Invoke();
            }

            return;
        }

        Replace(existing, new EditSessionChange
        {
            Id = changeId,
            Kind = EditSessionChangeKinds.Bookmarks,
            EntityId = workId,
            Bookmarks = new EditorBookmarkChangeDto { Ops = ops },
        });
    }

    /// <summary>The staged order of one series or section, or null.</summary>
    public EditSessionChange? FindVideoOrder(Guid parentId) => Find(EditSessionChangeKinds.VideoOrder, parentId);

    /// <summary>The staged series change of one video (join, new series or stand alone), or null.</summary>
    public EditSessionChange? FindVideoSeries(Guid videoId) => Find(EditSessionChangeKinds.VideoSeries, videoId);

    /// <summary>The staged "rename the files to match this order" of one series or section, or null.</summary>
    public EditSessionChange? FindVideoRename(Guid parentId) => Find(EditSessionChangeKinds.VideoRename, parentId);

    /// <summary>The staged move of one Extra (or of the episode itself when <paramref name="extraId"/> is null), or null.</summary>
    public EditSessionChange? FindExtraMove(Guid entityId, string? extraId) =>
        _changes.FirstOrDefault(c => c.Kind == EditSessionChangeKinds.ExtraMove
            && c.EntityId == entityId
            && string.Equals(c.TargetId, extraId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name given for a staged identity, relationship or artwork choice, or null when none was given.</summary>
    public string? Label(string changeId) => _labels.GetValueOrDefault(changeId);

    /// <summary>The warning given for a staged change, or null when none was given.</summary>
    public string? Note(string changeId) => _notes.GetValueOrDefault(changeId);

    /// <summary>What the item had before the change, as the staging screen recorded it (a Review row id: the change id, or the change id and field key for a details field); null when it was not recorded.</summary>
    public string? Before(string rowId) => _before.GetValueOrDefault(rowId);

    /// <summary>Removes one staged change by its id (the row's menu in Review changes).</summary>
    public bool Remove(string changeId)
    {
        var removed = _changes.RemoveAll(c => c.Id == changeId) > 0;
        if (removed)
        {
            ForgetLabels(changeId);
            _notes.Remove(changeId);
            ForgetBefore(changeId);
            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>Drops everything staged for one item (it was removed from the library meanwhile).</summary>
    public int RemoveForItem(Guid entityId)
    {
        var gone = _changes.Where(c => c.EntityId == entityId).Select(c => c.Id).ToList();
        var removed = _changes.RemoveAll(c => c.EntityId == entityId);
        if (removed > 0)
        {
            gone.ForEach(id =>
            {
                ForgetLabels(id);
                _notes.Remove(id);
                ForgetBefore(id);
            });
            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>Cancel: throws every staged change away.</summary>
    public void Discard()
    {
        if (_changes.Count == 0)
        {
            return;
        }

        _changes.Clear();
        _labels.Clear();
        _notes.Clear();
        _before.Clear();
        Changed?.Invoke();
    }

    public int CountForItem(Guid entityId) => _changes.Count(c => c.EntityId == entityId);

    /// <summary>The whole list as the request the Engine checks or saves.</summary>
    /// <summary>The request for a check or a save; <paramref name="writeIntoFiles"/> false skips the files for this one save (spec D15).</summary>
    public EditSessionRequest ToRequest(bool previewOnly, bool? writeIntoFiles = null) =>
        new() { PreviewOnly = previewOnly, Changes = [.. _changes], WriteIntoFiles = writeIntoFiles };

    private EditSessionChange? Find(string kind, Guid entityId) =>
        _changes.FirstOrDefault(c => c.Kind == kind && c.EntityId == entityId);

    private void Replace(EditSessionChange? existing, EditSessionChange next, string? label = null, string? note = null, string? before = null)
    {
        if (!string.IsNullOrWhiteSpace(before))
        {
            _before[next.Id] = before.Trim();
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            _labels.Remove(next.Id);
        }
        else
        {
            _labels[next.Id] = label.Trim();
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            _notes.Remove(next.Id);
        }
        else
        {
            _notes[next.Id] = note.Trim();
        }

        if (existing is null)
        {
            _changes.Add(next);
        }
        else
        {
            _changes[_changes.IndexOf(existing)] = next;
        }

        Changed?.Invoke();
    }

    private static string RowKey(string changeId, string fieldKey) => changeId + ":" + fieldKey;

    private void RememberBefore(string rowId, string? before)
    {
        if (!string.IsNullOrWhiteSpace(before))
        {
            _before[rowId] = before.Trim();
        }
    }

    private void ForgetLabels(string changeId)
    {
        foreach (var key in _labels.Keys.Where(key => key == changeId || key.StartsWith(changeId + ":", StringComparison.Ordinal)).ToList())
        {
            _labels.Remove(key);
        }
    }

    private void ForgetBefore(string changeId)
    {
        foreach (var key in _before.Keys.Where(key => key == changeId || key.StartsWith(changeId + ":", StringComparison.Ordinal)).ToList())
        {
            _before.Remove(key);
        }
    }

    private static bool SameKey(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NewId() => Guid.NewGuid().ToString("N");
}
