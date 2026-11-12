using System.Globalization;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The Bookmarks tab of a show, season or album (universal editor v5 D37, screen Q2): one row per episode or track with how many bookmarks
/// Everyone sees on it, grouped by season, plus the show's own Export and Import. A show file names each episode by its season and episode
/// number, so it can be read by any library that has the show whatever its files are called. Every entry point is administrators only, and a
/// member in the Vault is left out, as it is everywhere else.
/// </summary>
public sealed class EditorBookmarkOverviewService(
    PlayerCatalogueScope scope,
    EditorBookmarksService bookmarks,
    IEditorFamilySource header,
    BookmarkSetService sets,
    IVaultLibraryRepository vault,
    TimeProvider? time = null,
    BookmarkFrameService? frames = null)
{
    public const string ExportedShowFileName = "everyone-bookmarks-show.zip";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed record Member(EditorFamilyGroup Group, EditorFamilyMember Entry, DisplayWorkRow Asset);

    private sealed record Loaded(
        EditorFamilyMembers Family,
        EditorBookmarksService.Title Context,
        IReadOnlyList<Member> Members,
        IReadOnlyDictionary<Guid, IReadOnlyList<EditorBookmarkRow>> RowsByWork,
        IReadOnlySet<Guid> Definitive);

    private async Task<Loaded> LoadAsync(Guid workId, CancellationToken ct)
    {
        var profile = await bookmarks.RequireAdministratorAsync(ct);
        var family = await header.GetFamilyMembersAsync(workId, ct) ?? throw new PlayerResourceDeniedException();
        var assets = await scope.AssetsAsync(ct);
        var byWork = assets.GroupBy(row => row.WorkId).ToDictionary(group => group.Key, group => group.OrderBy(row => row.AssetId).First());
        var members = new List<Member>();
        foreach (var group in family.Groups)
        {
            foreach (var entry in group.Members)
            {
                if (byWork.TryGetValue(entry.WorkId, out var asset) && !await vault.IsInVaultAsync(entry.WorkId, ct))
                {
                    members.Add(new Member(group, entry, asset));
                }
            }
        }

        var context = new EditorBookmarksService.Title(profile, [], await scope.AssetIdsAsync(ct));
        var ids = members.Select(member => member.Entry.WorkId).ToList();
        var (rows, definitive) = ids.Count == 0
            ? ((IReadOnlyList<EditorBookmarkRow>)[], (IReadOnlySet<Guid>)new HashSet<Guid>())
            : await bookmarks.ReadManyAsync(ids, context, ct);
        var rowsByWork = rows.GroupBy(row => row.WorkId).ToDictionary(group => group.Key, group => (IReadOnlyList<EditorBookmarkRow>)[.. group]);
        return new Loaded(family, context, members, rowsByWork, definitive);
    }

    private static IReadOnlyList<EditorBookmarkRow> RowsOf(Loaded loaded, Guid workId) =>
        loaded.RowsByWork.TryGetValue(workId, out var found) ? found : [];

    /// <summary>The overview of the show, season or album the item belongs to; each season is a group of its episodes.</summary>
    public async Task<EditorBookmarkOverviewDto> OverviewAsync(Guid workId, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(workId, ct);
        var groups = new List<EditorBookmarkOverviewGroupDto>();
        var total = 0;
        var definitiveCount = 0;
        foreach (var group in loaded.Family.Groups)
        {
            var rows = new List<EditorBookmarkOverviewRowDto>();
            foreach (var member in loaded.Members.Where(member => member.Group == group))
            {
                var mine = RowsOf(loaded, member.Entry.WorkId);
                var definitive = loaded.Definitive.Contains(member.Entry.WorkId);
                if (definitive)
                {
                    definitiveCount++;
                }

                rows.Add(new EditorBookmarkOverviewRowDto
                {
                    WorkId = member.Entry.WorkId,
                    Code = Code(loaded.Family.MediaType, group, member.Entry),
                    Title = member.Entry.Title,
                    Number = member.Entry.Number,
                    EveryoneCount = bookmarks.EveryoneOf(mine, definitive).Count,
                    State = definitive ? EditorBookmarkStates.Definitive : EditorBookmarkStates.Pooled,
                    ProfileCount = mine.Count,
                });
            }

            var groupTotal = rows.Sum(row => row.EveryoneCount);
            total += groupTotal;
            groups.Add(new EditorBookmarkOverviewGroupDto
            {
                GroupId = group.GroupId,
                Title = group.Title,
                Number = group.Number,
                BookmarkCount = groupTotal,
                Rows = rows,
            });
        }

        return new EditorBookmarkOverviewDto
        {
            RootId = loaded.Family.RootId,
            RootTitle = loaded.Family.RootTitle,
            EveryoneTotal = total,
            MemberCount = loaded.Members.Count,
            DefinitiveCount = definitiveCount,
            Groups = groups,
        };
    }

    /// <summary>The whole show's Global bookmarks as one file, each episode named by season and episode number.</summary>
    public async Task<BookmarkPackFileDto> ExportAsync(Guid workId, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(workId, ct);
        var episodes = new List<BookmarkShowPackEpisode>();
        var framesBytes = 0L;
        foreach (var member in loaded.Members)
        {
            if (member.Entry.Number is not { } episodeNumber)
            {
                continue;
            }

            var definitive = loaded.Definitive.Contains(member.Entry.WorkId);
            var everyone = bookmarks.EveryoneOf(RowsOf(loaded, member.Entry.WorkId), definitive)
                .Where(row => row.PositionKind == BookmarkPooling.TimeKind)
                .OrderBy(row => row.PositionSeconds)
                .Take(BookmarkPackLimits.MaximumBookmarks)
                .ToList();
            if (everyone.Count == 0)
            {
                continue;
            }

            episodes.Add(new BookmarkShowPackEpisode(
                member.Group.Number ?? 0,
                episodeNumber,
                member.Entry.Title,
                Seconds(member.Asset),
                [.. everyone.Select(row =>
                {
                    // A video bookmark carries the small picture of its moment while the file's picture budget lasts (D37).
                    byte[]? frame = null;
                    if (frames?.ReadFrameBytes(row.AssetId, row.Id) is { } bytes && framesBytes + bytes.Length <= EditorBookmarkLimits.MaximumShowFramesBytes)
                    {
                        frame = bytes;
                        framesBytes += bytes.Length;
                    }

                    // A definitive set is the administrator's own list, so its names and notes go out. A pooled list is every profile's private
                    // bookmarks merged: only the time and the name go out, never a note or description somebody wrote for themselves.
                    return new BookmarkPackBookmark(
                        row.PositionSeconds, row.EndSeconds, string.IsNullOrWhiteSpace(row.ChapterTitle) ? null : row.ChapterTitle.Trim(), null,
                        definitive ? (string.IsNullOrWhiteSpace(row.Label) ? row.Note : row.Label) : row.Label,
                        definitive ? row.Description : null,
                        Frame: frame);
                })]));
        }

        if (episodes.Count == 0)
        {
            throw new BookmarkPackInvalidException("This show has no Global bookmarks to export yet.");
        }

        var chosen = episodes.Take(EditorBookmarkLimits.MaximumShowEpisodes).ToList();
        var bytes = BookmarkShowPackArchive.Write(
            new BookmarkShowPackContent($"{loaded.Family.RootTitle}: {BookmarkSetService.ExportedSetName}", chosen),
            _time.GetUtcNow());
        return new BookmarkPackFileDto
        {
            FileName = ExportedShowFileName,
            ContentBase64 = Convert.ToBase64String(bytes),
            BookmarkCount = chosen.Sum(episode => episode.Bookmarks.Count),
        };
    }

    /// <summary>A show file set beside the show, episode by episode. Changes nothing.</summary>
    public async Task<EditorBookmarkShowCompareDto> CompareAsync(Guid workId, EditorBookmarkCompareRequestDto request, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(workId, ct);
        EditorBookmarksService.RequireFileWithinLimit(request.ContentBase64, EditorBookmarkLimits.MaximumShowFileBytes);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.ContentBase64);
        }
        catch (FormatException)
        {
            throw new BookmarkPackInvalidException("This is not a show bookmark file.");
        }

        var content = BookmarkShowPackArchive.Read(bytes);
        var episodes = new List<EditorBookmarkShowCompareEpisodeDto>();
        var unmatched = 0;
        foreach (var episode in content.Episodes.OrderBy(item => item.SeasonNumber).ThenBy(item => item.EpisodeNumber))
        {
            var member = loaded.Members.FirstOrDefault(candidate =>
                (candidate.Group.Number ?? 0) == episode.SeasonNumber && candidate.Entry.Number == episode.EpisodeNumber);
            if (member is null)
            {
                unmatched++;
                episodes.Add(new EditorBookmarkShowCompareEpisodeDto { SeasonNumber = episode.SeasonNumber, EpisodeNumber = episode.EpisodeNumber, Title = episode.Title });
                continue;
            }

            var everyone = bookmarks.EveryoneOf(RowsOf(loaded, member.Entry.WorkId), loaded.Definitive.Contains(member.Entry.WorkId))
                .Select(row => EditorBookmarksService.ToPlayerBookmark(row, loaded.Context.ProfileId))
                .ToList();
            var compare = sets.CompareEpisode(content.Name, episode.Bookmarks, Seconds(member.Asset) ?? episode.RunningTimeSeconds, everyone);
            episodes.Add(new EditorBookmarkShowCompareEpisodeDto
            {
                SeasonNumber = episode.SeasonNumber,
                EpisodeNumber = episode.EpisodeNumber,
                WorkId = member.Entry.WorkId,
                Title = member.Entry.Title,
                Compare = compare,
                NewCount = compare.Rows.Count(row => row.Closeness == BookmarkClosenessLabels.New),
            });
        }

        return new EditorBookmarkShowCompareDto
        {
            Name = content.Name,
            BookmarkCount = content.Episodes.Sum(episode => episode.Bookmarks.Count),
            UnmatchedEpisodes = unmatched,
            Episodes = episodes,
        };
    }

    private static double? Seconds(DisplayWorkRow asset) =>
        double.TryParse(asset.FileDurationSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static string Code(string mediaType, EditorFamilyGroup group, EditorFamilyMember member) => mediaType switch
    {
        "TV" when member.Number is { } episode => group.Number is { } season ? $"S{season} E{episode}" : $"E{episode}",
        "Music" when member.Number is { } track => $"Track {track}",
        _ => member.Number is { } number ? number.ToString(CultureInfo.InvariantCulture) : string.Empty,
    };
}
