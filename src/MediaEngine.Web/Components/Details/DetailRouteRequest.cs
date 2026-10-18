using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Components.Details;

/// <summary>
/// Describes the API request and presentation copy for a routed detail surface.
/// Route components remain responsible only for translating their route/query
/// parameters into one of these requests.
/// </summary>
public sealed record DetailRouteRequest
{
    private DetailRouteRequest(
        DetailEntityType? entityType,
        Guid entityId,
        DetailPresentationContext presentationContext,
        string pageTitleFallback,
        string productTitle,
        string notFoundTitle,
        string notFoundMessage,
        string loadKey,
        string? containerId = null,
        DetailRouteOriginNavigation? originNavigation = null,
        string? requestedTab = null,
        Guid? selectedSeasonId = null)
    {
        EntityType = entityType;
        EntityId = entityId;
        PresentationContext = presentationContext;
        PageTitleFallback = pageTitleFallback;
        ProductTitle = productTitle;
        NotFoundTitle = notFoundTitle;
        NotFoundMessage = notFoundMessage;
        ContainerId = containerId;
        OriginNavigation = originNavigation;
        LoadKey = loadKey;
        RequestedTab = requestedTab;
        SelectedSeasonId = selectedSeasonId;
    }

    public DetailEntityType? EntityType { get; }
    public Guid EntityId { get; }
    public DetailPresentationContext PresentationContext { get; }
    public string PageTitleFallback { get; }
    public string ProductTitle { get; }
    public string NotFoundTitle { get; }
    public string NotFoundMessage { get; }
    public string? ContainerId { get; }
    public DetailRouteOriginNavigation? OriginNavigation { get; }

    /// <summary>
    /// Names the Engine payload this request loads. Two requests with the same key show the same page, so a change
    /// that only touches <see cref="RequestedTab"/> or <see cref="SelectedSeasonId"/> never reloads it (D19).
    /// A season opened through its show (<c>?season=</c>) shares the show's key.
    /// </summary>
    public string LoadKey { get; }

    /// <summary>The <c>tab</c> in the page address; null when the address names none (the default tab opens).</summary>
    public string? RequestedTab { get; }

    /// <summary>The season named by the page address (<c>?season=</c>, or a <c>/details/tvseason/{id}</c> address).</summary>
    public Guid? SelectedSeasonId { get; }

    public bool CanLoad => EntityType.HasValue;

    public static DetailRouteRequest ForUnified(
        string entityType,
        Guid id,
        string? contextValue,
        Guid? episodeId = null,
        Guid? seasonId = null,
        string? tab = null)
    {
        var requestedTab = string.IsNullOrWhiteSpace(tab) ? null : tab.Trim();
        var context = ParseContext(contextValue);
        var hasParsedEntityType = TryParseEntityType(entityType, out var parsedEntityType);
        if (hasParsedEntityType
            && parsedEntityType == DetailEntityType.TvShow
            && episodeId.HasValue)
        {
            return new(
                DetailEntityType.TvEpisode,
                episodeId.Value,
                DetailPresentationContext.Watch,
                pageTitleFallback: "TV Show",
                productTitle: "Tuvima Library",
                notFoundTitle: "Episode not found",
                notFoundMessage: "This episode could not be loaded from your library.",
                loadKey: $"tvepisode|{episodeId.Value:D}|{id:D}",
                containerId: id.ToString("D"),
                originNavigation: OriginFor(DetailPresentationContext.Watch),
                requestedTab: requestedTab);
        }

        // A season has no page of its own (D13): its two addresses, /details/tvshow/{show}?season={season} and
        // /details/tvseason/{season}, both ask the Engine for the season, which answers with the show's page
        // opened on that season.
        if (hasParsedEntityType
            && ((parsedEntityType == DetailEntityType.TvShow && seasonId.HasValue)
                || parsedEntityType == DetailEntityType.TvSeason))
        {
            var viaShow = parsedEntityType == DetailEntityType.TvShow;
            return new(
                DetailEntityType.TvSeason,
                viaShow ? seasonId!.Value : id,
                DetailPresentationContext.Watch,
                pageTitleFallback: "TV Show",
                productTitle: "Tuvima Library",
                notFoundTitle: "Season not found",
                notFoundMessage: "This season could not be loaded from your library.",
                loadKey: viaShow ? TvShowKey(id) : $"tvseason|{id:D}",
                containerId: viaShow ? id.ToString("D") : null,
                originNavigation: OriginFor(DetailPresentationContext.Watch),
                requestedTab: requestedTab,
                selectedSeasonId: viaShow ? seasonId!.Value : id);
        }

        return new(
            hasParsedEntityType ? parsedEntityType : null,
            id,
            context,
            pageTitleFallback: "Details",
            productTitle: "Tuvima Library",
            notFoundTitle: "Detail page not found",
            notFoundMessage: "This item could not be loaded from your library.",
            loadKey: hasParsedEntityType
                ? (parsedEntityType == DetailEntityType.TvShow ? TvShowKey(id) : $"{parsedEntityType}|{id:D}|{context}")
                : $"unknown|{entityType}|{id:D}",
            originNavigation: OriginFor(context),
            requestedTab: requestedTab);
    }

    /// <summary>The load key of a show's page, however it was reached (with or without a season, in any context).</summary>
    public static string TvShowKey(Guid showId) => $"tvshow|{showId:D}";

    private static bool TryParseEntityType(string value, out DetailEntityType entityType)
    {
        var normalized = value
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        if (normalized.Contains("podcast", StringComparison.OrdinalIgnoreCase))
        {
            entityType = default;
            return false;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out entityType)
            && entityType != DetailEntityType.TvEpisode;
    }

    private static DetailPresentationContext ParseContext(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DetailPresentationContext.Default;
        }

        var normalized = value
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        return Enum.TryParse(normalized, ignoreCase: true, out DetailPresentationContext context)
            ? context
            : DetailPresentationContext.Default;
    }

    private static DetailRouteOriginNavigation OriginFor(DetailPresentationContext context) =>
        context switch
        {
            DetailPresentationContext.Listen => new("Back to Listen", "/listen"),
            DetailPresentationContext.Watch => new("Back to Watch", "/watch"),
            DetailPresentationContext.Read or DetailPresentationContext.Comics => new("Back to Read", "/read"),
            _ => new("Back", "/"),
        };
}

public sealed record DetailRouteOriginNavigation(string Label, string FallbackRoute);
