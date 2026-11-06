using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Editing;

/// <summary>A link from the editor's header to the item's record at a provider.</summary>
public sealed record EditorProviderLink(string Name, string Url);

/// <summary>
/// What the editor's header and tab bar say about the item being edited (universal editor v4 D2, D3, section 3a). Pure: nothing is read
/// or changed here, so the same words appear wherever the editor opens.
/// </summary>
public static class EditorHeaderRules
{
    /// <summary>"TV episode", "Film", "Album": the kind of item, shown after its parent in the header's kind line.</summary>
    public static string KindLabel(string? mediaType, string? scopeId, string? fallback = null)
    {
        var scope = (scopeId ?? string.Empty).ToLowerInvariant();
        var tv = string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase);
        var label = scope switch
        {
            "series" when tv => "TV show",
            "series" when VideoFolderWording.IsVideoFolderMediaType(mediaType) => VideoFolderWording.KindLabel,
            "season" => "TV season",
            "episode" => "TV episode",
            "album" => "Album",
            "track" => "Song",
            "movie" => "Film",
            "series" => "Series",
            "book" => "Book",
            "issue" => "Comic issue",
            _ => null,
        };
        if (label is not null)
        {
            return label;
        }

        return mediaType?.ToLowerInvariant() switch
        {
            "movies" => "Film",
            "books" => "Book",
            "audiobooks" => "Audiobook",
            "comics" => "Comic",
            "music" => "Song",
            "tv" => "TV",
            _ => string.IsNullOrWhiteSpace(fallback) ? "Item" : fallback,
        };
    }

    /// <summary>
    /// The Contents tab belongs to an item that has children to list: a show, season, album or series (spec section 3a). It follows the
    /// family: an item the Engine reports no family for (a film, a standalone book) never gets it.
    /// </summary>
    public static bool HasContents(string? scopeId, bool hasFamily) =>
        hasFamily && scopeId is not null && scopeId.ToLowerInvariant() is "series" or "season" or "album";

    /// <summary>The links to the item's provider records that can be built from what the Engine reports; empty when none are known.</summary>
    public static IReadOnlyList<EditorProviderLink> Links(MediaEditorIdentitySummaryDto? identity, string? mediaType, string? scopeId)
    {
        var links = new List<EditorProviderLink>();
        if (identity is null)
        {
            return links;
        }

        var provider = identity.ProviderName ?? string.Empty;
        var id = identity.ProviderItemId?.Trim();
        if (!string.IsNullOrWhiteSpace(id) && id.All(character => char.IsLetterOrDigit(character) || character is '-' or '_'))
        {
            if (provider.Contains("tvdb", StringComparison.OrdinalIgnoreCase) && string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase))
            {
                var kind = (scopeId ?? string.Empty).ToLowerInvariant() switch
                {
                    "episode" => "episode",
                    "season" => "season",
                    _ => "series",
                };
                links.Add(new EditorProviderLink("TheTVDB", $"https://thetvdb.com/dataset/{kind}/{id}"));
            }
            else if (provider.Contains("tmdb", StringComparison.OrdinalIgnoreCase) || provider.Contains("themoviedb", StringComparison.OrdinalIgnoreCase))
            {
                var kind = string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
                links.Add(new EditorProviderLink("TMDB", $"https://www.themoviedb.org/{kind}/{id}"));
            }
        }

        var qid = identity.WikidataQid?.Trim();
        if (!string.IsNullOrWhiteSpace(qid) && qid.Length > 1 && qid[0] is 'Q' or 'q' && qid.Skip(1).All(char.IsDigit))
        {
            links.Add(new EditorProviderLink("Wikidata", $"https://www.wikidata.org/wiki/Q{qid[1..]}"));
        }

        return links;
    }

    /// <summary>The one line of facts under the title: the pieces that are known, in order, joined by a middle dot.</summary>
    public static string FactsLine(params string?[] pieces) =>
        string.Join(" · ", pieces.Where(piece => !string.IsNullOrWhiteSpace(piece)).Select(piece => piece!.Trim()));
}
