using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>How a group tile lays its covers out.</summary>
public enum GroupCollageArrangement
{
    /// <summary>No member has a cover: the tile is the plain tile with the kind icon.</summary>
    Empty,

    /// <summary>One row, every cover at the same height and in its own shape, centred, filled in order from the left.</summary>
    Row,

    /// <summary>Exactly two covers: one in the top-left corner, the other in the bottom-right, overlapping a little in the middle.</summary>
    Corners,
}

/// <summary>One cover of the collage: the member, its width over its height, and where it starts (Corners only).</summary>
/// <param name="Item">The member the cover belongs to.</param>
/// <param name="Aspect">Width divided by height of the cover's own shape (portrait 2/3, square 1, wide 16/9).</param>
/// <param name="Offset">How far from the left edge of the pair this cover starts, in cover heights. Always 0 in a Row.</param>
public sealed record GroupCollageCover(ArtworkStackItem Item, double Aspect, double Offset);

/// <summary>The collage for one width tier of a group tile.</summary>
/// <param name="TierCovers">The most covers this width tier shows (3, 4 or 6).</param>
/// <param name="Arrangement">Row, Corners or Empty.</param>
/// <param name="Covers">The covers actually drawn, in order.</param>
/// <param name="HiddenCount">How many titles of the group are not drawn: the number in the "+N" chip (0 draws no chip).</param>
/// <param name="AspectSum">Row: the covers' aspects added up. Corners: the pair's width in cover heights.</param>
public sealed record GroupCollagePlan(
    int TierCovers,
    GroupCollageArrangement Arrangement,
    IReadOnlyList<GroupCollageCover> Covers,
    int HiddenCount,
    double AspectSum)
{
    /// <summary>The gaps between neighbouring covers in a Row (none in Corners).</summary>
    public int GapCount => Arrangement == GroupCollageArrangement.Row ? Math.Max(0, Covers.Count - 1) : 0;
}

/// <summary>Where one cover lands inside the tile, in pixels from the tile's top-left corner.</summary>
public readonly record struct GroupCollageBox(double Left, double Top, double Width, double Height);

/// <summary>
/// The group tile's cover collage rule (issue 1175, also 1160, 1173, 1174). One landscape tile for a collection, Series or Universe:
/// covers keep their native shape (portrait book or film, square album, wide still), share one height, are never cropped or stretched,
/// and media types may mix. How many are drawn depends on the tile's width: 3, then 4, then at most 6, and a "+N" chip counts the rest.
/// Two covers sit in opposite corners and overlap slightly. The name sits on a solid band under the covers, so it reads on any cover.
/// <para>
/// The component draws the plan for every width tier and the stylesheet picks one with a container query on the tile's own width, so a
/// tile is right whatever width its place gives it (320 px on a desktop shelf, wider in a browse grid, narrower on a phone). The
/// thresholds and the numbers in <see cref="Measure"/> are the ones in <c>GroupCoverCollage.razor.css</c>; a test keeps them together.
/// </para>
/// </summary>
public static class GroupCollageLayout
{
    /// <summary>Below this tile width (px) a tile draws up to <see cref="NarrowCovers"/> covers.</summary>
    public const double MediumFromWidthPx = 300;

    /// <summary>From this tile width (px) a tile draws up to <see cref="WideCovers"/> covers.</summary>
    public const double WideFromWidthPx = 380;

    public const int NarrowCovers = 3;
    public const int MediumCovers = 4;
    public const int WideCovers = 6;

    /// <summary>The most covers any tile draws; a composer never needs to hand over more.</summary>
    public const int MaxCovers = WideCovers;

    /// <summary>The width tiers, narrowest first.</summary>
    public static IReadOnlyList<int> Tiers { get; } = [NarrowCovers, MediumCovers, WideCovers];

    /// <summary>Space between the tile edge and the covers on the left, right and top, in px.</summary>
    public const double InsetPx = 6;

    /// <summary>Height of the solid name band at the bottom of the tile, in px.</summary>
    public const double BandPx = 48;

    /// <summary>Space between the covers and the band, in px; the covers area ends <see cref="InsetPx"/> above the band.</summary>
    public const double BottomReservePx = BandPx + InsetPx;

    /// <summary>Gap between neighbouring covers in a Row, in px.</summary>
    public const double GapPx = 5;

    /// <summary>How much of the narrower cover's width the two corner covers overlap by.</summary>
    public const double CornerOverlap = 0.12;

    /// <summary>The share of the covers area's height the two corner covers fill (they sit 8 percent apart vertically).</summary>
    public const double CornerHeightShare = 0.92;

    /// <summary>The tile is 16 by 9.</summary>
    public const double TileAspect = 16d / 9;

    /// <summary>How many covers a tile of this width draws: 3 under 300 px, 4 under 380 px, otherwise 6.</summary>
    public static int CoverCountForWidth(double tileWidthPx) =>
        tileWidthPx < MediumFromWidthPx ? NarrowCovers
        : tileWidthPx < WideFromWidthPx ? MediumCovers
        : WideCovers;

    /// <summary>A cover's own shape as width over height: portrait 2/3, square 1, wide 16/9.</summary>
    public static double AspectOf(ArtworkShape shape) => shape switch
    {
        ArtworkShape.Square => 1d,
        ArtworkShape.Wide => 16d / 9,
        _ => 2d / 3,
    };

    /// <summary>The plan for a tile of the given width.</summary>
    public static GroupCollagePlan Plan(IReadOnlyList<ArtworkStackItem> members, int totalCount, double tileWidthPx) =>
        PlanForTier(members, totalCount, CoverCountForWidth(tileWidthPx));

    /// <summary>The plans for all three width tiers, narrowest first; the component draws them together and CSS picks one.</summary>
    public static IReadOnlyList<GroupCollagePlan> PlansForAllTiers(IReadOnlyList<ArtworkStackItem> members, int totalCount) =>
        Tiers.Select(tier => PlanForTier(members, totalCount, tier)).ToList();

    /// <summary>
    /// The plan for one tier. Members without a picture are skipped; the first covers in order are drawn. Exactly two covers are the
    /// Corners arrangement at every width; anything else is a Row. <paramref name="totalCount"/> is every title the group holds, so
    /// "+N" counts titles whether or not they have a cover.
    /// </summary>
    public static GroupCollagePlan PlanForTier(IReadOnlyList<ArtworkStackItem> members, int totalCount, int tierCovers)
    {
        ArgumentNullException.ThrowIfNull(members);
        var withPictures = members.Where(member => !string.IsNullOrWhiteSpace(member.ImageUrl)).ToList();
        if (withPictures.Count == 0)
        {
            return new GroupCollagePlan(tierCovers, GroupCollageArrangement.Empty, [], 0, 0);
        }

        var shown = withPictures.Take(Math.Min(tierCovers, MaxCovers)).ToList();
        var hidden = Math.Max(0, Math.Max(totalCount, withPictures.Count) - shown.Count);
        if (shown.Count == 2)
        {
            var first = AspectOf(shown[0].Shape);
            var second = AspectOf(shown[1].Shape);
            var secondStart = first - (CornerOverlap * Math.Min(first, second));
            return new GroupCollagePlan(
                tierCovers,
                GroupCollageArrangement.Corners,
                [new GroupCollageCover(shown[0], first, 0), new GroupCollageCover(shown[1], second, secondStart)],
                hidden,
                secondStart + second);
        }

        var covers = shown.Select(member => new GroupCollageCover(member, AspectOf(member.Shape), 0)).ToList();
        return new GroupCollagePlan(tierCovers, GroupCollageArrangement.Row, covers, hidden, covers.Sum(cover => cover.Aspect));
    }

    /// <summary>
    /// Where each cover lands in a tile of the given width, with the same arithmetic the stylesheet does with container units. Every
    /// cover has one height; the row never reaches past the tile's edges; nothing is cropped or stretched (width / height is the
    /// cover's own aspect).
    /// </summary>
    public static IReadOnlyList<GroupCollageBox> Measure(GroupCollagePlan plan, double tileWidthPx)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Arrangement == GroupCollageArrangement.Empty)
        {
            return [];
        }

        var areaWidth = tileWidthPx - (2 * InsetPx);
        var areaHeight = (tileWidthPx / TileAspect) - InsetPx - BottomReservePx;
        if (plan.Arrangement == GroupCollageArrangement.Corners)
        {
            var cornerHeight = Math.Min(CornerHeightShare * areaHeight, areaWidth / plan.AspectSum);
            var start = InsetPx + ((areaWidth - (plan.AspectSum * cornerHeight)) / 2);
            return
            [
                new GroupCollageBox(start + (plan.Covers[0].Offset * cornerHeight), InsetPx, plan.Covers[0].Aspect * cornerHeight, cornerHeight),
                new GroupCollageBox(start + (plan.Covers[1].Offset * cornerHeight), InsetPx + areaHeight - cornerHeight, plan.Covers[1].Aspect * cornerHeight, cornerHeight),
            ];
        }

        var gaps = plan.GapCount * GapPx;
        var height = Math.Min(areaHeight, (areaWidth - gaps) / plan.AspectSum);
        var left = InsetPx + ((areaWidth - ((plan.AspectSum * height) + gaps)) / 2);
        var top = InsetPx + ((areaHeight - height) / 2);
        var boxes = new List<GroupCollageBox>(plan.Covers.Count);
        foreach (var cover in plan.Covers)
        {
            var width = cover.Aspect * height;
            boxes.Add(new GroupCollageBox(left, top, width, height));
            left += width + GapPx;
        }

        return boxes;
    }
}
