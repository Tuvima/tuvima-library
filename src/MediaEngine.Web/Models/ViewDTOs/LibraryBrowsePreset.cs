namespace MediaEngine.Web.Models.ViewDTOs;

public enum LibraryLayoutMode
{
    Card,
    List,
}

public enum BrowseHeroVariant
{
    Read,
    Watch,
    Listen,
}

public enum TimelineAggregation
{
    MediaItems,
    Albums,
    TvShows,
}

public sealed record BrowseGroupingOption(
    string Value,
    string Label,
    string Icon,
    bool Disabled = false,
    string? Description = null);

/// <summary>One library chip on a gathering tab: the library's id and the name on the chip.</summary>
public sealed record BrowseLibraryChip(Guid Id, string Label);

public sealed record BrowseTabPreset
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string MediaType { get; init; }
    public IReadOnlyList<BrowseGroupingOption> GroupingOptions { get; init; } = [];
    public string DefaultGrouping { get; init; } = "all";
    public LibraryLayoutMode DefaultLayout { get; init; } = LibraryLayoutMode.Card;
    public string YearSemantic { get; init; } = "Release / publication year";
    public TimelineAggregation TimelineAggregation { get; init; } = TimelineAggregation.MediaItems;
    public string TimelineItemNoun { get; init; } = "item";

    /// <summary>The library this tab browses (a <c>BrowseLibraryDto</c> id); null for a fixed tab such as Playlists.</summary>
    public Guid? LibraryId { get; init; }

    /// <summary>
    /// The libraries a gathering tab (Watch's Videos) can narrow to, one chip each after "All"; empty for every other tab. Only libraries the
    /// profile may see are ever in the list, so a hidden library has no chip.
    /// </summary>
    public IReadOnlyList<BrowseLibraryChip> LibraryChips { get; init; } = [];

    /// <summary>Other names an old link may use for this tab (the YouTube tab and a library's own tab now live in Videos).</summary>
    public IReadOnlyList<string> AlsoKnownAs { get; init; } = [];

    /// <summary>The fixed tab whose per-tab rules (sort, columns, links) this library tab follows; null means this tab's own <see cref="Id"/>.</summary>
    public string? BehaviourId { get; init; }

    public string EffectiveBehaviourId => BehaviourId ?? Id;
}

public sealed record LibraryBrowsePreset
{
    public required string RouteBase { get; init; }
    public required string Title { get; init; }
    public required BrowseHeroVariant HeroVariant { get; init; }
    public bool UseExplicitDefaultTabRoute { get; init; }
    public IReadOnlyList<BrowseTabPreset> Tabs { get; init; } = [];
}

public sealed record BrowseHeroViewModel
{
    public required BrowseHeroVariant Variant { get; init; }
    public required string Eyebrow { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Description { get; init; }
    public string? MetaLine { get; init; }
    public string? SupportingLine { get; init; }
    public string? CoverUrl { get; init; }
    public string CoverAspectRatioCss { get; init; } = "2 / 3";
    public string? BackgroundUrl { get; init; }
    public double? ProgressPct { get; init; }
    public string PrimaryActionLabel { get; init; } = "Open";
    public string? SecondaryActionLabel { get; init; }
    public string PrimaryNavigationUrl { get; init; } = "/";
    public string? SecondaryNavigationUrl { get; init; }
}
