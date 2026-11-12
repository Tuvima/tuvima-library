using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Source-level checks for music-player-and-listen-fixes D7 (WP6): the Now playing page and the phone's expanded player keep the key sizes of the approved mockup
/// (unified-player-v2.html screens 13 and 15), as measured against it at 1024, 1280, 1440 and 390 wide. Whitespace is ignored, so only the values are pinned.
/// </summary>
public sealed class NowPlayingMockupStyleTests
{
    private const string PageCss = "src/MediaEngine.Web/Components/Pages/NowPlayingPage.razor.css";
    private const string PhoneCss = "src/MediaEngine.Web/Components/Player/PlaybackFullPlayer.razor.css";
    private const string TabsCss = "src/MediaEngine.Web/Components/Player/PlaybackListTabs.razor.css";
    private const string IdentityLinkCss = "src/MediaEngine.Web/Components/Player/PlaybackIdentityLink.razor.css";

    // ---- The page (mockup screen 13) ----

    [Fact]
    public void PageTopLine_IsFiftySixPixelsTall_WithTheIconsWhereTheMockupDrawsThem_ThoughTheButtonsAreFortyFourPixelTargets()
    {
        var css = Read(PageCss);

        // 6 + 44 + 6 = 56 px; 14 px of padding plus the button's 10 px of inner room puts the 24 px icons 24 px from each side.
        Assert.Contains("padding:6px14px;", Rule(css, ".np__top"));
        Assert.Contains("--playback-glyph-size:24px;", Rule(css, ".np__top"));
        Assert.Contains("padding:012px010px;", Rule(css, ".np__top ::deep .np__back"));
        Assert.Contains("min-height:var(--playback-control-target);", Rule(css, ".np__top ::deep button"));
    }

    [Fact]
    public void PageBody_IsTheMockupsTwoColumns_ASquareCoverAndTheTabsWithFortyPixelsBetween()
    {
        var css = Read(PageCss);
        var body = Rule(css, ".np__body");
        var cover = Rule(css, ".np__cover");

        Assert.Contains("grid-template-columns:minmax(0,280px)minmax(0,1fr);", body);
        Assert.Contains("gap:40px;", body);
        Assert.Contains("padding:8px48px24px;", body);
        Assert.Contains("width:min(280px,100%);", cover);
        Assert.Contains("aspect-ratio:1;", cover);
        Assert.Contains("border-radius:8px;", cover);
        Assert.Contains("box-shadow:020px50pxvar(--tl-scrim-dark-4);", cover);
    }

    [Fact]
    public void PageCoverColumn_HasNoScrollBoxOfItsOwn_SoTheCoversShadowIsNotClipped()
    {
        Assert.DoesNotContain("overflow", Rule(Read(PageCss), ".np__facts"));
    }

    [Fact]
    public void PageFacts_UseTheMockupsSizesWeightsAndLineHeight()
    {
        var css = Read(PageCss);

        var title = Rule(css, ".np__title");
        Assert.Contains("font-size:var(--tl-type-heading-size);", title);
        // Sizes and weights come from the Look type jobs; the line height and spacing follow the mockup.
        Assert.Contains("font-weight:var(--tl-type-heading-weight);", title);
        Assert.Contains("line-height:1.45;", title);
        Assert.Contains("margin:002px;", title);
        var artist = Rule(css, ".np__line");
        Assert.Contains("font-size:var(--tl-type-body-size);", artist);
        Assert.Contains("line-height:1.45;", artist);
        var album = Rule(css, ".np__sub");
        Assert.Contains("font-size:var(--tl-type-secondary-size);", album);
        Assert.Contains("line-height:1.45;", album);
        Assert.Contains("margin:0;", album);
        Assert.Contains("margin-top:14px;", Rule(css, ".np__meta"));
    }

    [Fact]
    public void PageTabsRow_PlacesTheSyncedChipTwentyEightPixelsAfterTheLastTab_AndTheUnderlineSitsAboveTheHairline()
    {
        var page = Read(PageCss);
        var tabs = Read(TabsCss);

        Assert.Contains("gap:28px;", Rule(page, ".np__tabs-row"));
        Assert.Contains("border-bottom:1pxsolidvar(--tl-border-default);", Rule(page, ".np__tabs-row"));
        // The strip draws its tabs 1 px over its own hairline; the flush strip (the page) has no hairline of its own, so it has no overlap.
        Assert.Contains("--plt-tab-overlap:0px;", Rule(tabs, ".plt--flush"));
        Assert.Contains("margin:00var(--plt-tab-overlap,-1px);", Rule(tabs, ".plt ::deep .plt__tab"));
    }

    [Fact]
    public void IdentityLinks_AreNotUnderlinedAtRest_AsTheMockupDrawsThem()
    {
        Assert.Contains("text-decoration:none;", Rule(Read(IdentityLinkCss), ".playback-identity-owner ::deep .playback-identity-link"));
    }

    // ---- The phone's expanded player (mockup screen 15) ----

    [Fact]
    public void PhoneSpacing_IsTheMockupsStack_WithOnlyAShortWindowTighteningIt()
    {
        var css = Read(PhoneCss);
        var owner = Rule(css, ".playback-full-owner");

        Assert.Contains("--playback-full-pad-x:16px;", owner);
        Assert.Contains("--playback-full-pad-y:16px;", owner);
        Assert.Contains("--playback-full-art-gap:20px;", owner);
        Assert.Contains("--playback-full-transport-gap:20px;", owner);
        Assert.Contains("--playback-full-transport-top:8px;", owner);
        Assert.Contains("--playback-full-tools-gap:18px;", owner);
        // The mockup's own 640 px phone fits whole; the tightening starts below 600 px tall, on the supported 600 scale.
        Assert.Contains("@media(max-height:600px)", Compact(css));
        Assert.DoesNotContain("max-height:640px", Compact(css));
    }

    [Fact]
    public void PhoneTopLine_HasTwentyFourPixelIconsAtThePadding_ThoughItsButtonsAreFortyFourPixelTargets()
    {
        var top = Rule(Read(PhoneCss), ".playback-full__top");

        Assert.Contains("min-height:44px;", top);
        Assert.Contains("margin:-10px;", top);
        Assert.Contains("--playback-glyph-size:24px;", top);
    }

    [Fact]
    public void PhoneArtwork_IsTwoHundredSixtyPixelsWithTwentyAboveAndBelow_NotStretchedToFillTheWindow()
    {
        var css = Read(PhoneCss);
        var art = Rule(css, ".playback-full__art");

        Assert.Contains("flex:01auto;", art);
        Assert.Contains("margin:var(--playback-full-art-gap)auto;", art);
        Assert.Contains("max-width:min(260px,100%);", Rule(css, ".playback-full__art img,.playback-full__art-placeholder"));
        Assert.Contains("width:260px;", Rule(css, ".playback-full__art-placeholder"));
        Assert.Contains("height:260px;", Rule(css, ".playback-full__art-placeholder"));
    }

    [Fact]
    public void PhoneTitleLine_UsesTheMockupsTypeAndPutsTheFortyPixelFillsFourPixelsApart()
    {
        var css = Read(PhoneCss);

        Assert.Contains("font-size:18px;", Rule(css, ".playback-full__title"));
        Assert.Contains("font-weight:600;", Rule(css, ".playback-full__title"));
        Assert.Contains("line-height:1.45;", Rule(css, ".playback-full__title"));
        Assert.Contains("font-size:14px;", Rule(css, ".playback-full__secondary"));
        Assert.Contains("line-height:1.45;", Rule(css, ".playback-full__secondary"));
        Assert.Contains("gap:4px;", Rule(css, ".playback-full__identity"));
        // 44 px buttons side by side draw 40 px fills (2 px inside each) 4 px apart; the wrapper reaches 2 px past each end so the fills end where the mockup's do.
        Assert.Contains("margin-inline:-2px;", Rule(css, ".playback-full__actions"));
        Assert.Contains("--playback-icon-button-fill-inset:2px;", Rule(css, ".playback-full__actions"));
        Assert.Contains("gap:0;", Rule(css, ".playback-full__actions ::deep .playback-identity-actions"));
    }

    [Fact]
    public void PhoneTransport_HasFortyEightPixelButtonsTwentyApart_AndAFiftySixPixelWhitePlayDisc_AsTheVideoFollowUpsSizeThem()
    {
        var css = Read(PhoneCss);
        var transport = Rule(css, ".playback-full__transport");
        var play = Rule(css, ".playback-full__transport ::deep .playback-phone-play");

        Assert.Contains("gap:var(--playback-full-transport-gap);", transport);
        Assert.Contains("margin-top:var(--playback-full-transport-top);", transport);
        Assert.Contains("--playback-control-target:48px;", transport);
        Assert.Contains("--playback-control-glyph:28px;--playback-play-target:56px;--playback-play-glyph:40px;", transport);
        Assert.Contains("--playback-primary-size:56px;", Rule(css, ".playback-full__transport ::deep .playback-primary-button-shell--phone"));
        Assert.Contains("width:56px;", play);
        Assert.Contains("height:56px;", play);
        Assert.Contains("border-radius:50%;", play);
        Assert.Contains("background:var(--playback-text);", play);
        Assert.Contains("--playback-glyph-size:var(--playback-play-glyph);", play);
    }

    [Fact]
    public void PhoneTimeline_SitsTwelvePixelsUnderTheTitleLine_WithItsRailLevelWithTheTimes()
    {
        var css = Read(PhoneCss);

        Assert.Contains("margin-top:12px;", Rule(css, ".playback-full__timeline"));
        Assert.Contains("height:var(--playback-slider-hit);", Rule(css, ".playback-full__timeline ::deep .playback-range-slider__rail"));
        Assert.Contains("margin-top:var(--playback-full-tools-gap);", Rule(css, ".playback-full-owner ::deep .playback-tool-row"));
    }

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, "(?m)^" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"No rule for {selector}.");
        return Compact(match.Groups["body"].Value);
    }

    private static string Compact(string text) => Regex.Replace(text, @"\s+", string.Empty);

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(
        FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("MediaEngine.slnx not found above the test output folder.");
    }
}
