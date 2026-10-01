using System.Text.RegularExpressions;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    private MediaEditorScopeDto? StaticHeaderScope => _editorContext?.Scopes
        .Where(scope => scope.ScopeId is not ("file" or "edition"))
        .OrderBy(scope => scope.Order).FirstOrDefault();

    protected string StaticHeaderTitle => NavigatorRootNode?.Title
        ?? StaticHeaderScope?.DisplayTitle ?? HeaderTitle;
    protected string? StaticHeaderSubtitle => EditorMediaType == "Music"
        ? StaticHeaderScope?.DisplaySubtitle : null;
    protected string StaticHeaderArtworkShapeClass => NavigatorRootNode?.ArtworkShape switch
    {
        "square" => "is-square",
        "wide" when EditorMediaType != "TV" => "is-landscape",
        _ => EditorMediaType == "Music" ? "is-square" : "is-portrait",
    };
    protected string? StaticHeaderCoverUrl => GetContextArtworkUrl(NavigatorRootNode)
        ?? GetHeaderArtworkPreviewUrl(StaticHeaderScope, "Poster")
        ?? GetHeaderArtworkPreviewUrl(StaticHeaderScope, "CoverArt")
        ?? (StaticHeaderScope?.FieldEntityId == ActiveScope?.FieldEntityId ? CurrentCoverUrl : Request.CoverUrl);
    protected string? SmallEditorArtworkUrl(string? url) => MediaTileArtworkUrl.Sized(url, "s") ?? url;
    protected string StaticHeaderArtworkSizes => StaticHeaderArtworkShapeClass switch
    {
        "is-square" => "88px",
        "is-landscape" => "(max-width: 900px) 96px, 140px",
        _ => "(max-width: 900px) 52px, 64px",
    };
    protected string? EditorArtworkSrcSet(string? url) => MediaTileArtworkUrl.SrcSet(
        MediaTileArtworkUrl.Sized(url, "s"), MediaTileArtworkUrl.Sized(url, "m"), MediaTileArtworkUrl.Sized(url, "l"));
    protected string StaticHeaderKind => NavigatorRootNode?.NodeKind switch
    {
        "show" or "tvshow" => "TV Series",
        "series" when EditorMediaType == "TV" => "TV Series",
        "album" => "Music · Album",
        "artist" => "Music · Artist",
        "film_series" => "Movie Series",
        "series" => "Series",
        _ => NormalizeEditorHeadingLabel(StaticHeaderScope?.Label ?? HeaderKicker),
    };
    protected string? StaticHeaderYear
    {
        get
        {
            var parent = StaticHeaderScope;
            var parentYear = parent is not null && parent.FieldEntityId == ActiveScope?.FieldEntityId
                ? _detail?.Year
                : parent is not null && _scopeStates.TryGetValue(
                    BuildScopeStateKey(parent.FieldEntityId, parent.ScopeId), out var parentState)
                    ? parentState.Detail?.Year
                    : null;
            if (!string.IsNullOrWhiteSpace(parentYear)
                && Regex.IsMatch(parentYear.Trim(), @"^\d{4}(?:\s*[–-]\s*\d{4})?$"))
            {
                return parentYear.Trim();
            }

            var subtitle = parent?.DisplaySubtitle ?? NavigatorRootNode?.Subtitle;
            return !string.IsNullOrWhiteSpace(subtitle)
                && Regex.IsMatch(subtitle.Trim(), @"^\d{4}(?:\s*[–-]\s*\d{4})?$")
                ? subtitle.Trim() : null;
        }
    }
    protected string? StaticHeaderRuntime => EditorMediaType is not ("TV" or "Music")
        && StaticHeaderScope?.FieldEntityId == ActiveScope?.FieldEntityId
        ? FormatRuntimeFact(_detail?.Runtime) : null;
    protected MediaEditorIdentitySummaryDto? StaticHeaderIdentity => StaticHeaderScope?.IdentitySummary
        ?? ActiveScope?.IdentitySummary;
    protected string? ContextRootArtworkUrl => GetContextArtworkUrl(NavigatorRootNode);
    protected string ContextRootLabel => NavigatorRootNode?.Label ?? StaticHeaderScope?.Label ?? "Item";
    private string? StaticHeaderProviderUrl(MediaEditorIdentitySummaryDto identity)
    {
        if (string.IsNullOrWhiteSpace(identity.ProviderItemId)) return null;
        var provider = NormalizeProviderKey(identity.ProviderName);
        var key = provider switch
        {
            "tvdb" => "tvdb_id",
            "tmdb" => "tmdb_id",
            "musicbrainz" when StaticHeaderScope?.ScopeId == "artist" => "musicbrainz_artist_id",
            "musicbrainz" => "musicbrainz_release_id",
            "imdb" => "imdb_id",
            "comicvine" or "comic_vine" => "comicvine_id",
            _ => null,
        };
        return key is null ? null : ProviderCatalogue.GetExternalUrl(key,
            NormalizeProviderItemId(provider, identity.ProviderItemId), EditorMediaType)?.Url;
    }
    protected string HeaderProviderDisplayName(string? provider) => FormatProviderName(provider?.ToLowerInvariant(), EditorMediaType);
    protected string HeaderProviderIcon(string? provider) => ProviderCatalogue.GetMaterialIcon(provider ?? "");
    protected string? HeaderProviderLogoUrl(string? provider)
    {
        var icon = ProviderCatalogue.GetByName(provider ?? "")?.IconPath;
        return string.IsNullOrWhiteSpace(icon) ? null : "/" + icon.TrimStart('/');
    }
}
