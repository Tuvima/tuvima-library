using System.Globalization;
using MediaEngine.Api.Services.Collections.ListImport;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Import;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Import;

/// <summary>
/// A bookmark pack (Videos spec D46): a small ZIP with how to recognise one title and its saved positions. The shared Import
/// reads it exactly as the player does (<see cref="BookmarkPackArchive"/>), says what it holds and which title in the library it
/// is for. Adding the bookmarks needs the title's own player (its chapters and running time decide whether the cut matches), so
/// the player's Saved list hands the same file to its existing check and add steps.
/// </summary>
public sealed class BookmarkPackImportKindHandler : IImportKindHandler
{
    private readonly IListImportCandidateRepository _candidates;
    private readonly IFuzzyMatchingService _fuzzy;
    private readonly IConfigurationLoader _configuration;

    public BookmarkPackImportKindHandler(
        IListImportCandidateRepository candidates, IFuzzyMatchingService fuzzy, IConfigurationLoader configuration)
    {
        _candidates = candidates;
        _fuzzy = fuzzy;
        _configuration = configuration;
    }

    public string Kind => ImportKinds.BookmarkPack;

    public int Priority => 10;

    public bool Detect(ImportSource source)
    {
        if (!source.IsZip)
        {
            return false;
        }

        try
        {
            _ = BookmarkPackArchive.Read(source.Bytes);
            return true;
        }
        catch (BookmarkPackInvalidException)
        {
            // Not a bookmark pack (a Letterboxd ZIP, for one): the next kind gets to look at it.
            return false;
        }
    }

    public async Task<ImportPreviewDto> PreviewAsync(ImportSource source, ImportContext context, CancellationToken ct)
    {
        var pack = BookmarkPackArchive.Read(source.Bytes);

        Guid? workId = null;
        string? workTitle = null;
        if (pack.Title.Ids.Count > 0)
        {
            var candidates = await _candidates.GetCandidatesAsync(context.AllowedWorkIds, ct).ConfigureAwait(false);
            var matcher = new ListEntryMatcher(candidates, _fuzzy, _configuration.LoadScoring().AutoLinkThreshold);
            var ids = pack.Title.Ids.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (matcher.Match(new ListMatchInput(string.Empty, null, null, ids, [])) is { } match)
            {
                workId = match.WorkId;
                workTitle = candidates.FirstOrDefault(candidate => candidate.WorkId == match.WorkId)?.Title;
            }
        }

        var count = pack.Bookmarks.Count;
        var facts = new List<ImportFactDto>
        {
            new("Bookmarks", count.ToString(CultureInfo.InvariantCulture)),
            new("For", workTitle ?? "A title that is not in your library"),
        };
        if (!string.IsNullOrWhiteSpace(pack.Author))
        {
            facts.Add(new ImportFactDto("Made by", pack.Author!));
        }

        return new ImportPreviewDto
        {
            Kind = Kind,
            KindLabel = "Bookmark pack",
            Recognised = true,
            Heading = pack.Name,
            Subheading = $"{count} bookmark{(count == 1 ? string.Empty : "s")}{(workTitle is null ? string.Empty : $" for {workTitle}")}",
            Facts = facts,
            Notes =
            [
                "A pack holds how to recognise the title and each bookmark's time, chapter name, note and description. It holds no file paths, profile names or what anyone watched.",
            ],
            Message = workTitle is null
                ? "Library cannot tell which title this pack is for. Open the title you want it on and import it from the player's Saved list."
                : "Bookmark packs are added from the title's player: open it, choose More… in the bookmarks panel, then Import.",
            CanApply = false,
            BookmarkPack = new ImportBookmarkPackSummaryDto
            {
                Name = pack.Name,
                Author = pack.Author,
                BookmarkCount = count,
                WorkId = workId,
                WorkTitle = workTitle,
            },
        };
    }

    public Task<ImportApplyResultDto> ApplyAsync(ImportSource source, ImportContext context, CancellationToken ct) =>
        Task.FromResult(new ImportApplyResultDto(false, "Bookmark packs are added from the title's player."));
}
