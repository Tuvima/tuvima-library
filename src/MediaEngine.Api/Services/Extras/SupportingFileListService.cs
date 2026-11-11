using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Extras;

namespace MediaEngine.Api.Services.Extras;

/// <summary>What a person asked of the supporting-files list (universal editor v5 screen E). Every field is optional.</summary>
/// <param name="Search">Words that must all appear in the file name, the title shown or the description, ignoring case.</param>
/// <param name="Where"><c>all</c> (or null), <c>root</c> or a scope key (see <see cref="SupportingFileWhereOptionDto"/>).</param>
/// <param name="Type">One of the <c>SupportingFileListDefaults.Type*</c> values, or null for every type.</param>
/// <param name="Group"><c>all</c> (or null), <c>ungrouped</c>, or a group id.</param>
/// <param name="Sort">One of <c>title</c>, <c>type</c>, <c>where</c> (null is <c>title</c>).</param>
/// <param name="Descending">True to reverse the order.</param>
/// <param name="Page">From 1.</param>
/// <param name="PageSize">Rows per page; null is <see cref="SupportingFileListDefaults.PageSize"/>.</param>
public sealed record SupportingFileListQuery(
    string? Search = null,
    string? Where = null,
    string? Type = null,
    string? Group = null,
    string? Sort = null,
    bool Descending = false,
    int Page = 1,
    int? PageSize = null);

/// <summary>
/// Lists a root title's supporting files for the browser (universal editor v5 D15, D16): search over file name, title and description,
/// the Where, type and group filters, sorting and paging, with the left list's counts. The files of a title number in the hundreds, so
/// the list is filtered in memory over the rows the repository returns for the title's folders; the values a person set and the values
/// read from the folders already fold into each row, so one place decides what a title, group or place reads as. A folder of pictures is
/// one row (D17).
/// </summary>
public sealed class SupportingFileListService(
    IItemExtraRepository extras,
    ISupportingFileTitleResolver resolver,
    IExtrasConfigurationSource? rules = null)
{
    internal const string AllKey = "all";

    public async Task<SupportingFileListDto> ListAsync(Guid titleId, SupportingFileListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var folders = await resolver.ResolveItemFoldersAsync(titleId, ct).ConfigureAwait(false);
        var config = rules?.Current ?? ExtrasConfigurationProvider.Active;
        var rows = folders.Count == 0 ? [] : await extras.ListByItemFoldersAsync(folders, ct).ConfigureAwait(false);
        var entries = Collapse(rows);

        var groups = await LoadGroupsAsync(folders, entries, ct).ConfigureAwait(false);
        var filtered = Filter(entries, query, groups, config).ToList();
        var ordered = Order(filtered, query, config).ToList();

        var pageSize = Math.Clamp(query.PageSize ?? SupportingFileListDefaults.PageSize, 1, SupportingFileListDefaults.MaxPageSize);
        var pageCount = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)pageSize));
        var page = Math.Clamp(query.Page, 1, pageCount);

        return new SupportingFileListDto
        {
            Items = [.. ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(entry => ToRow(entry, config))],
            Total = ordered.Count,
            TotalAll = entries.Count,
            Page = page,
            PageSize = pageSize,
            PageCount = pageCount,
            Groups = groups,
            UngroupedCount = entries.Count(entry => entry.Primary.GroupId is null),
            WhereOptions = BuildWhereOptions(entries),
        };
    }

    /// <summary>One supporting file as the list sees it: a single file, or a folder of pictures (the first picture stands for all).</summary>
    internal sealed record Entry(ItemExtra Primary, IReadOnlyList<ItemExtra> Members)
    {
        public int Count => Members.Count;

        public string Title => Primary.Kind == ItemExtraKinds.Picture && Members.Count > 1 ? FolderTitle(Primary) : Primary.DisplayTitle;

        public ExtraScope Scope => Primary.EffectiveScope;
    }

    /// <summary>Pictures that share a folder are one item; every other file is its own.</summary>
    internal static List<Entry> Collapse(IReadOnlyList<ItemExtra> rows)
    {
        var entries = new List<Entry>(rows.Count);
        foreach (var row in rows.Where(row => row.Kind != ItemExtraKinds.Picture))
        {
            entries.Add(new Entry(row, [row]));
        }

        foreach (var folder in rows
                     .Where(row => row.Kind == ItemExtraKinds.Picture)
                     .GroupBy(row => (row.ItemFolder.ToLowerInvariant(), (row.GroupKey ?? string.Empty).ToLowerInvariant())))
        {
            var pictures = folder.OrderBy(row => row.SortOrder).ThenBy(row => row.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
            entries.Add(new Entry(pictures[0], pictures));
        }

        return entries;
    }

    private static string FolderTitle(ItemExtra first)
    {
        var name = (first.GroupKey ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(name) ? first.DisplayTitle : name.Replace('_', ' ').Trim();
    }

    private async Task<IReadOnlyList<SupportingFileGroupDto>> LoadGroupsAsync(
        IReadOnlyList<string> folders, IReadOnlyList<Entry> entries, CancellationToken ct)
    {
        var groups = new List<SupportingFileGroupDto>();
        foreach (var folder in folders)
        {
            foreach (var group in await extras.ListGroupsAsync(folder, ct).ConfigureAwait(false))
            {
                if (groups.Any(existing => existing.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                groups.Add(new SupportingFileGroupDto
                {
                    Id = group.Id.ToString("D"),
                    Name = group.Name,
                    SortOrder = groups.Count,
                    FileCount = entries.Count(entry => entry.Primary.GroupName?.Equals(group.Name, StringComparison.OrdinalIgnoreCase) == true),
                });
            }
        }

        return groups;
    }

    private static IEnumerable<Entry> Filter(
        IEnumerable<Entry> entries, SupportingFileListQuery query, IReadOnlyList<SupportingFileGroupDto> groups, ExtrasConfiguration config)
    {
        var words = (query.Search ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var where = ParseWhere(query.Where);
        var type = string.IsNullOrWhiteSpace(query.Type) ? null : query.Type.Trim().ToLowerInvariant();
        var groupFilter = query.Group?.Trim();
        var ungrouped = string.Equals(groupFilter, SupportingFileListDefaults.GroupUngrouped, StringComparison.OrdinalIgnoreCase);
        var groupName = !ungrouped && !string.IsNullOrWhiteSpace(groupFilter)
            && !string.Equals(groupFilter, SupportingFileListDefaults.GroupAll, StringComparison.OrdinalIgnoreCase)
                ? groups.FirstOrDefault(group => string.Equals(group.Id, groupFilter, StringComparison.OrdinalIgnoreCase))?.Name ?? groupFilter
                : null;

        foreach (var entry in entries)
        {
            if (where is { } scope && !SupportingFileScopes.Takes(scope, entry.Scope))
            {
                continue;
            }

            if (type is not null && !TypeOf(entry.Primary, config).Equals(type, StringComparison.Ordinal))
            {
                continue;
            }

            if (ungrouped && entry.Primary.GroupId is not null)
            {
                continue;
            }

            if (groupName is not null && !string.Equals(entry.Primary.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (words.Length > 0)
            {
                var text = $"{Path.GetFileName(entry.Primary.FilePath)} {entry.Title} {entry.Primary.Description}";
                if (!words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }

            yield return entry;
        }
    }

    private static ExtraScope? ParseWhere(string? where)
    {
        if (string.IsNullOrWhiteSpace(where) || where.Trim().Equals(SupportingFileListDefaults.WhereAll, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // An unreadable value selects nothing rather than everything: the person asked for a place that does not exist.
        return ExtraScope.TryParse(where, out var scope) ? scope : new ExtraScope(ExtraScopeLevel.None);
    }

    private static IEnumerable<Entry> Order(List<Entry> entries, SupportingFileListQuery query, ExtrasConfiguration config)
    {
        var sort = (query.Sort ?? SupportingFileListDefaults.SortTitle).Trim().ToLowerInvariant();
        IOrderedEnumerable<Entry> ordered = sort switch
        {
            SupportingFileListDefaults.SortType => query.Descending
                ? entries.OrderByDescending(entry => TypeOrder(entry.Primary, config))
                : entries.OrderBy(entry => TypeOrder(entry.Primary, config)),
            SupportingFileListDefaults.SortWhere => query.Descending
                ? entries.OrderByDescending(entry => SupportingFileScopes.SortKey(entry.Scope), StringComparer.Ordinal)
                : entries.OrderBy(entry => SupportingFileScopes.SortKey(entry.Scope), StringComparer.Ordinal),
            _ => query.Descending
                ? entries.OrderByDescending(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                : entries.OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase),
        };

        // Ties read the same way every time: title, then where, then file name.
        return ordered
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => SupportingFileScopes.SortKey(entry.Scope), StringComparer.Ordinal)
            .ThenBy(entry => entry.Primary.FilePath, StringComparer.OrdinalIgnoreCase);
    }

    private static int TypeOrder(ItemExtra extra, ExtrasConfiguration config) => extra.Kind switch
    {
        ItemExtraKinds.Document => 0,
        ItemExtraKinds.Picture => 1,
        _ => IsAudio(extra, config) ? 3 : 2,
    };

    /// <summary>The kinds the type filter offers: documents, pictures, video clips and audio.</summary>
    internal static string TypeOf(ItemExtra extra, ExtrasConfiguration config) => extra.Kind switch
    {
        ItemExtraKinds.Document => SupportingFileListDefaults.TypeDocument,
        ItemExtraKinds.Picture => SupportingFileListDefaults.TypePicture,
        _ => IsAudio(extra, config) ? SupportingFileListDefaults.TypeAudio : SupportingFileListDefaults.TypeClip,
    };

    private static bool IsAudio(ItemExtra extra, ExtrasConfiguration config) =>
        extra.Kind == ItemExtraKinds.Clip
        && config.AcceptedTypes.Audio.Contains(Path.GetExtension(extra.FilePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>The badge and the words for the type column, from the file's extension.</summary>
    internal static (string Badge, string Label) TypeWords(ItemExtra extra, bool isFolder, ExtrasConfiguration config)
    {
        var extension = Path.GetExtension(extra.FilePath).TrimStart('.').ToUpperInvariant();
        return TypeOf(extra, config) switch
        {
            SupportingFileListDefaults.TypeDocument => extension switch
            {
                "PDF" => ("PDF", "PDF"),
                "EPUB" => ("EPUB", "EPUB"),
                "TXT" or "MD" => ("TXT", "Text"),
                _ => (extension.Length == 0 ? "DOC" : extension, "Document"),
            },
            SupportingFileListDefaults.TypePicture => ("IMG", isFolder ? "Pictures" : "Image"),
            SupportingFileListDefaults.TypeAudio => ("AUDIO", "Audio"),
            _ => ("VIDEO", "Video"),
        };
    }

    private static SupportingFileRowDto ToRow(Entry entry, ExtrasConfiguration config)
    {
        var extra = entry.Primary;
        var (badge, typeLabel) = TypeWords(extra, entry.Count > 1, config);
        var role = extra.EffectiveRole;
        return new SupportingFileRowDto
        {
            Id = extra.Id.ToString("D"),
            Kind = extra.Kind switch
            {
                ItemExtraKinds.Picture => DetailExtraCardKinds.Pictures,
                ItemExtraKinds.Clip => DetailExtraCardKinds.Clip,
                _ => DetailExtraCardKinds.Document,
            },
            Type = TypeOf(extra, config),
            Badge = badge,
            TypeLabel = typeLabel,
            Title = entry.Title,
            HasOwnTitle = !string.IsNullOrWhiteSpace(extra.OwnTitle),
            FileName = Path.GetFileName(extra.FilePath),
            Description = extra.Description,
            WhereKey = entry.Scope.Key,
            WhereLabel = SupportingFileScopes.LabelOf(entry.Scope),
            ScopeSource = extra.ScopeSource,
            AlongsideKey = extra.EffectiveAlongside?.Key,
            AlongsideLabel = extra.EffectiveAlongside is { } alongside ? SupportingFileScopes.LabelOf(alongside) : null,
            GroupId = extra.GroupId?.ToString("D"),
            Group = extra.GroupName,
            Role = role,
            RoleLabel = config.LabelFor(extra.Kind, role),
            Tags = extra.TagList,
            SizeBytes = entry.Members.Sum(member => member.SizeBytes),
            PageCount = extra.PageCount,
            DurationSeconds = extra.DurationSeconds,
            ItemCount = entry.Count,
            IsMissing = entry.Members.All(member => member.IsMissingFromDisk || !File.Exists(member.FilePath)),
        };
    }

    private static List<SupportingFileWhereOptionDto> BuildWhereOptions(IReadOnlyList<Entry> entries)
    {
        var options = new List<SupportingFileWhereOptionDto>
        {
            new()
            {
                Key = SupportingFileListDefaults.WhereAll,
                Label = SupportingFileScopes.WholeTitleLabel,
                Level = SupportingFileListDefaults.WhereAll,
                FileCount = entries.Count,
            },
        };

        var scopes = new HashSet<ExtraScope> { ExtraScope.Root };
        foreach (var scope in entries.Select(entry => entry.Scope))
        {
            scopes.Add(scope);
            foreach (var ancestor in SupportingFileScopes.Ancestors(scope))
            {
                scopes.Add(ancestor);
            }
        }

        // The title level is always offered; a place with no files in it or below it is not.
        foreach (var scope in scopes.OrderBy(scope => SupportingFileScopes.SortKey(scope), StringComparer.Ordinal))
        {
            var count = scope.Level == ExtraScopeLevel.Root
                ? entries.Count(entry => entry.Scope.Level == ExtraScopeLevel.Root)
                : entries.Count(entry => SupportingFileScopes.Takes(scope, entry.Scope));
            if (count == 0 && scope.Level != ExtraScopeLevel.Root)
            {
                continue;
            }

            options.Add(new SupportingFileWhereOptionDto
            {
                Key = scope.Key,
                Label = SupportingFileScopes.LabelOf(scope),
                Level = SupportingFileScopes.LevelOf(scope),
                FileCount = count,
                ParentKey = SupportingFileScopes.Ancestors(scope).Select(ancestor => ancestor.Key).LastOrDefault(),
            });
        }

        return options;
    }
}
