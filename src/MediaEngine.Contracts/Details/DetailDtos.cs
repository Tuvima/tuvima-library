namespace MediaEngine.Contracts.Details;

public sealed class DetailPageViewModel
{
    public string Id { get; init; } = string.Empty;
    public DetailEntityType EntityType { get; init; }
    public DetailPresentationContext PresentationContext { get; init; }
    public DetailEditorTarget? EditorTarget { get; init; }

    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? Tagline { get; init; }
    public string? SecondaryTitleText { get; init; }
    public string? SecondaryTitleTextKind { get; init; }
    public bool SecondaryTitleTextHasMore { get; init; }
    public string? Description { get; init; }
    public DescriptionAttributionViewModel? DescriptionAttribution { get; init; }
    public IReadOnlyList<ExternalSourceLinkViewModel> SourceLinks { get; init; } = [];
    public PersonDetailFacts? PersonDetails { get; init; }
    public DetailFactsViewModel? Facts { get; init; }

    public bool UsesEpisodeArtwork { get; init; }
    public ArtworkSet Artwork { get; init; } = new();
    public HeroBrandViewModel? HeroBrand { get; init; }
    public ProgressViewModel? Progress { get; init; }

    public IReadOnlyList<OwnedFormatViewModel> OwnedFormats { get; init; } = [];
    public MultiFormatState MultiFormatState { get; init; } = MultiFormatState.SingleFormat;
    public ReadingListeningSyncViewModel? ReadingListeningSync { get; init; }
    public ReadingListeningSyncCapabilityViewModel? SyncCapability { get; init; }
    public SequencePlacementViewModel? SequencePlacement { get; init; }

    public IReadOnlyList<MetadataPill> Metadata { get; init; } = [];
    public IReadOnlyList<DetailAction> PrimaryActions { get; init; } = [];
    public IReadOnlyList<DetailAction> SecondaryActions { get; init; } = [];
    public MediaEngine.Contracts.Progress.PersonalStatusInfo? PersonalStatus { get; set; }
    public IReadOnlyList<DetailAction> OverflowActions { get; set; } = [];

    public IReadOnlyList<CreditGroupViewModel> ContributorGroups { get; init; } = [];
    public IReadOnlyList<CreditGroupViewModel> FullContributorGroups { get; init; } = [];
    public IReadOnlyList<EntityCreditViewModel> PreviewContributors { get; init; } = [];
    public IReadOnlyList<CharacterGroupViewModel> CharacterGroups { get; init; } = [];
    public IReadOnlyList<EntityCreditViewModel> PreviewCharacters { get; init; } = [];

    public IReadOnlyList<RelationshipGroup> RelationshipStrip { get; init; } = [];
    public IReadOnlyList<DetailTab> Tabs { get; init; } = [];
    public IReadOnlyList<MediaGroupingViewModel> MediaGroups { get; init; } = [];
    public DetailPrimaryModuleViewModel PrimaryModule { get; init; } = new();
    public MusicAlbumCompanionViewModel? MusicAlbumCompanion { get; init; }

    public CanonicalIdentityStatus IdentityStatus { get; init; } = CanonicalIdentityStatus.Unknown;
    public LibraryStatus LibraryStatus { get; init; } = LibraryStatus.Unknown;
    public bool IsAdminView { get; init; }
}

public sealed class MusicAlbumCompanionViewModel
{
    public string? PrimaryArtistId { get; init; }
    public string? PrimaryArtistName { get; init; }
    public string? PrimaryArtistRoute { get; init; }
    public IReadOnlyList<MusicAlbumPreviewViewModel> MoreByAlbums { get; init; } = [];
}

public sealed class MusicAlbumPreviewViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Year { get; init; }
    public string? ArtworkUrl { get; init; }
    public string Route { get; init; } = string.Empty;
}

public sealed class DetailEditorTarget
{
    public string EntityId { get; init; } = string.Empty;
    public string EntityKind { get; init; } = "Work";
    public string ContainerMode { get; init; } = "Canonical";
    public string? InitialTab { get; init; }
    public string? InitialScope { get; init; }
}

public sealed class DescriptionAttributionViewModel
{
    public string SourceName { get; init; } = string.Empty;
    public string? SourceTitle { get; init; }
    public string? SourceUrl { get; init; }
    public string LicenseName { get; init; } = string.Empty;
    public string? LicenseUrl { get; init; }
    public DateTimeOffset? RetrievedAt { get; init; }
    public bool IsModifiedOrSummarized { get; init; }
    public string Notice { get; init; } = string.Empty;
}

public sealed class ExternalSourceLinkViewModel
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string? Tooltip { get; init; }
}

public sealed class DetailFactsViewModel
{
    public string? MediaKind { get; init; }
    public string? Year { get; init; }
    public string? ReleaseDate { get; init; }
    public string? Rating { get; init; }
    public string? ContentRating { get; init; }
    public string? Runtime { get; init; }
    public string? Duration { get; init; }
    public string? Language { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyDictionary<string, string> Identifiers { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Authors { get; init; } = [];
    public IReadOnlyList<string> Artists { get; init; } = [];
    public IReadOnlyList<string> AlbumArtists { get; init; } = [];
    public IReadOnlyList<string> Actors { get; init; } = [];
    public IReadOnlyList<string> Directors { get; init; } = [];
    public IReadOnlyList<string> Writers { get; init; } = [];
    public IReadOnlyList<string> Composers { get; init; } = [];
    public IReadOnlyList<string> Narrators { get; init; } = [];
    public IReadOnlyList<string> Illustrators { get; init; } = [];
    public IReadOnlyList<string> Producers { get; init; } = [];

    public string? ShowName { get; init; }
    public string? SeasonNumber { get; init; }
    public string? EpisodeNumber { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? Network { get; init; }
    public string? SeasonCount { get; init; }
    public string? EpisodeCount { get; init; }

    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? TrackNumber { get; init; }
    public string? TrackCount { get; init; }
    public string? DiscNumber { get; init; }
    public string? DiscCount { get; init; }
    public string? Isrc { get; init; }
    public string? Label { get; init; }
    public bool? IsExplicit { get; init; }

    public string? Series { get; init; }
    public string? SeriesPosition { get; init; }
    public string? IssueNumber { get; init; }
    public string? IssueTitle { get; init; }
    public string? Publisher { get; init; }
    public string? PageCount { get; init; }
}

public enum DetailEntityType
{
    Work,
    Movie,
    MovieSeries,
    TvShow,
    TvSeason,
    TvEpisode,
    Book,
    BookSeries,
    Audiobook,
    ComicIssue,
    ComicSeries,
    MusicAlbum,
    Person,
    Character,
    Universe,
    Collection
}

public enum DetailPresentationContext
{
    Default,
    Listen,
    Watch,
    Read,
    Comics,
    Admin,
    Search,
    Universe
}

public sealed class ArtworkSet
{
    public string? BackdropUrl { get; init; }
    public string? BannerUrl { get; init; }
    public string? PosterUrl { get; init; }
    public string? CoverUrl { get; init; }
    public string? LogoUrl { get; init; }
    public string? PortraitUrl { get; init; }
    public string? CharacterImageUrl { get; init; }
    public IReadOnlyList<string> RelatedArtworkUrls { get; init; } = [];

    public IReadOnlyList<string> DominantColors { get; init; } = [];
    public string? PrimaryColor { get; init; }
    public string? SecondaryColor { get; init; }
    public string? AccentColor { get; init; }
    public string? BackdropLeftTopColor { get; init; }
    public string? BackdropLeftMiddleColor { get; init; }
    public string? BackdropLeftBottomColor { get; init; }

    public HeroArtworkViewModel HeroArtwork { get; init; } = new();
    public ArtworkPresentationMode PresentationMode { get; init; } = ArtworkPresentationMode.GeneratedIdentity;
    public ArtworkSource Source { get; init; } = ArtworkSource.Generated;
}

public sealed class HeroArtworkViewModel
{
    public string? Url { get; init; }
    public string? SmallUrl { get; init; }
    public string? MediumUrl { get; init; }
    public string? LargeUrl { get; init; }
    public HeroArtworkMode Mode { get; init; } = HeroArtworkMode.Placeholder;
    public bool HasImage { get; init; }
    public double? AspectRatio { get; init; }
    public string? BackgroundPosition { get; init; }
    public string? MobilePosition { get; init; }
}

public enum HeroArtworkMode
{
    BackdropWithLogo = 0,
    BackdropWithRenderedTitle = 1,
    ArtworkFallback = 2,
    Placeholder = 3
}

public sealed class HeroBrandViewModel
{
    public string? Label { get; init; }
    public string? ImageUrl { get; init; }
}

public enum ArtworkPresentationMode
{
    CinematicBackdrop,
    ColorGradientFromArtwork,
    PortraitEcho,
    CollageGradient,
    GeneratedIdentity,
    PairedEditionGradient
}

public enum ArtworkSource
{
    Unknown,
    User,
    Provider,
    Generated
}

public sealed class MetadataPill
{
    public string Label { get; init; } = string.Empty;
    public string? Icon { get; init; }
    public string? Kind { get; init; }
    public string? Route { get; init; }
    public string? Tooltip { get; init; }
}

public sealed class DetailAction
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? Icon { get; init; }
    public string? Route { get; init; }
    public string? Tooltip { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsDestructive { get; init; }
    public bool IsAdminOnly { get; init; }
    public bool IsDisabled { get; init; }
    public bool IsStub { get; init; }
    public bool IsSelected { get; set; }
    public string? DisplayStyle { get; init; }
    public IReadOnlyList<DetailAction> Children { get; init; } = [];
}

public sealed class DetailTab
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool IsAdminOnly { get; init; }
}

public sealed class DetailPrimaryModuleViewModel
{
    public DetailPrimaryModuleKind Kind { get; init; } = DetailPrimaryModuleKind.None;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<string> GroupKeys { get; init; } = [];
    public bool SupportsLaneFilter { get; init; }
    public bool SupportsRoleFilter { get; init; }
}

public enum DetailPrimaryModuleKind
{
    None,
    Sequence,
    Tracks,
    Chapters,
    Episodes,
    Works,
    CollectionItems,
    Appearances
}

public sealed class PersonDetailFacts
{
    public string? WikidataQid { get; init; }
    public string? WikidataUrl { get; init; }
    public string? Biography { get; init; }
    public string? Occupation { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public string? DateOfBirth { get; init; }
    public string? DateOfDeath { get; init; }
    public string? PlaceOfBirth { get; init; }
    public string? PlaceOfDeath { get; init; }
    public string? Nationality { get; init; }
    public bool IsPseudonym { get; init; }
    public bool IsGroup { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? EnrichedAt { get; init; }
    public IReadOnlyList<PersonExternalLink> ExternalLinks { get; init; } = [];
    public IReadOnlyList<PersonRelatedLink> Aliases { get; init; } = [];
    public IReadOnlyList<PersonRelatedLink> GroupMembers { get; init; } = [];
    public IReadOnlyList<PersonRelatedLink> MemberOfGroups { get; init; } = [];
}

public sealed class PersonExternalLink
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? IconLabel { get; init; }
}

public sealed class PersonRelatedLink
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    public string? Route { get; init; }
}

public sealed class SequencePlacementViewModel
{
    public string ContainerId { get; init; } = string.Empty;
    public string? SourceContainerId { get; init; }
    public string ContainerTitle { get; init; } = string.Empty;
    public string? ContainerDescription { get; init; }
    public string? ContainerWikipediaUrl { get; init; }
    public string SelectedContainerId { get; init; } = string.Empty;
    public bool CanChooseContainer { get; init; }
    public bool CanSetDefaultContainer { get; init; }
    public IReadOnlyList<SequenceContainerOptionViewModel> AvailableContainers { get; init; } = [];
    public string? UniverseId { get; init; }
    public string? UniverseTitle { get; init; }
    public string ContainerLabel { get; init; } = "Series";
    public string ItemLabel { get; init; } = "Item";
    public string ItemPluralLabel { get; init; } = "Items";
    public string? GroupLabel { get; init; }
    public string? CurrentGroupKey { get; init; }
    public int? PositionNumber { get; init; }
    public double? PositionSort { get; init; }
    public int? TotalKnownItems { get; init; }
    public bool HasAuthoritativeTotal { get; init; }
    public string? PositionLabel { get; init; }
    public string? PositionText { get; init; }
    public string? PositionSummary { get; init; }
    public SequenceOrderingType OrderingType { get; init; } = SequenceOrderingType.LibraryOrder;
    public SequenceItemViewModel? PreviousItem { get; init; }
    public SequenceItemViewModel CurrentItem { get; init; } = new();
    public SequenceItemViewModel? NextItem { get; init; }
    public IReadOnlyList<SequenceItemViewModel> OrderedItems { get; init; } = [];
    public IReadOnlyList<SequenceGroupViewModel> Groups { get; init; } = [];
}

public sealed class SequenceContainerOptionViewModel
{
    public string ContainerId { get; init; } = string.Empty;
    public string? SourceContainerId { get; init; }
    public string ContainerTitle { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
    public bool IsDefault { get; init; }
    public string? MediaScope { get; init; }
    public IReadOnlyList<string> EquivalentContainerIds { get; init; } = [];
}

public sealed class SetDefaultSequenceRequest
{
    public string ContainerId { get; init; } = string.Empty;
    public string? ContainerTitle { get; init; }
}

public enum SequenceOrderingType
{
    PublicationOrder,
    ReleaseOrder,
    StoryOrder,
    LibraryOrder,
    IssueNumber,
    EpisodeNumber,
    VolumeNumber
}

public sealed class SequenceGroupViewModel
{
    public int OwnedCount { get; init; }
    public int CompletedCount { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? EntityId { get; init; }
    public string? ArtworkSmallUrl { get; init; }
    public string? ArtworkUrl { get; init; }
    public string? BackgroundArtworkUrl { get; init; }
    public int? TotalKnownItems { get; init; }
    public bool HasAuthoritativeTotal { get; init; }
    public IReadOnlyList<SequenceItemViewModel> Items { get; init; } = [];
}

public sealed class SequenceItemViewModel
{
    public MediaEngine.Contracts.Display.DisplayEpisodeContextDto? EpisodeContext { get; init; }
    public double? ProgressPercent { get; init; }
    public double? PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public double? RemainingSeconds { get; init; }
    public string? ProgressLabel { get; init; }
    public string Id { get; init; } = string.Empty;
    public DetailEntityType EntityType { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ArtworkUrl { get; init; }
    public string? Route { get; init; }
    public string? Description { get; init; }
    public string? Duration { get; init; }
    public string? PublicationDate { get; init; }
    public int? PositionNumber { get; init; }
    public double? PositionSort { get; init; }
    public string? PositionLabel { get; init; }
    public string? PositionText { get; init; }
    public string? GroupKey { get; init; }
    public string? GroupTitle { get; init; }
    public string MembershipScope { get; init; } = "MainSequence";
    public bool IsCurrent { get; init; }
    public bool IsOwned { get; init; }
    public LibraryProgressState ProgressState { get; init; } = LibraryProgressState.Unknown;
}

public sealed class OwnedFormatViewModel
{
    public string Id { get; init; } = string.Empty;
    public MediaFormatType FormatType { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? CoverUrl { get; init; }
    public string? EditionTitle { get; init; }
    public string? Publisher { get; init; }
    public string? ReleaseDate { get; init; }
    public string? PrimaryContributor { get; init; }
    public string? FileFormat { get; init; }
    public string? Runtime { get; init; }
    public int? PageCount { get; init; }
    public int? ChapterCount { get; init; }
    public ProgressViewModel? Progress { get; init; }
    public IReadOnlyList<DetailAction> Actions { get; init; } = [];
}

public enum MediaFormatType
{
    Ebook,
    Audiobook,
    Paperback,
    Hardcover,
    ComicIssue,
    ComicVolume,
    Movie,
    TvSeries,
    MusicAlbum
}

public enum MultiFormatState
{
    SingleFormat,
    MultipleFormatsSeparateProgress,
    MultipleFormatsSyncAvailable,
    MultipleFormatsSyncEnabled,
    MultipleFormatsSyncUnavailable
}

public enum SyncCapabilityState
{
    NotApplicable,
    Unavailable,
    AvailableNotEnabled,
    Enabled,
    NeedsReview,
    Failed
}

public sealed class ReadingListeningSyncCapabilityViewModel
{
    public SyncCapabilityState State { get; init; } = SyncCapabilityState.NotApplicable;
    public string? Reason { get; init; }
    public double? EstimatedConfidence { get; init; }
    public string? TextEditionId { get; init; }
    public string? AudioEditionId { get; init; }
    public DetailAction? EnableAction { get; init; }
    public DetailAction? PreviewAction { get; init; }
    public DetailAction? SettingsAction { get; init; }
}

public sealed class ReadingListeningSyncViewModel
{
    public string WorkId { get; init; } = string.Empty;
    public string TextEditionId { get; init; } = string.Empty;
    public string AudioEditionId { get; init; } = string.Empty;
    public SyncCapabilityState Status { get; init; }
    public double Confidence { get; init; }
    public string? CurrentChapterTitle { get; init; }
    public double PercentComplete { get; init; }
    public TextPositionViewModel? TextPosition { get; init; }
    public AudioPositionViewModel? AudioPosition { get; init; }
    public IReadOnlyList<ChapterSyncRowViewModel> ChapterMap { get; init; } = [];
}

public sealed class ProgressViewModel
{
    public double Percent { get; init; }
    public DetailProgressKind Kind { get; init; }
    public string? Label { get; init; }
    public string? ContextLabel { get; init; }
    public string? PercentLabel { get; init; }
    public string? RemainingLabel { get; init; }
    public string? SecondaryLabel { get; init; }
}

public enum DetailProgressKind
{
    Unknown,
    Reading,
    Listening,
    Watching,
}

public sealed class TextPositionViewModel
{
    public string? Label { get; init; }
}

public sealed class AudioPositionViewModel
{
    public string? Label { get; init; }
}

public sealed class ChapterSyncRowViewModel
{
    public string Title { get; init; } = string.Empty;
    public string? TextPosition { get; init; }
    public string? AudioPosition { get; init; }
    public string? Status { get; init; }
}

public sealed class EntityCreditViewModel
{
    public bool IsUpdatingDetails { get; set; }
    public IReadOnlyList<int> Seasons { get; init; } = [];
    public string EntityId { get; init; } = string.Empty;
    public RelatedEntityType EntityType { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public string? FallbackInitials { get; init; }
    public string PrimaryRole { get; init; } = string.Empty;
    public string? SecondaryRole { get; init; }
    public string? CharacterName { get; init; }
    public string? CharacterEntityId { get; init; }
    public string? CharacterImageUrl { get; init; }
    public int SortOrder { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsCanonical { get; init; }
    public string? SourceName { get; init; }
    public string? SourceId { get; init; }
}

public enum RelatedEntityType
{
    Person,
    Character,
    Organization,
    Group,
    Publisher,
    Label,
    Universe,
    Series
}

public sealed class CreditGroupViewModel
{
    public string Title { get; init; } = string.Empty;
    public CreditGroupType GroupType { get; init; }
    public IReadOnlyList<EntityCreditViewModel> Credits { get; init; } = [];
    public int DisplayPriority { get; init; }
    public bool IsInitiallyExpanded { get; init; } = true;
    public int InitialVisibleCount { get; init; } = 8;
}

public enum CreditGroupType
{
    Cast,
    Directors,
    Writers,
    Producers,
    Authors,
    Narrators,
    Translators,
    Editors,
    Illustrators,
    CreativeTeam,
    MusicCredits,
    RelatedPeople,
    Collaborators,
    Organizations,
    Publishers,
    Labels,
    PrimaryArtists,
    FeaturedArtists,
    BandMembers,
    RelatedArtists
}

public sealed class CharacterGroupViewModel
{
    public string Title { get; init; } = string.Empty;
    public CharacterGroupType GroupType { get; init; }
    public IReadOnlyList<EntityCreditViewModel> Characters { get; init; } = [];
}

public enum CharacterGroupType
{
    MainCharacters,
    SupportingCharacters,
    Villains,
    Teams,
    Factions,
    Houses,
    Organizations
}

public sealed class RelationshipGroup
{
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<RelatedEntityChip> Items { get; init; } = [];
}

public sealed class RelatedEntityChip
{
    public string Id { get; init; } = string.Empty;
    public RelatedEntityType EntityType { get; init; }
    public string Label { get; init; } = string.Empty;
    public string? Route { get; init; }
}

public sealed class MediaGroupingViewModel
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<MediaGroupingItemViewModel> Items { get; init; } = [];
    public int OwnedCount { get; init; }
    public int TotalCount { get; init; }
    public int MissingCount { get; init; }
    public double CompletionPercent { get; init; }
    public bool InitiallyCollapsed { get; init; }
}

public sealed class MediaGroupingItemViewModel
{
    public string Id { get; init; } = string.Empty;
    public DetailEntityType EntityType { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? Description { get; init; }
    public string? ArtworkUrl { get; init; }
    public string? TrackNumber { get; init; }
    public string? Duration { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Artist { get; init; }
    public string? AssetId { get; init; }
    public int? ChapterIndex { get; init; }
    public double? StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public double? ResumePositionSeconds { get; init; }
    public bool IsExplicit { get; init; }
    public string? Quality { get; init; }
    public double? ProgressPercent { get; init; }
    public string? Lane { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<MetadataPill> Metadata { get; init; } = [];
    public IReadOnlyList<DetailAction> Actions { get; init; } = [];
    public bool IsOwned { get; init; } = true;
    public bool IsFavorite { get; init; }
    public LibraryProgressState ProgressState { get; init; } = LibraryProgressState.Unknown;
}

public enum CanonicalIdentityStatus
{
    Unknown,
    LocalOnly,
    ProviderMatched,
    WikidataLinked,
    NeedsReview
}

public enum LibraryStatus
{
    Unknown,
    Owned,
    PartiallyOwned,
    Unowned,
    InProgress,
    Completed
}

public enum LibraryProgressState
{
    Unknown,
    Unstarted,
    InProgress,
    Completed,
    Missing,
    Unowned
}
