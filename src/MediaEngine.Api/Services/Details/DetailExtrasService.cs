using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Extras;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;

namespace MediaEngine.Api.Services.Details;

/// <summary>
/// Adds a title's Extras to its page (Extras spec WP2). The page has already been composed for the viewer, so only a title the viewer may
/// see gets here: Kids levels, hidden libraries and the Vault are applied before this runs, and a Kids profile that may not see the show
/// never receives its Extras. The Extras of a title are found from the folders its own files sit in. An episode's or lesson's own page lists the
/// files that belong to it and the files shown alongside it (universal editor v5 D31, the second place); the title's page lists them all, each
/// card saying which lesson it is shown alongside so the page's rows can carry the chip (the first place).
/// </summary>
public sealed class DetailExtrasService(
    IMediaAssetRepository assets,
    IItemExtraRepository extras,
    ILibraryFolderResolver libraries,
    IExtrasConfigurationSource? rules = null,
    IEditorConfigurationSource? editorConfig = null)
{
    // How many of the page's files are looked up to find the folders Extras sit in. One batched lookup, so the cost is the folder walk, not a
    // query per file; 500 covers a long course (every lesson then gets its chip) and still bounds a page that lists thousands of files.
    private const int MaxAssetsToLocate = 500;

    private static readonly HashSet<DetailEntityType> TitleTypes =
    [
        DetailEntityType.Work,
        DetailEntityType.Movie,
        DetailEntityType.TvShow,
        DetailEntityType.Book,
        DetailEntityType.Audiobook,
        DetailEntityType.ComicIssue,
        DetailEntityType.ComicSeries,
        DetailEntityType.MusicAlbum,
    ];

    private static readonly HashSet<DetailEntityType> EpisodeTypes = [DetailEntityType.TvEpisode];

    internal async Task ApplyAsync(
        DetailPageViewModel detail,
        IReadOnlyList<Guid>? authorizedAssetIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var isEpisode = EpisodeTypes.Contains(detail.EntityType);
        if (!TitleTypes.Contains(detail.EntityType) && !isEpisode)
        {
            return;
        }

        var assetIds = new List<Guid>();
        if (authorizedAssetIds is not null)
        {
            assetIds.AddRange(authorizedAssetIds);
        }

        foreach (var item in detail.MediaGroups.SelectMany(group => group.Items))
        {
            if (Guid.TryParse(item.AssetId, out var assetId) && assetId != Guid.Empty)
            {
                assetIds.Add(assetId);
            }
        }

        var distinct = assetIds.Distinct().Take(MaxAssetsToLocate).ToList();
        if (distinct.Count == 0)
        {
            return;
        }

        var paths = await assets.GetFilePathsAsync(distinct, ct).ConfigureAwait(false);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Values)
        {
            foreach (var folder in CandidateFolders(path))
            {
                folders.Add(folder);
            }
        }

        if (folders.Count == 0)
        {
            return;
        }

        var found = await extras.ListByItemFoldersAsync([.. folders], ct).ConfigureAwait(false);
        if (isEpisode)
        {
            found = [.. found.Where(extra => BelongsToOrShownWith(extra, detail, paths.Values))];
        }

        if (found.Count == 0)
        {
            return;
        }

        // A file shown alongside a lesson (a video in a folder of videos) names it by path; the card carries the lesson's asset so the page's row finds it.
        var lessons = await LessonAssetsAsync(found, ct).ConfigureAwait(false);
        var cardsPerGroup = (editorConfig?.Current ?? EditorConfigurationProvider.BuiltIn).CardsPerGroup;
        detail.Extras = ExtraCardBuilder.Build(
            found, File.Exists, rules?.Current,
            extra => extra.EffectiveAlongside is { } alongside ? lessons(extra, alongside) : null,
            cardsPerGroup,
            extra => lessons(extra, extra.EffectiveScope));

        // An episode's or lesson's own page lists every file it has, so it has no "See all" and no cap (0); a title's page caps each group.
        detail.ExtrasCardsPerGroup = isEpisode ? 0 : cardsPerGroup;
    }

    // An episode's page: the files that belong to this episode, and the files of its season or show that are shown alongside it.
    private static bool BelongsToOrShownWith(ItemExtra extra, DetailPageViewModel detail, IEnumerable<string> pagePaths)
    {
        var mine = new List<ExtraScope>();
        if (int.TryParse(detail.Facts?.SeasonNumber, out var season) && int.TryParse(detail.Facts?.EpisodeNumber, out var episode))
        {
            mine.Add(ExtraScope.ForEpisode(season, episode));
        }

        foreach (var path in pagePaths)
        {
            mine.Add(ExtraScope.ForFile(RelativePath(extra.ItemFolder, path)));
        }

        return mine.Contains(extra.EffectiveScope) || (extra.EffectiveAlongside is { } alongside && mine.Contains(alongside));
    }

    // Finds the media asset of a lesson a file belongs to or is shown alongside (a scope at file level names it by path). The files of every
    // folder that holds such an Extra are listed once, in batches, and indexed by their path below the title folder.
    private async Task<Func<ItemExtra, ExtraScope, Guid?>> LessonAssetsAsync(IReadOnlyList<ItemExtra> found, CancellationToken ct)
    {
        var wanted = found.Where(extra => extra.EffectiveAlongside is { Level: ExtraScopeLevel.File } || extra.EffectiveScope.Level == ExtraScopeLevel.File).ToList();
        if (wanted.Count == 0)
        {
            return (_, _) => null;
        }

        var files = await assets.ListFilesUnderFoldersAsync([.. wanted.Select(extra => extra.ItemFolder).Distinct(StringComparer.OrdinalIgnoreCase)], ct)
            .ConfigureAwait(false);
        var byPath = new Dictionary<(string Folder, string Path), Guid>();
        foreach (var folder in wanted.Select(extra => extra.ItemFolder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in files)
            {
                var relative = RelativePath(folder, file.FilePath);
                if (!relative.StartsWith("../", StringComparison.Ordinal))
                {
                    byPath.TryAdd((folder.ToLowerInvariant(), relative.ToLowerInvariant()), file.AssetId);
                }
            }
        }

        return (extra, scope) => scope.Level == ExtraScopeLevel.File
            && byPath.TryGetValue((extra.ItemFolder.ToLowerInvariant(), (scope.Path ?? string.Empty).ToLowerInvariant()), out var asset)
                ? asset
                : null;
    }

    // The path below the title folder with forward slashes, as a lesson's scope writes it.
    private static string RelativePath(string itemFolder, string filePath) =>
        Path.GetRelativePath(itemFolder, filePath).Replace('\\', '/');

    /// <summary>
    /// The folders an Extra of this file's title can be filed under: the file's own folder and each folder above it, up to but not
    /// including the library folder. A show's Extras sit in its top folder, a film's in the film's folder.
    /// </summary>
    internal IEnumerable<string> CandidateFolders(string filePath)
    {
        var source = libraries.ResolveSourcePath(filePath);
        if (string.IsNullOrWhiteSpace(source))
        {
            yield break;
        }

        var root = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (!string.IsNullOrEmpty(directory)
               && directory.Length > root.Length
               && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            yield return directory;
            directory = Path.GetDirectoryName(directory);
        }
    }
}
