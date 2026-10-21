using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// What the person has typed for one field in the batch section. Every field starts as "No change" and only becomes a change once the
/// person touches it: a scalar gets a value or Clear, a list gets at least one value with Add, Replace or Remove.
/// </summary>
public sealed class DetailBatchFieldDraft
{
    private readonly List<string> _values = [];

    public DetailBatchFieldDraft(LibraryBatchFieldState field)
    {
        Key = field.Key;
        IsList = string.Equals(field.Kind, LibraryBatchFieldKinds.List, StringComparison.Ordinal);
        CanClear = field.Operations.Contains(LibraryBatchEditOps.Clear);
        ListOperation = field.Operations.Contains(LibraryBatchEditOps.Add) || field.Operations.Count == 0
            ? LibraryBatchEditOps.Add
            : field.Operations[0];
    }

    public string Key { get; }

    public bool IsList { get; }

    public bool CanClear { get; }

    /// <summary>The typed value of a scalar field; blank means nothing typed.</summary>
    public string? Text { get; private set; }

    /// <summary>True when a scalar field is to be emptied on every ticked item.</summary>
    public bool Clear { get; private set; }

    /// <summary>Add, Replace or Remove for a list field.</summary>
    public string ListOperation { get; private set; }

    public IReadOnlyList<string> Values => _values;

    public bool IsTouched => IsList
        ? _values.Count > 0
        : Clear || !string.IsNullOrWhiteSpace(Text);

    public void SetText(string? text)
    {
        Text = text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            Clear = false;
        }
    }

    public void SetClear(bool clear)
    {
        if (!CanClear)
        {
            return;
        }

        Clear = clear;
        if (clear)
        {
            Text = null;
        }
    }

    public void SetListOperation(string operation) => ListOperation = operation;

    /// <summary>Adds a value to a list field unless it is blank or already there (ignoring case). Returns true when it was added.</summary>
    public bool AddValue(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || _values.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _values.Add(trimmed);
        return true;
    }

    public void RemoveValue(string value) => _values.RemoveAll(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>The operation to send, or null when the field is still "No change".</summary>
    public LibraryFieldChange? ToChange()
    {
        if (!IsTouched)
        {
            return null;
        }

        if (IsList)
        {
            return new LibraryFieldChange { Key = Key, Op = ListOperation, Values = [.. _values] };
        }

        return Clear
            ? new LibraryFieldChange { Key = Key, Op = LibraryBatchEditOps.Clear }
            : new LibraryFieldChange { Key = Key, Op = LibraryBatchEditOps.Set, Value = Text!.Trim() };
    }
}

/// <summary>The batch section's input across every field. It holds only what was touched; it is dropped when the section closes or the changes are applied.</summary>
public sealed class DetailBatchDraft
{
    private readonly Dictionary<string, DetailBatchFieldDraft> _fields = new(StringComparer.OrdinalIgnoreCase);

    public bool HasTouched => _fields.Values.Any(draft => draft.IsTouched);

    /// <summary>The field's draft, created on first use.</summary>
    public DetailBatchFieldDraft For(LibraryBatchFieldState field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!_fields.TryGetValue(field.Key, out var draft))
        {
            draft = new DetailBatchFieldDraft(field);
            _fields[field.Key] = draft;
        }

        return draft;
    }

    /// <summary>Keeps the input of fields the Engine still offers and drops the rest (after the ticked rows changed).</summary>
    public void Keep(IEnumerable<LibraryBatchFieldState> fields)
    {
        var keys = fields.Where(field => field.Operations.Count > 0).Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _fields.Keys.Where(key => !keys.Contains(key)).ToList())
        {
            _fields.Remove(key);
        }
    }

    /// <summary>Exactly the touched fields, in the order they were first shown.</summary>
    public List<LibraryFieldChange> ToChanges() =>
    [
        .. _fields.Values.Select(field => field.ToChange()).OfType<LibraryFieldChange>(),
    ];

    public void Reset() => _fields.Clear();
}
