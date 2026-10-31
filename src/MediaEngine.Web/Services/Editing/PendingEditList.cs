using MediaEngine.Contracts.Editing;

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

    public event Action? Changed;

    public IReadOnlyList<EditSessionChange> Changes => _changes;

    public int Count => _changes.Count;

    public bool IsEmpty => _changes.Count == 0;

    /// <summary>How many things Save would do: one per staged field, and one per artwork, identity, relationship or file-move entry.</summary>
    public int ChangeCount => _changes.Sum(change => change.Kind == EditSessionChangeKinds.Details ? Math.Max(1, change.Fields.Count) : 1);

    /// <summary>Stages one details field for an item, replacing an earlier value staged for the same field.</summary>
    public void SetField(Guid entityId, string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var change = Find(EditSessionChangeKinds.Details, entityId);
        var fields = change?.Fields.Where(f => !SameKey(f.Key, key)).ToList() ?? [];
        fields.Add(new EditSessionFieldChange { Key = key.Trim(), Value = value });
        Replace(change, new EditSessionChange
        {
            Id = change?.Id ?? NewId(),
            Kind = EditSessionChangeKinds.Details,
            EntityId = entityId,
            Fields = fields,
        });
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
    public void SetArtwork(Guid entityId, string slot, string targetId, string? label = null)
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
        }, label);
    }

    /// <summary>
    /// Stages an identity, relationship or file-move change; one per item and kind, a later one replacing the earlier.
    /// <paramref name="label"/> is an optional name for the chosen record or destination, shown in Review changes; it is never sent to the Engine.
    /// </summary>
    public void SetTarget(string kind, Guid entityId, string? targetId, string? token = null, string? label = null)
    {
        if (kind is EditSessionChangeKinds.Details or EditSessionChangeKinds.Artwork or EditSessionChangeKinds.ExtraMove || !EditSessionChangeKinds.IsKnown(kind))
        {
            throw new ArgumentException("Use SetField, SetArtwork or SetExtraMove for details, artwork and Extras changes.", nameof(kind));
        }

        var existing = Find(kind, entityId);
        Replace(existing, new EditSessionChange
        {
            Id = existing?.Id ?? NewId(),
            Kind = kind,
            EntityId = entityId,
            TargetId = targetId,
            Token = token,
        }, label);
    }

    /// <summary>
    /// Stages moving one Extra to an episode (<paramref name="extraId"/>, with <see cref="EditSessionExtraMove.ToEpisodeToken"/>) or one
    /// episode to Extras (<paramref name="extraId"/> null, token <see cref="EditSessionExtraMove.ToExtrasToken"/>). Unlike the other
    /// kinds there can be several per item, one for each Extra, so they are told apart by <see cref="EditSessionChange.TargetId"/>.
    /// </summary>
    public void SetExtraMove(Guid entityId, string? extraId, string token, string? label = null)
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
        }, label);
    }

    /// <summary>The staged move of one Extra (or of the episode itself when <paramref name="extraId"/> is null), or null.</summary>
    public EditSessionChange? FindExtraMove(Guid entityId, string? extraId) =>
        _changes.FirstOrDefault(c => c.Kind == EditSessionChangeKinds.ExtraMove
            && c.EntityId == entityId
            && string.Equals(c.TargetId, extraId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name given for a staged identity, relationship or artwork choice, or null when none was given.</summary>
    public string? Label(string changeId) => _labels.GetValueOrDefault(changeId);

    /// <summary>Removes one staged change by its id (the row's menu in Review changes).</summary>
    public bool Remove(string changeId)
    {
        var removed = _changes.RemoveAll(c => c.Id == changeId) > 0;
        if (removed)
        {
            _labels.Remove(changeId);
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
            gone.ForEach(id => _labels.Remove(id));
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
        Changed?.Invoke();
    }

    public int CountForItem(Guid entityId) => _changes.Count(c => c.EntityId == entityId);

    /// <summary>The whole list as the request the Engine checks or saves.</summary>
    /// <summary>The request for a check or a save; <paramref name="writeIntoFiles"/> false skips the files for this one save (spec D15).</summary>
    public EditSessionRequest ToRequest(bool previewOnly, bool? writeIntoFiles = null) =>
        new() { PreviewOnly = previewOnly, Changes = [.. _changes], WriteIntoFiles = writeIntoFiles };

    private EditSessionChange? Find(string kind, Guid entityId) =>
        _changes.FirstOrDefault(c => c.Kind == kind && c.EntityId == entityId);

    private void Replace(EditSessionChange? existing, EditSessionChange next, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            _labels.Remove(next.Id);
        }
        else
        {
            _labels[next.Id] = label.Trim();
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

    private static bool SameKey(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NewId() => Guid.NewGuid().ToString("N");
}
