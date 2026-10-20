namespace MediaEngine.Domain.Services;

/// <summary>Whether a batch field holds one value or a list of values.</summary>
public enum BatchEditFieldKind
{
    Scalar,
    List,
}

/// <summary>One field a batch edit knows about. Locked fields are shown, with the reason, but never written.</summary>
public sealed record BatchEditField(
    string Key,
    string Label,
    BatchEditFieldKind Kind,
    string? LockedReason)
{
    public bool IsEditable => LockedReason is null;

    /// <summary>The operations the field accepts (empty for a locked field).</summary>
    public IReadOnlyList<string> Operations => LockedReason is not null
        ? []
        : Kind == BatchEditFieldKind.List
            ? [BatchEditFieldCatalog.OpAdd, BatchEditFieldCatalog.OpReplace, BatchEditFieldCatalog.OpRemove]
            : [BatchEditFieldCatalog.OpSet, BatchEditFieldCatalog.OpClear];
}

/// <summary>A validated batch field operation, ready to apply to one item.</summary>
public sealed record BatchEditChange(BatchEditField Field, string Operation, string? Value, IReadOnlyList<string> Values);

/// <summary>
/// The metadata fields a batch edit may touch and the rules for changing them. A batch edit follows the same rules as the
/// pencil on a detail page: it writes presentation overrides (<see cref="DetailDisplayOverrideCatalog"/>), never provider
/// facts, never structure (names, files, parents). Credits are provider facts and are listed as locked.
/// </summary>
public static class BatchEditFieldCatalog
{
    public const string OpSet = "set";
    public const string OpClear = "clear";
    public const string OpAdd = "add";
    public const string OpReplace = "replace";
    public const string OpRemove = "remove";

    private const string ProviderFactReason = "Credits come from the catalogue and cannot be edited.";

    private static readonly char[] ListSeparators = [',', ';', '|', '\r', '\n'];

    private static readonly BatchEditField[] FieldList =
    [
        new("genre", "Genres", BatchEditFieldKind.List, null),
        new("custom_tags", "Tags", BatchEditFieldKind.List, null),
        new("year", "Year", BatchEditFieldKind.Scalar, null),
        new("rating", "Rating", BatchEditFieldKind.Scalar, null),
        new("content_rating", "Content rating", BatchEditFieldKind.Scalar, null),
        new("language", "Language", BatchEditFieldKind.Scalar, null),
        new("tagline", "Tagline", BatchEditFieldKind.Scalar, null),
        new("director", "Directors", BatchEditFieldKind.List, ProviderFactReason),
        new("screenwriter", "Writers", BatchEditFieldKind.List, ProviderFactReason),
        new("cast_member", "Cast", BatchEditFieldKind.List, ProviderFactReason),
        new("author", "Authors", BatchEditFieldKind.List, ProviderFactReason),
        new("narrator", "Narrators", BatchEditFieldKind.List, ProviderFactReason),
        new("creator", "Creators", BatchEditFieldKind.List, ProviderFactReason),
        new("artist", "Artists", BatchEditFieldKind.List, ProviderFactReason),
        new("album_artist", "Album artists", BatchEditFieldKind.List, ProviderFactReason),
        new("composer", "Composers", BatchEditFieldKind.List, ProviderFactReason),
        new("illustrator", "Illustrators", BatchEditFieldKind.List, ProviderFactReason),
    ];

    private static readonly Dictionary<string, BatchEditField> ByKey =
        FieldList.ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every field a batch preview reports, editable ones first.</summary>
    public static IReadOnlyList<BatchEditField> All => FieldList;

    public static bool TryGet(string? key, out BatchEditField field)
    {
        if (!string.IsNullOrWhiteSpace(key) && ByKey.TryGetValue(key.Trim(), out var found))
        {
            field = found;
            return true;
        }

        field = null!;
        return false;
    }

    /// <summary>
    /// True when the detail pages show this field's value from the item's parent (show or season, album) while the item has
    /// no value of its own. Such a value is changed on the parent, so an item that inherits it cannot take a batch change.
    /// </summary>
    public static bool IsInherited(string mediaType, string workKind, bool hasParentWork, string key)
    {
        if (!hasParentWork)
        {
            return false;
        }

        return mediaType.Trim().ToLowerInvariant() switch
        {
            "tv" => key is "genre" or "language" or "content_rating",
            "music" => string.Equals(workKind, "child", StringComparison.OrdinalIgnoreCase)
                       && (key is "genre" or "language"),
            _ => false,
        };
    }

    /// <summary>
    /// Checks one requested operation against its field's kind and normalises its values. A field the request names that
    /// is unknown or a provider fact, or an operation the field's kind does not take, is an error the caller maps to a 400.
    /// </summary>
    public static bool TryValidateChange(
        string? key,
        string? op,
        string? value,
        IReadOnlyList<string>? values,
        out BatchEditChange change,
        out string? error)
    {
        change = null!;
        if (!TryGet(key, out var field))
        {
            error = $"'{key}' is not a field a batch edit can change.";
            return false;
        }

        if (!field.IsEditable)
        {
            error = $"{field.Label} cannot be changed here. {field.LockedReason}";
            return false;
        }

        var operation = (op ?? string.Empty).Trim().ToLowerInvariant();
        if (!field.Operations.Contains(operation))
        {
            error = $"'{op}' does not apply to {field.Label}. Use {string.Join(" or ", field.Operations)}.";
            return false;
        }

        var given = (values ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToList();
        if (field.Kind == BatchEditFieldKind.Scalar)
        {
            if (given.Count > 0)
            {
                error = $"{field.Label} takes a single value, not a list.";
                return false;
            }

            if (operation == OpClear)
            {
                change = new BatchEditChange(field, operation, null, []);
                error = null;
                return true;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"Give {field.Label} a value, or use clear to remove it.";
                return false;
            }

            if (!DetailDisplayOverrideCatalog.TryValidateValue(field.Key, value, out var normalized, out error))
            {
                return false;
            }

            change = new BatchEditChange(field, operation, normalized, []);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            error = $"{field.Label} takes a list of values, not a single value.";
            return false;
        }

        if (operation != OpReplace && given.Count == 0)
        {
            error = $"Give at least one value to {operation} for {field.Label}.";
            return false;
        }

        if (given.Any(item => item.IndexOfAny(ListSeparators) >= 0))
        {
            error = $"A {field.Label.ToLowerInvariant()} value cannot contain a comma, semicolon, pipe or line break.";
            return false;
        }

        if (!LibraryTagCatalog.TryNormalize(given, out var normalizedList, out error))
        {
            return false;
        }

        change = new BatchEditChange(field, operation, null, normalizedList);
        return true;
    }

    /// <summary>Reads a stored list value (an override, or a packed canonical scalar) into its items.</summary>
    public static IReadOnlyList<string> ParseList(string? stored) =>
        (stored ?? string.Empty)
            .Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The list a list operation leads to: <c>add</c> keeps what is there and appends, <c>remove</c> takes values off, <c>replace</c> is exactly the given values.</summary>
    public static IReadOnlyList<string> ApplyListOperation(
        IReadOnlyList<string> current,
        string operation,
        IReadOnlyList<string> values) => operation switch
        {
            OpAdd => current.Concat(values).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            OpRemove => current.Where(item => !values.Contains(item, StringComparer.OrdinalIgnoreCase)).ToList(),
            _ => values.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };

    /// <summary>The override string a list is stored as: tags use "; ", genres use ", " (as the pencil does). An empty list is an empty string.</summary>
    public static string ToOverrideValue(BatchEditField field, IReadOnlyList<string> items) =>
        string.Join(string.Equals(field.Key, "custom_tags", StringComparison.OrdinalIgnoreCase) ? "; " : ", ", items);

    /// <summary>Compares two lists as sets, ignoring order and case.</summary>
    public static bool SameItems(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count
        && left.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right.OrderBy(item => item, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
}
