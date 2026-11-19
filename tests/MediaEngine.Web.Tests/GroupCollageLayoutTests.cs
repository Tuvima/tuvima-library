using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The cover collage rule of a group tile (issue 1175; also 1160, 1173, 1174): how many covers by tile width (3, then 4, then at most 6,
/// the rest in a "+N" chip), two covers in opposite corners overlapping a little, and every cover in its own shape at one height, never
/// cropped or stretched and never past the tile's edge.
/// </summary>
public sealed class GroupCollageLayoutTests
{
    private const double Epsilon = 0.0001;

    [Theory]
    [InlineData(200, 3)]
    [InlineData(240, 3)]
    [InlineData(299, 3)]
    [InlineData(299.9, 3)]
    [InlineData(300, 4)]
    [InlineData(320, 4)]
    [InlineData(379.9, 4)]
    [InlineData(380, 6)]
    [InlineData(400, 6)]
    [InlineData(520, 6)]
    public void TheNumberOfCoversGrowsWithTheTileWidth_ThreeThenFourThenSix(double width, int expected)
    {
        Assert.Equal(expected, GroupCollageLayout.CoverCountForWidth(width));
    }

    [Fact]
    public void ATileNeverDrawsMoreThanSixCovers_EvenWhenTheGroupHoldsMore()
    {
        var plan = GroupCollageLayout.Plan(Members(Shapes(ArtworkShape.Portrait, 12)), totalCount: 12, tileWidthPx: 1000);

        Assert.Equal(6, plan.Covers.Count);
        Assert.Equal(6, plan.HiddenCount);
    }

    [Theory]
    [InlineData(240, 3, 7)]
    [InlineData(320, 4, 6)]
    [InlineData(400, 6, 4)]
    public void TheCoversNotDrawnAreCountedInThePlusChip(double width, int drawn, int hidden)
    {
        var plan = GroupCollageLayout.Plan(Members(Shapes(ArtworkShape.Portrait, 10)), totalCount: 10, tileWidthPx: width);

        Assert.Equal(GroupCollageArrangement.Row, plan.Arrangement);
        Assert.Equal(drawn, plan.Covers.Count);
        Assert.Equal(hidden, plan.HiddenCount);
        Assert.Equal(Enumerable.Range(0, drawn).Select(index => $"item-{index}"), plan.Covers.Select(cover => cover.Item.Id));
    }

    [Fact]
    public void TitlesWithoutAPictureAreStillCountedInThePlusChip_ButNeverDrawn()
    {
        var members = new List<ArtworkStackItem>
        {
            Member(0, ArtworkShape.Portrait),
            Member(1, ArtworkShape.Portrait, hasPicture: false),
            Member(2, ArtworkShape.Portrait),
            Member(3, ArtworkShape.Portrait),
            Member(4, ArtworkShape.Portrait),
        };

        var plan = GroupCollageLayout.Plan(members, totalCount: 9, tileWidthPx: 240);

        Assert.Equal(["item-0", "item-2", "item-3"], plan.Covers.Select(cover => cover.Item.Id));
        Assert.Equal(6, plan.HiddenCount);
    }

    [Fact]
    public void AGroupWhoseTitlesAllFit_HasNoPlusChip()
    {
        var plan = GroupCollageLayout.Plan(Members(Shapes(ArtworkShape.Portrait, 3)), totalCount: 3, tileWidthPx: 240);

        Assert.Equal(3, plan.Covers.Count);
        Assert.Equal(0, plan.HiddenCount);
    }

    [Fact]
    public void EveryCoverKeepsItsOwnShape_PortraitTwoThirdsSquareOneWideSixteenNinths()
    {
        Assert.Equal(2d / 3, GroupCollageLayout.AspectOf(ArtworkShape.Portrait), Epsilon);
        Assert.Equal(1d, GroupCollageLayout.AspectOf(ArtworkShape.Square), Epsilon);
        Assert.Equal(16d / 9, GroupCollageLayout.AspectOf(ArtworkShape.Wide), Epsilon);
    }

    [Fact]
    public void AMixedGroupAddsUpItsCoversByTheirOwnShapes_AtEveryWidth()
    {
        var shapes = new[]
        {
            ArtworkShape.Portrait, ArtworkShape.Square, ArtworkShape.Wide, ArtworkShape.Portrait, ArtworkShape.Square, ArtworkShape.Wide,
            ArtworkShape.Portrait, ArtworkShape.Portrait, ArtworkShape.Square, ArtworkShape.Wide,
        };
        var members = shapes.Select((shape, index) => Member(index, shape)).ToList();

        var narrow = GroupCollageLayout.Plan(members, 10, 240);
        var medium = GroupCollageLayout.Plan(members, 10, 320);
        var wide = GroupCollageLayout.Plan(members, 10, 400);

        Assert.Equal(2d / 3 + 1 + 16d / 9, narrow.AspectSum, Epsilon);
        Assert.Equal(2d / 3 + 1 + 16d / 9 + 2d / 3, medium.AspectSum, Epsilon);
        Assert.Equal((2d / 3) + 1 + (16d / 9) + (2d / 3) + 1 + (16d / 9), wide.AspectSum, Epsilon);
    }

    [Fact]
    public void TwoTitles_AreTheCornersArrangement_AtEveryWidth()
    {
        var members = Members(Shapes(ArtworkShape.Portrait, 2));

        foreach (var width in new[] { 240d, 320d, 400d })
        {
            var plan = GroupCollageLayout.Plan(members, 2, width);
            Assert.Equal(GroupCollageArrangement.Corners, plan.Arrangement);
            Assert.Equal(2, plan.Covers.Count);
            Assert.Equal(0, plan.HiddenCount);
        }
    }

    [Theory]
    [InlineData(240)]
    [InlineData(320)]
    [InlineData(400)]
    public void TwoTitles_SitInOppositeCorners_OneHeight_AndOverlapSlightlyWithoutLeavingTheTile(double width)
    {
        var plan = GroupCollageLayout.Plan(Members(Shapes(ArtworkShape.Portrait, 2)), 2, width);

        var boxes = GroupCollageLayout.Measure(plan, width);

        var first = boxes[0];
        var second = boxes[1];
        var areaTop = GroupCollageLayout.InsetPx;
        var areaBottom = (width / GroupCollageLayout.TileAspect) - GroupCollageLayout.BottomReservePx;
        Assert.Equal(areaTop, first.Top, Epsilon);
        Assert.Equal(areaBottom, second.Top + second.Height, Epsilon);
        Assert.True(first.Left < second.Left);
        Assert.Equal(first.Height, second.Height, Epsilon);
        // A slight overlap: the second starts inside the first, by well under a quarter of its width.
        var overlap = first.Left + first.Width - second.Left;
        Assert.InRange(overlap, Epsilon, first.Width / 4);
        Assert.True(first.Left >= GroupCollageLayout.InsetPx - Epsilon);
        Assert.True(second.Left + second.Width <= width - GroupCollageLayout.InsetPx + Epsilon);
    }

    [Fact]
    public void TwoWideTitles_StillFitAcrossTheTile_ByGettingShorter()
    {
        var members = new List<ArtworkStackItem> { Member(0, ArtworkShape.Wide), Member(1, ArtworkShape.Wide) };

        var boxes = GroupCollageLayout.Measure(GroupCollageLayout.Plan(members, 2, 240), 240);

        Assert.True(boxes[0].Left >= GroupCollageLayout.InsetPx - Epsilon);
        Assert.True(boxes[1].Left + boxes[1].Width <= 240 - GroupCollageLayout.InsetPx + Epsilon);
        Assert.Equal(boxes[0].Height, boxes[1].Height, Epsilon);
    }

    [Fact]
    public void OneTitle_IsASingleCoverCentredInTheTile()
    {
        var plan = GroupCollageLayout.Plan(Members(Shapes(ArtworkShape.Portrait, 1)), 1, 320);

        var box = Assert.Single(GroupCollageLayout.Measure(plan, 320));

        Assert.Equal(GroupCollageArrangement.Row, plan.Arrangement);
        Assert.Equal(160, box.Left + (box.Width / 2), Epsilon);
    }

    [Fact]
    public void AGroupWithNoPictures_IsEmpty_SoTheTileIsThePlainOneWithTheKindIcon()
    {
        var plan = GroupCollageLayout.Plan([Member(0, ArtworkShape.Portrait, hasPicture: false)], 4, 320);

        Assert.Equal(GroupCollageArrangement.Empty, plan.Arrangement);
        Assert.Empty(plan.Covers);
        Assert.Equal(0, plan.HiddenCount);
        Assert.Empty(GroupCollageLayout.Measure(plan, 320));
    }

    [Theory]
    [InlineData(240, ArtworkShape.Portrait)]
    [InlineData(320, ArtworkShape.Portrait)]
    [InlineData(400, ArtworkShape.Portrait)]
    [InlineData(240, ArtworkShape.Square)]
    [InlineData(400, ArtworkShape.Square)]
    [InlineData(240, ArtworkShape.Wide)]
    [InlineData(320, ArtworkShape.Wide)]
    [InlineData(400, ArtworkShape.Wide)]
    [InlineData(520, ArtworkShape.Wide)]
    public void ARowOfOneShape_HasOneHeight_KeepsEachCoversShape_AndStaysInsideTheTile(double width, ArtworkShape shape)
    {
        var plan = GroupCollageLayout.Plan(Members(Shapes(shape, 8)), 8, width);

        var boxes = GroupCollageLayout.Measure(plan, width);

        AssertRowFits(plan, boxes, width);
    }

    [Theory]
    [InlineData(240)]
    [InlineData(320)]
    [InlineData(400)]
    [InlineData(520)]
    public void AMixedRowOfBooksAlbumsAndFilms_HasOneHeight_KeepsEachCoversShape_AndStaysInsideTheTile(double width)
    {
        var shapes = new[]
        {
            ArtworkShape.Portrait, ArtworkShape.Square, ArtworkShape.Wide, ArtworkShape.Portrait, ArtworkShape.Square, ArtworkShape.Wide,
            ArtworkShape.Wide, ArtworkShape.Wide,
        };
        var plan = GroupCollageLayout.Plan(shapes.Select((shape, index) => Member(index, shape)).ToList(), 8, width);

        var boxes = GroupCollageLayout.Measure(plan, width);

        AssertRowFits(plan, boxes, width);
    }

    [Fact]
    public void ACoverHeightFollowsTheAreaWhenTheRowHasRoom_AndTheWidthWhenItDoesNot()
    {
        // Three portrait covers at 320 px (4 drawn at the 4-cover tier, so use three titles): plenty of width, so the height is the area's.
        var roomy = GroupCollageLayout.Measure(GroupCollageLayout.Plan(Members(Shapes(ArtworkShape.Portrait, 3)), 3, 320), 320);
        // Six wide covers at 400 px: no room for the area's height, so the covers get shorter to fit across.
        var tight = GroupCollageLayout.Measure(GroupCollageLayout.Plan(Members(Shapes(ArtworkShape.Wide, 6)), 6, 400), 400);

        var areaHeight = (320 / GroupCollageLayout.TileAspect) - GroupCollageLayout.InsetPx - GroupCollageLayout.BottomReservePx;
        Assert.Equal(areaHeight, roomy[0].Height, Epsilon);
        Assert.True(tight[0].Height < ((400 / GroupCollageLayout.TileAspect) - GroupCollageLayout.InsetPx - GroupCollageLayout.BottomReservePx));
    }

    [Fact]
    public void TheNumbersTheStylesheetUsesAreTheOnesTheRuleUses()
    {
        var css = File.ReadAllText(Path.Combine(ComponentsShared, "GroupCoverCollage.razor.css"));

        // Tile widths: under 300 px three covers, under 380 px four (container queries on the tile's own width).
        Assert.Contains($"(max-width: {GroupCollageLayout.WideFromWidthPx - 0.01:0.##}px)", css, StringComparison.Ordinal);
        Assert.Contains($"(max-width: {GroupCollageLayout.MediumFromWidthPx - 0.01:0.##}px)", css, StringComparison.Ordinal);
        // Layout: 6 px edges, a 48 px band with 6 px above it, 5 px between covers.
        Assert.Contains($"inset: {GroupCollageLayout.InsetPx}px {GroupCollageLayout.InsetPx}px {GroupCollageLayout.BottomReservePx}px;", css, StringComparison.Ordinal);
        Assert.Contains($"height: {GroupCollageLayout.BandPx}px;", css, StringComparison.Ordinal);
        Assert.Contains($"gap: {GroupCollageLayout.GapPx}px;", css, StringComparison.Ordinal);
        Assert.Contains($"* {GroupCollageLayout.GapPx}px)", css, StringComparison.Ordinal);
        Assert.Contains($"min({GroupCollageLayout.CornerHeightShare * 100:0}cqh", css, StringComparison.Ordinal);
    }

    private static string ComponentsShared => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "Shared"));

    private static void AssertRowFits(GroupCollagePlan plan, IReadOnlyList<GroupCollageBox> boxes, double width)
    {
        Assert.Equal(plan.Covers.Count, boxes.Count);
        var areaBottom = (width / GroupCollageLayout.TileAspect) - GroupCollageLayout.BottomReservePx;
        for (var index = 0; index < boxes.Count; index++)
        {
            // One height for the whole row, and the cover's own width over height: nothing cropped or stretched.
            Assert.Equal(boxes[0].Height, boxes[index].Height, Epsilon);
            Assert.Equal(plan.Covers[index].Aspect, boxes[index].Width / boxes[index].Height, Epsilon);
            // Inside the tile's side edges, below its top edge and above the name band.
            Assert.True(boxes[index].Left >= GroupCollageLayout.InsetPx - Epsilon, $"cover {index} starts left of the edge");
            Assert.True(boxes[index].Left + boxes[index].Width <= width - GroupCollageLayout.InsetPx + Epsilon, $"cover {index} ends right of the edge");
            Assert.True(boxes[index].Top >= GroupCollageLayout.InsetPx - Epsilon);
            Assert.True(boxes[index].Top + boxes[index].Height <= areaBottom + Epsilon, $"cover {index} reaches into the band");
            if (index > 0)
            {
                Assert.Equal(GroupCollageLayout.GapPx, boxes[index].Left - (boxes[index - 1].Left + boxes[index - 1].Width), Epsilon);
            }
        }

        // The row is centred, so the right side is never left empty (issue 1160).
        var leftSpace = boxes[0].Left;
        var rightSpace = width - (boxes[^1].Left + boxes[^1].Width);
        Assert.Equal(leftSpace, rightSpace, Epsilon);
    }

    private static IReadOnlyList<ArtworkShape> Shapes(ArtworkShape shape, int count) => Enumerable.Repeat(shape, count).ToList();

    private static IReadOnlyList<ArtworkStackItem> Members(IReadOnlyList<ArtworkShape> shapes) =>
        shapes.Select((shape, index) => Member(index, shape)).ToList();

    private static ArtworkStackItem Member(int index, ArtworkShape shape, bool hasPicture = true) => new()
    {
        Id = $"item-{index}",
        Title = $"Item {index}",
        ImageUrl = hasPicture ? $"/art/{index}.jpg" : string.Empty,
        Shape = shape,
    };
}
