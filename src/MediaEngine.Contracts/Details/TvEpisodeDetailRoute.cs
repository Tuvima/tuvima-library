namespace MediaEngine.Contracts.Details;

public static class TvEpisodeDetailRoute
{
    public static string Build(Guid showId, Guid episodeId, string context = "watch")
    {
        if (showId == Guid.Empty)
        {
            throw new ArgumentException("A show identity is required.", nameof(showId));
        }
        if (episodeId == Guid.Empty)
        {
            throw new ArgumentException("An owned episode identity is required.", nameof(episodeId));
        }
        return $"/details/tvshow/{showId:D}?context={Uri.EscapeDataString(context)}&episode={episodeId:D}";
    }
}
