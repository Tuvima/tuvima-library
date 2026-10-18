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
        string? containerId = null,
        DetailRouteOriginNavigation? originNavigation = null)
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
    public bool CanLoad => EntityType.HasValue;

    public static DetailRouteRequest ForUnified(
        string entityType,
        Guid id,
        string? contextValue,
        Guid? episodeId = null,
        Guid? seasonId = null)
    {
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
                containerId: id.ToString("D"),
                originNavigation: OriginFor(DetailPresentationContext.Watch));
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
                containerId: viaShow ? id.ToString("D") : null,
                originNavigation: OriginFor(DetailPresentationContext.Watch));
        }

        return new(
            hasParsedEntityType ? parsedEntityType : null,
            id,
            context,
            pageTitleFallback: "Details",
            productTitle: "Tuvima Library",
            notFoundTitle: "Detail page not found",
            notFoundMessage: "This item could not be loaded from your library.",
            originNavigation: OriginFor(context));
    }

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
