using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The one folder step (Libraries v3 D21, L4): Add library's third step and Edit's Add folder are the same window. Both read their heading, lead, chips and
/// folder-name choice from here, so the two callers cannot drift apart. The window itself is <see cref="ServerFolderPicker"/>; callers differ only in what they prefill.
/// </summary>
public static class LibraryFolderStep
{
    /// <summary>The folder's friendly name is always the first field of the folder step.</summary>
    public const bool ShowFolderName = true;

    /// <summary>"Where is it? · Movies"; just "Where is it?" while the kind is not known.</summary>
    public static string Heading(IStringLocalizer<SharedStrings> l, LibraryTypeOption? kind) =>
        kind is null ? l["FolderStep_Where"].Value : l["FolderStep_WhereKind", KindLabel(kind)].Value;

    /// <summary>The line under the heading.</summary>
    public static string Lead(IStringLocalizer<SharedStrings> l, LibraryTypeOption? kind) => l["FolderStep_Lead", Plural(kind)].Value;

    /// <summary>The name a kind goes by everywhere: the kind list's label ("Videos", "Other audio", "Photos and videos").</summary>
    public static string KindLabel(LibraryTypeOption kind) => LibraryTypeCatalog.AddChoiceLabel(kind);

    // "Movies" reads "movies" in the middle of a sentence; "TV shows" and "YouTube" keep their capitals, and the "Other" kinds just say files.
    public static string Plural(LibraryTypeOption? kind)
    {
        if (kind is null || kind.Key.StartsWith("other", StringComparison.Ordinal))
        {
            return "files";
        }

        var label = kind.PluralLabel;
        var firstWord = label.Split(' ')[0];
        return firstWord.Skip(1).Any(char.IsUpper) ? label : char.ToLowerInvariant(label[0]) + label[1..];
    }

    /// <summary>The area and kind chips above the folder list, read only and fixed to the library's area and kind. <paramref name="areaLabel"/> null leaves the area chip out.</summary>
    public static RenderFragment Chips(string areaKey, string? areaLabel, LibraryTypeOption? kind) => builder =>
    {
        builder.OpenComponent<LibraryFolderStepChips>(0);
        builder.AddAttribute(1, nameof(LibraryFolderStepChips.AreaKey), areaKey);
        builder.AddAttribute(2, nameof(LibraryFolderStepChips.AreaLabel), areaLabel);
        builder.AddAttribute(3, nameof(LibraryFolderStepChips.KindLabel), kind is null ? string.Empty : KindLabel(kind));
        builder.CloseComponent();
    };
}
