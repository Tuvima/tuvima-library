using System.Text.RegularExpressions;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Extras;

namespace MediaEngine.Api.Services.Extras;

/// <summary>
/// Reads one supporting file for the editor (universal editor v5 D11, D13, D14, section 4): the file with what a person set and what the
/// Engine read from the folders, the lists the form chooses from (this kind of file's roles, the title's groups, the places it can belong
/// to), and the owner's header with the file marked as the row the editor is open on. A read only. A file is only read for the title whose
/// own folder it sits in, so a book's neighbour in its Series cannot be opened through that book.
/// </summary>
public sealed partial class SupportingFileEditorReadService(
    IItemExtraRepository extras,
    IItemExtrasScanner scanner,
    ILibraryFolderResolver folders,
    EditorHeaderReadService headers,
    IExtrasConfigurationSource? rules = null)
{
    /// <summary>The editor kind of a supporting file in <c>config/editor.json</c>.</summary>
    public const string Kind = "supporting_file";

    /// <summary>The words for the title level when the title's name is not known.</summary>
    public const string WholeTitle = "Whole title";

    /// <summary>The editor's read of the file; null when the file is not one of this item's own supporting files.</summary>
    public async Task<EditorSupportingFileDto?> GetAsync(Guid workId, Guid fileId, Guid? profileId, CancellationToken ct = default)
    {
        var file = await extras.GetAsync(fileId, ct).ConfigureAwait(false);
        if (file is null || !await headers.OwnsSupportingFileAsync(workId, fileId, ct).ConfigureAwait(false))
        {
            return null;
        }

        var config = rules?.Current ?? ExtrasConfigurationProvider.Active;
        var header = await headers.GetHeaderAsync(workId, profileId, ct, fileId).ConfigureAwait(false);
        var titleName = header?.Root.Title is { Length: > 0 } name ? name : WholeTitle;

        // A folder of pictures is one item: the facts are the whole folder's.
        var members = new List<ItemExtra> { file };
        if (file.Kind == ItemExtraKinds.Picture)
        {
            var siblings = (await extras.ListByItemFoldersAsync([file.ItemFolder], ct).ConfigureAwait(false))
                .Where(other => other.Kind == ItemExtraKinds.Picture
                    && string.Equals(other.ItemFolder, file.ItemFolder, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(other.GroupKey ?? string.Empty, file.GroupKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (siblings.Count > 0)
            {
                members = siblings;
            }
        }

        var (badge, typeLabel) = SupportingFileListService.TypeWords(file, members.Count > 1, config);
        var places = await ListPlacesAsync(file.ItemFolder, titleName, ct).ConfigureAwait(false);
        var groups = (await extras.ListGroupsAsync(file.ItemFolder, ct).ConfigureAwait(false)).Select(group => group.Name).ToList();
        if (file.GroupName is { Length: > 0 } own && !groups.Contains(own, StringComparer.OrdinalIgnoreCase))
        {
            groups.Add(own);
        }

        return new EditorSupportingFileDto
        {
            TitleId = workId,
            File = ExtraCardBuilder.ToSupportingFile(
                file, File.Exists, config, members.Count, members.Sum(member => member.SizeBytes)),
            Type = SupportingFileListService.TypeOf(file, config),
            TypeLabel = typeLabel,
            Badge = badge,
            MimeType = ExtraCardBuilder.MimeTypeOf(file.FilePath),
            Folder = FolderOf(file),
            ReadOnly = IsReadOnly(file),
            Roles = [.. config.RolesFor(file.Kind).Select(role => new EditorSupportingFileRoleDto { Id = role.Id, Label = role.Label })],
            Groups = groups,
            Places = places,
            ScopeLabel = LabelOf(file.EffectiveScope, titleName),
            FolderScopeLabel = LabelOf(file.InferredScope, titleName),
            AlongsideLabel = EditorHeaderReadService.AlongsideOf(file.EffectiveAlongside, header?.Root.MediaType ?? string.Empty)?.Label,
            Header = header,
            Config = headers.GetKindConfig(Kind) ?? new EditorHeaderConfigDto(),
        };
    }

    // The title first, then seasons with their episodes, then sections with their lessons, then tracks.
    private async Task<List<EditorSupportingFilePlaceDto>> ListPlacesAsync(string itemFolder, string titleName, CancellationToken ct)
    {
        var places = new List<EditorSupportingFilePlaceDto>
        {
            new() { Key = ExtraScope.Root.Key, Label = titleName, Level = SupportingFileScopes.LevelOf(ExtraScope.Root) },
        };

        foreach (var scope in (await scanner.ListPlacesAsync(itemFolder, ct).ConfigureAwait(false))
                     .Where(scope => scope.Level != ExtraScopeLevel.Root && scope.Level != ExtraScopeLevel.None)
                     .Distinct()
                     .OrderBy(scope => SupportingFileScopes.SortKey(scope), StringComparer.Ordinal))
        {
            places.Add(new EditorSupportingFilePlaceDto
            {
                Key = scope.Key,
                Label = LabelOf(scope, titleName),
                Level = SupportingFileScopes.LevelOf(scope),
                ParentKey = SupportingFileScopes.Ancestors(scope).Select(ancestor => ancestor.Key).LastOrDefault(),
            });
        }

        return places;
    }

    /// <summary>"The Last Signal", "Season 2", "Season 2, episode 3", "02 · Components".</summary>
    internal static string LabelOf(ExtraScope scope, string titleName)
    {
        if (scope.Level == ExtraScopeLevel.Root)
        {
            return titleName;
        }

        var label = SupportingFileScopes.LabelOf(scope);
        var numbered = NumberedName().Match(label);
        return scope.Level is ExtraScopeLevel.Folder or ExtraScopeLevel.File && numbered.Success
            ? $"{numbered.Groups[1].Value} · {numbered.Groups[2].Value.Trim()}"
            : label;
    }

    // The folder the file sits in, from the title's folder down ("Season 02 / Documents"); the title folder's own name when it sits in it.
    private static string? FolderOf(ItemExtra file)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(file.FilePath));
        var root = Path.GetFullPath(file.ItemFolder);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var relative = Path.GetRelativePath(root, directory);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return string.Join(" / ", relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries));
    }

    // A file in a Read-only folder, or outside every folder Library manages, is read where it is and never moved or deleted.
    private bool IsReadOnly(ItemExtra file)
    {
        var resolved = folders.ResolveSourceForPath(file.FilePath);
        return resolved is null || !resolved.Source.IsManaged || !resolved.Source.IsWritable;
    }

    [GeneratedRegex(@"^(\d+)[\s._-]+(.+)$")]
    private static partial Regex NumberedName();
}
