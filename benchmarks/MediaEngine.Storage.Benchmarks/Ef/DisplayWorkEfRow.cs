namespace MediaEngine.Storage.Benchmarks.Ef;

/// <summary>
/// EF-side twin of <c>MediaEngine.Api.Services.Display.DisplayWorkRow</c> (src/MediaEngine.Api/Services/Display/DisplayProjectionRows.cs,
/// commit 2210bc763), property for property. The only difference: <see cref="CollectionManifestTotalCount"/> is
/// <c>int?</c>. The production type declares a non-nullable <c>int</c> for a column that is NULL for most rows;
/// Dapper leaves default(int) in that case, whereas EF refuses to read NULL into a non-nullable property (a real port
/// would simply make the production property nullable). The rows are mapped to the production type afterwards with
/// <see cref="RowMapper"/>, so every variant still returns the same <c>DisplayWorkRow</c> objects.
/// </summary>
internal sealed class DisplayWorkEfRow
{
    public bool IsUpdatingDetails { get; set; }
    public Guid WorkId { get; set; }
    public string? LibraryId { get; set; }
    public Guid? CollectionId { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public string? WorkKind { get; set; }
    public Guid RootWorkId { get; set; }
    public Guid EditionId { get; set; }
    public Guid AssetId { get; set; }
    public string? IdentityQid { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? SortTitle { get; set; }
    public string? Tagline { get; set; }
    public string? Description { get; set; }
    public string? RootDescription { get; set; }
    public string? Author { get; set; }
    public string? Artist { get; set; }
    public Guid? ArtistPersonId { get; set; }
    public string? ArtistPersonName { get; set; }
    public string? Album { get; set; }
    public string? Year { get; set; }
    public string? SeriesEndYear { get; set; }
    public string? ContentRating { get; set; }
    public string? Runtime { get; set; }
    public string? Duration { get; set; }
    public string? PageCount { get; set; }
    public string? Rating { get; set; }
    public string? Genre { get; set; }
    public string? Series { get; set; }
    public string? SeriesPosition { get; set; }
    public string? CollectionTitle { get; set; }
    public string? CollectionDescription { get; set; }
    public string? CollectionType { get; set; }
    public string? CollectionGroupByField { get; set; }
    public int? CollectionManifestTotalCount { get; set; }
    public string? Narrator { get; set; }
    public string? Publisher { get; set; }
    public string? SearchPublisher { get; set; }
    public string? Director { get; set; }
    public string? Network { get; set; }
    public string? SearchNetwork { get; set; }
    public string? CountryOfOrigin { get; set; }
    public string? Franchise { get; set; }
    public string? Source { get; set; }
    public string? Quality { get; set; }
    public string? EpisodeStillUrl { get; set; }
    public string? EpisodeStillSmallUrl { get; set; }
    public string? EpisodeStillMediumUrl { get; set; }
    public string? EpisodeStillLargeUrl { get; set; }
    public string? ShowName { get; set; }
    public string? SeasonNumber { get; set; }
    public string? EpisodeNumber { get; set; }
    public string? TrackNumber { get; set; }
    public string? CoverUrl { get; set; }
    public string? CoverSmallUrl { get; set; }
    public string? CoverMediumUrl { get; set; }
    public string? CoverLargeUrl { get; set; }
    public string? SquareUrl { get; set; }
    public string? SquareSmallUrl { get; set; }
    public string? SquareMediumUrl { get; set; }
    public string? SquareLargeUrl { get; set; }
    public string? BannerUrl { get; set; }
    public string? BannerSmallUrl { get; set; }
    public string? BannerMediumUrl { get; set; }
    public string? BannerLargeUrl { get; set; }
    public string? BackgroundUrl { get; set; }
    public string? BackgroundSmallUrl { get; set; }
    public string? BackgroundMediumUrl { get; set; }
    public string? BackgroundLargeUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? CoverState { get; set; }
    public string? SquareState { get; set; }
    public string? BannerState { get; set; }
    public string? BackgroundState { get; set; }
    public string? LogoState { get; set; }
    public string? CollectionCoverUrl { get; set; }
    public string? CollectionSquareUrl { get; set; }
    public string? CollectionBannerUrl { get; set; }
    public string? CollectionBackgroundUrl { get; set; }
    public string? CollectionLogoUrl { get; set; }
    public string? CollectionAccentColor { get; set; }
    public string? RootCoverUrl { get; set; }
    public string? RootCoverSmallUrl { get; set; }
    public string? RootCoverMediumUrl { get; set; }
    public string? RootCoverLargeUrl { get; set; }
    public string? RootSquareUrl { get; set; }
    public string? RootSquareSmallUrl { get; set; }
    public string? RootSquareMediumUrl { get; set; }
    public string? RootSquareLargeUrl { get; set; }
    public string? RootBannerUrl { get; set; }
    public string? RootBannerSmallUrl { get; set; }
    public string? RootBannerMediumUrl { get; set; }
    public string? RootBannerLargeUrl { get; set; }
    public string? RootBackgroundUrl { get; set; }
    public string? RootBackgroundSmallUrl { get; set; }
    public string? RootBackgroundMediumUrl { get; set; }
    public string? RootBackgroundLargeUrl { get; set; }
    public string? RootLogoUrl { get; set; }
    public string? RootCoverState { get; set; }
    public string? RootSquareState { get; set; }
    public string? RootBannerState { get; set; }
    public string? RootBackgroundState { get; set; }
    public string? RootLogoState { get; set; }
    public string? RootCoverWidthPx { get; set; }
    public string? RootCoverHeightPx { get; set; }
    public string? RootSquareWidthPx { get; set; }
    public string? RootSquareHeightPx { get; set; }
    public string? RootBannerWidthPx { get; set; }
    public string? RootBannerHeightPx { get; set; }
    public string? RootBackgroundWidthPx { get; set; }
    public string? RootBackgroundHeightPx { get; set; }
    public string? RootAccentColor { get; set; }
    public string? CoverWidthPx { get; set; }
    public string? CoverHeightPx { get; set; }
    public string? SquareWidthPx { get; set; }
    public string? SquareHeightPx { get; set; }
    public string? BannerWidthPx { get; set; }
    public string? BannerHeightPx { get; set; }
    public string? BackgroundWidthPx { get; set; }
    public string? BackgroundHeightPx { get; set; }
    public string? AccentColor { get; set; }
}
