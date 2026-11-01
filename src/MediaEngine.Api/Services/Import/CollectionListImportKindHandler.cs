using System.Globalization;
using MediaEngine.Api.Services.Collections.ListImport;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Import;

namespace MediaEngine.Api.Services.Import;

/// <summary>
/// A list of titles: an IMDb, Letterboxd, Trakt, Goodreads or StoryGraph export, a CSV, or a text file with one title per line
/// (Collections and lists spec 4d). It is the catch-all kind: every file the other kinds do not claim is tried as a list, and a
/// file that is not a list gets the standard "Couldn't read this file" sentence.
/// </summary>
public sealed class CollectionListImportKindHandler : IImportKindHandler
{
    private readonly ListImportService _lists;

    public CollectionListImportKindHandler(ListImportService lists) => _lists = lists;

    public string Kind => ImportKinds.CollectionList;

    public int Priority => 1000;

    public bool Detect(ImportSource source) => true;

    public async Task<ImportPreviewDto> PreviewAsync(ImportSource source, ImportContext context, CancellationToken ct)
    {
        ParsedList parsed;
        try
        {
            parsed = ListFileParser.Parse(source.FileName, source.Bytes);
        }
        catch (ListFileNotUnderstoodException ex)
        {
            return new ImportPreviewDto
            {
                Kind = ImportKinds.Unknown,
                KindLabel = "Unknown file",
                Recognised = false,
                Message = ex.Message,
            };
        }

        var list = await _lists.PreviewAsync(source.FileName, parsed, shelf: null, overrides: [], context.AllowedWorkIds, ct).ConfigureAwait(false);
        var facts = new List<ImportFactDto>
        {
            new("Titles in the list", list.EntryCount.ToString(CultureInfo.InvariantCulture)),
            new("You own", $"{list.OwnedCount} of {list.EntryCount}"),
        };
        if (list.UnmatchedCount > 0)
        {
            facts.Add(new ImportFactDto("Couldn't match", list.UnmatchedCount.ToString(CultureInfo.InvariantCulture)));
        }

        if (list.Shelves.Count > 0)
        {
            facts.Add(new ImportFactDto("Shelves", string.Join(", ", list.Shelves.Select(shelf => shelf.Name))));
        }

        return new ImportPreviewDto
        {
            Kind = Kind,
            KindLabel = "Collection list",
            Recognised = true,
            Heading = list.SuggestedName,
            Subheading = $"{list.SourceLabel}, {list.EntryCount} title{(list.EntryCount == 1 ? string.Empty : "s")}",
            Facts = facts,
            Notes = ["Titles are matched to your library by their identifiers first, then by title and year."],
            CanApply = false,
            ApplyLabel = "Make a collection",
            List = list,
        };
    }

    // A list becomes a collection through the New collection flow, which asks for its name, who sees it and where it shows.
    public Task<ImportApplyResultDto> ApplyAsync(ImportSource source, ImportContext context, CancellationToken ct) =>
        Task.FromResult(new ImportApplyResultDto(false, "Make a collection from this list in the New collection flow."));
}
