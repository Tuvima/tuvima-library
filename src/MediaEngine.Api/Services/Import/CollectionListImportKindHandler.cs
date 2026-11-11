using System.Globalization;
using MediaEngine.Api.Services.Collections.ListImport;
using MediaEngine.Api.Services.Collections.ListImport.Sources;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Import;

namespace MediaEngine.Api.Services.Import;

/// <summary>
/// A list of titles: an IMDb, Letterboxd, Trakt, Goodreads or StoryGraph export, a CSV, a text file with one title per line, or a
/// list address from TMDb, MDBList, Open Library, MusicBrainz or Trakt (Collections and lists spec 4d). It is the catch-all kind: every file the other kinds do not claim is tried as a list, and a
/// file that is not a list gets the standard "Couldn't read this file" sentence.
/// </summary>
public sealed class CollectionListImportKindHandler : IImportKindHandler
{
    private readonly ListImportService _lists;
    private readonly ListInputReader? _reader;

    /// <param name="lists">Reads, matches and previews a list.</param>
    /// <param name="reader">Turns a pasted web address into a list as well as a file. Without it only files are read.</param>
    public CollectionListImportKindHandler(ListImportService lists, ListInputReader? reader = null)
    {
        _lists = lists;
        _reader = reader;
    }

    public string Kind => ImportKinds.CollectionList;

    public int Priority => 1000;

    public bool Detect(ImportSource source) => true;

    public async Task<ImportPreviewDto> PreviewAsync(ImportSource source, ImportContext context, CancellationToken ct)
    {
        ParsedList parsed;
        try
        {
            parsed = _reader is null
                ? ListFileParser.Parse(source.FileName, source.Bytes)
                : await _reader.ReadAsync(source.FileName, source.Bytes, context.ActiveProfile?.Id, ct).ConfigureAwait(false);
        }
        catch (WebListRefusedException ex)
        {
            return new ImportPreviewDto
            {
                Kind = ImportKinds.Unknown,
                KindLabel = "List address",
                Recognised = false,
                Message = ex.Message,
                MessageLinkHref = ex.LinkHref,
                MessageLinkLabel = ex.LinkLabel,
            };
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
        var notes = new List<string> { "Titles are matched to your library by their identifiers first, then by title and year." };
        if (list.WillTryAgain)
        {
            notes.Insert(0, $"{list.SourceLabel} did not answer just now. Make the collection anyway: it is saved now and Library will try again.");
        }

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
            KindLabel = list.IsWebSource ? "List address" : "Collection list",
            Recognised = true,
            Heading = list.SuggestedName,
            Subheading = $"{list.SourceLabel}, {list.EntryCount} title{(list.EntryCount == 1 ? string.Empty : "s")}",
            Facts = facts,
            Notes = notes,
            PrivacyNote = list.IsWebSource ? ImportPrivacy.AddressGoesTo(SiteOf(parsed.SourceKind)) : ImportPrivacy.FilesStayOnThisMachine,
            CanApply = false,
            ApplyLabel = "Make a collection",
            List = list,
        };
    }

    private static string SiteOf(string kind) => kind switch
    {
        ListSourceKinds.TmdbList or ListSourceKinds.TmdbCollection => "TMDb",
        ListSourceKinds.MdbList => "MDBList",
        ListSourceKinds.OpenLibrary => "Open Library",
        ListSourceKinds.MusicBrainz => "MusicBrainz",
        ListSourceKinds.TraktList or ListSourceKinds.TraktWatchlist => "Trakt",
        _ => "the site",
    };

    // A list becomes a collection through the New collection flow, which asks for its name, who sees it and where it shows.
    public Task<ImportApplyResultDto> ApplyAsync(ImportSource source, ImportContext context, CancellationToken ct) =>
        Task.FromResult(new ImportApplyResultDto(false, "Make a collection from this list in the New collection flow."));
}
