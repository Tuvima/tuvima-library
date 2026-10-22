using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// One kind of library the Libraries screen lists, drawn from the Engine's list of library kinds. <paramref name="LibraryId"/> is set
/// only for a library that no kind claims (an older custom library, listed so its folders can be seen and removed).
/// </summary>
public sealed record LibraryTypeOption(
    string Key,
    string Label,
    string PluralLabel,
    string Area,
    string[] MediaTypes,
    string Icon,
    string? LibraryId = null,
    string Tooltip = "",
    IReadOnlyList<string>? LookupProviders = null,
    string? NamingTemplate = null,
    bool AllowsMultiple = false,
    int MaxLibraries = 1);

/// <summary>Keys the Libraries addresses and the Dashboard know by name: the Import folder row and the View group.</summary>
public static class LibraryTypeCatalog
{
    public const string ImportKey = "import";
    public const string ViewArea = "view";

    /// <summary>True for a View kind (Photos, Personal videos, Documents): its folders live in View, not in a library.</summary>
    public static bool IsViewKind(LibraryTypeOption? type) =>
        type is not null && string.Equals(type.Area, ViewArea, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The library kinds ("recipes") the Engine lists, shaped for the Libraries screens: the folders panel (first-run setup and
/// Settings), the group pages, the front door and Add folder are all drawn from this one list, so a kind a plugin adds appears
/// in each of them with no screen changes. A group's types are the available kinds of its area, in the list's order.
/// </summary>
public sealed class LibraryKindCatalog
{
    private readonly IReadOnlyList<LibraryTypeOption> _all;

    /// <summary>A catalog with no kinds, for before the list has been read.</summary>
    public static LibraryKindCatalog Empty { get; } = new([]);

    public LibraryKindCatalog(IEnumerable<LibraryKindDto> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        _all =
        [
            .. kinds
                .Where(kind => kind.Available && !string.IsNullOrWhiteSpace(kind.Key))
                .Select(ToOption),
        ];
        Groups =
        [
            .. LibraryGroupCatalog.Definitions.Select(definition => new LibraryGroup(
                definition.Key,
                definition.Label,
                definition.Icon,
                [.. _all.Where(option => string.Equals(option.Area, definition.Key, StringComparison.OrdinalIgnoreCase)).Select(option => option.Key)])),
        ];
        Types = [.. _all.Where(option => !LibraryTypeCatalog.IsViewKind(option))];
    }

    /// <summary>The kinds with a library behind them (every available kind outside View), in the list's order.</summary>
    public IReadOnlyList<LibraryTypeOption> Types { get; }

    /// <summary>The five groups in the order the front door shows them, each with its available kinds.</summary>
    public IReadOnlyList<LibraryGroup> Groups { get; }

    /// <summary>
    /// The View kinds (Photos, Personal videos, Documents). View keeps its folders in its own store, not in a library of the
    /// Work/Edition graph, so they are not in <see cref="Types"/>.
    /// </summary>
    public IReadOnlyList<LibraryTypeOption> ViewKinds => [.. _all.Where(LibraryTypeCatalog.IsViewKind)];

    /// <summary>An available View kind by key, or null (a kind of another group is not a View kind).</summary>
    public LibraryTypeOption? FindViewKind(string? key) => Find(key) is { } kind && LibraryTypeCatalog.IsViewKind(kind) ? kind : null;

    /// <summary>An available kind (including the View kinds) by key, or null.</summary>
    public LibraryTypeOption? Find(string? key) =>
        _all.FirstOrDefault(option => string.Equals(option.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The group a kind belongs to, or null for an unknown key.</summary>
    public LibraryGroup? GroupFor(string? typeKey) =>
        Groups.FirstOrDefault(group => group.TypeKeys.Contains(typeKey ?? string.Empty, StringComparer.OrdinalIgnoreCase));

    public LibraryGroup? TryGetGroup(string? key) =>
        Groups.FirstOrDefault(group => string.Equals(group.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A group by key, only when it may be shown: the Vault is not found while Vault mode is off.</summary>
    public LibraryGroup? TryGetVisibleGroup(string? key, bool showVault) =>
        Visible(showVault).FirstOrDefault(group => string.Equals(group.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The group a catalogued library with this area sits under, or null when the area is unknown.</summary>
    public LibraryGroup? ForArea(string? area) =>
        string.Equals(area?.Trim(), LibraryGroupCatalog.VaultKey, StringComparison.OrdinalIgnoreCase) ? null : TryGetGroup(area);

    /// <summary>The groups to draw: Vault is last and only present when it may be shown.</summary>
    public IReadOnlyList<LibraryGroup> Visible(bool showVault) =>
        showVault ? Groups : [.. Groups.Where(group => group.Key != LibraryGroupCatalog.VaultKey)];

    private static LibraryTypeOption ToOption(LibraryKindDto kind) => new(
        kind.Key,
        kind.Label,
        PluralLabelFor(kind.Label),
        kind.Area,
        string.IsNullOrWhiteSpace(kind.Format) ? [] : [kind.Format],
        LibraryKindIcons.Resolve(kind.Icon),
        Tooltip: kind.Tooltip,
        LookupProviders: kind.LookupProviders,
        NamingTemplate: kind.NamingTemplate,
        AllowsMultiple: kind.AllowsMultiple,
        MaxLibraries: Math.Max(1, kind.MaxLibraries));

    /// <summary>The label as it reads in the middle of a sentence: "TV Shows" is "TV shows", and "&amp;" reads "and".</summary>
    internal static string PluralLabelFor(string label)
    {
        var words = label.Replace("&", "and", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select((word, index) =>
            index == 0 || word.All(char.IsUpper) || word.Length == 0 ? word : char.ToLowerInvariant(word[0]) + word[1..]));
    }
}

/// <summary>Turns a kind's icon name (an <c>AppMaterialIcons</c> name such as <c>MenuBook</c>) into the outlined icon the screens draw.</summary>
public static class LibraryKindIcons
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Outlined = new(() =>
        typeof(AppMaterialIcons.Outlined)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!, StringComparer.OrdinalIgnoreCase));

    /// <summary>The outlined icon with this name, or the folder icon when there is none.</summary>
    public static string Resolve(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Outlined.Value.TryGetValue(name.Trim(), out var icon) ? icon : AppMaterialIcons.Outlined.Folder;
}

/// <summary>One step of the breadcrumb on the group and type pages; no <paramref name="Href"/> means the current page.</summary>
public sealed record LibraryCrumb(string Label, string? Href = null);

/// <summary>What the Settings frame shows for one Libraries address: its title and the breadcrumb steps from "Libraries" down (none on the front door).</summary>
public sealed record LibraryLevelDescription(string Title, IReadOnlyList<LibraryCrumb> Crumbs);

/// <summary>
/// Reads a Libraries address (/settings/libraries/{subsection}/{detail}) and says what the Settings frame should show above the
/// content: the frame owns the breadcrumb and the title, so the pages draw neither. An address that names nothing (or the Vault
/// while it is hidden) reads as the front door; the page itself shows its "does not exist" state.
/// </summary>
public static class LibraryAddress
{
    public const string FrontDoorTitle = "Libraries";
    private const string OtherLibraryTitle = "Other library";
    private const string LibrariesAddress = "/settings/libraries";

    public static LibraryLevelDescription Describe(LibraryKindCatalog kinds, string? subsection, string? detail, bool showVault)
    {
        var front = new LibraryLevelDescription(FrontDoorTitle, []);
        var sub = subsection?.Trim();
        var det = detail?.Trim();
        var libraries = new LibraryCrumb(FrontDoorTitle, LibrariesAddress);
        if (string.IsNullOrEmpty(sub))
        {
            return front;
        }

        if (string.Equals(sub, LibraryTypeCatalog.ImportKey, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(det) ? Level("Import folder", libraries) : front;
        }

        if (string.Equals(sub, "other", StringComparison.OrdinalIgnoreCase))
        {
            return det is not null && det.StartsWith("other-", StringComparison.OrdinalIgnoreCase) ? Level(OtherLibraryTitle, libraries) : front;
        }

        if (kinds.TryGetVisibleGroup(sub, showVault) is not { } group)
        {
            return front;
        }

        var groupCrumb = new LibraryCrumb(group.Label, LibraryGroupCatalog.GroupAddress(group));
        if (string.IsNullOrEmpty(det))
        {
            return Level(group.Label, libraries);
        }

        if (group.Key == LibraryTypeCatalog.ViewArea)
        {
            return kinds.FindViewKind(det) is { } viewKind ? Level(viewKind.Label, libraries, groupCrumb) : front;
        }

        if (group.TypeKeys.FirstOrDefault(key => string.Equals(key, det, StringComparison.OrdinalIgnoreCase)) is { } typeKey)
        {
            return Level(kinds.Find(typeKey)!.Label, libraries, groupCrumb);
        }

        return det.StartsWith("other-", StringComparison.OrdinalIgnoreCase) ? Level(OtherLibraryTitle, libraries, groupCrumb) : front;
    }

    // The last step is the page itself, so it has no link.
    private static LibraryLevelDescription Level(string title, params LibraryCrumb[] ancestors) =>
        new(title, [.. ancestors, new LibraryCrumb(title)]);
}

/// <summary>A way of presenting library types: Watch, Listen, Read, View, Vault. A group has no folders of its own.</summary>
public sealed record LibraryGroup(string Key, string Label, string Icon, IReadOnlyList<string> TypeKeys);

/// <summary>The five groups, in the order the Libraries front door shows them (icons match the main navigation).</summary>
public static class LibraryGroupCatalog
{
    public const string VaultKey = "vault";

    /// <summary>The groups themselves are fixed; which kinds sit in each comes from the Engine's list of library kinds (see <see cref="LibraryKindCatalog"/>).</summary>
    public static IReadOnlyList<(string Key, string Label, string Icon)> Definitions { get; } =
    [
        ("watch", "Watch", AppMaterialIcons.Outlined.LiveTv),
        ("listen", "Listen", AppMaterialIcons.Outlined.Headphones),
        ("read", "Read", AppMaterialIcons.Outlined.MenuBook),
        ("view", "View", AppMaterialIcons.Outlined.PhotoLibrary),
        (VaultKey, "Vault", AppMaterialIcons.Outlined.Lock),
    ];

    /// <summary>The address of a group page ("/settings/libraries/watch").</summary>
    public static string GroupAddress(LibraryGroup group) => $"/settings/libraries/{group.Key}";

    /// <summary>The address of a type page ("/settings/libraries/watch/movies"); "other-{libraryId}" for a library that is not a standard type.</summary>
    public static string TypeAddress(LibraryGroup group, string typeKey) => $"/settings/libraries/{group.Key}/{typeKey}";
}
