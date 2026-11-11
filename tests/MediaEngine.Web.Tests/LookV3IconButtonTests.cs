using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Cinematic;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look v3 D9 and D12 (WP6): one icon button with three variants, and one shared light 56 px hero button on Home and on a details page.
/// The tile "..." is the Plain variant over artwork, the rated thumb is Filled at rest, and a folder of videos gets an Open button.
/// </summary>
public sealed class LookV3IconButtonTests : AsyncBunitContext
{
    private static readonly string Web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");

    public LookV3IconButtonTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(api);
        Services.AddSingleton(new PlaybackSessionController(null!, api));
        Services.AddSingleton(new DeviceContextService(api));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Web, .. parts]));

    [Fact]
    public void AnIconButtonWhoseLabelRepeatsItsTooltip_DoesNotDrawTheTooltip()
    {
        var cut = Render<AppIconButton>(p => p
            .Add(c => c.Icon, AppMaterialIcons.Outlined.MoreVert)
            .Add(c => c.Tooltip, "Rate")
            .Add(c => c.Label, "Rate"));

        Assert.Equal("true", cut.Find("[data-playback-tooltip]").GetAttribute("data-playback-tooltip-suppressed"));
        Assert.Equal("Rate", cut.Find("button").GetAttribute("aria-label"));
    }

    [Fact]
    public void AnIconOnlyButton_KeepsItsTooltip()
    {
        var cut = Render<AppIconButton>(p => p
            .Add(c => c.Icon, AppMaterialIcons.Outlined.MoreVert)
            .Add(c => c.Tooltip, "More"));

        Assert.Equal("false", cut.Find("[data-playback-tooltip]").GetAttribute("data-playback-tooltip-suppressed"));
    }

    [Fact]
    public void AnIconButtonWhoseLabelSaysSomethingElse_KeepsItsTooltip()
    {
        var cut = Render<AppIconButton>(p => p
            .Add(c => c.Icon, AppMaterialIcons.Outlined.MoreVert)
            .Add(c => c.Tooltip, "Add to My List")
            .Add(c => c.Label, "My List"));

        Assert.Equal("false", cut.Find("[data-playback-tooltip]").GetAttribute("data-playback-tooltip-suppressed"));
    }

    [Fact]
    public void ThePlainVariantOverArtworkHasAScrimAndNoBorder()
    {
        var button = Render<AppIconButton>(p => p
            .Add(c => c.Icon, AppMaterialIcons.Outlined.MoreVert)
            .Add(c => c.Tooltip, "More")
            .Add(c => c.ButtonStyle, AppButtonStyle.Text)
            .Add(c => c.OverArtwork, true)).Find("button");

        Assert.Contains("app-icon-button--plain", button.ClassName);
        Assert.Contains("app-icon-button--over-artwork", button.ClassName);

        var css = Read("wwwroot", "native-fields.css");
        var plain = Regex.Match(css, @"\.app-icon-button--plain\.app-icon-button \{[^}]*\}").Value;
        var scrim = Regex.Match(css, @"\.app-icon-button--over-artwork\.app-icon-button \{[^}]*\}").Value;
        Assert.DoesNotContain("border-color", plain);
        Assert.Contains("var(--tl-scrim-soft)", scrim);
        Assert.Contains("--tl-scrim-soft:", Read("wwwroot", "tuvima.tokens.css"));
    }

    [Fact]
    public void TheTileMenuUsesThePlainIconButtonWithAScrimAndNoBorder()
    {
        var tileActions = Read("Components", "MediaTiles", "MediaTileActions.razor");
        Assert.Contains("ButtonClass=\"media-tile-action app-icon-button app-icon-button--plain app-icon-button--over-artwork", tileActions);

        // The old local tile-menu button rules are gone; only the corner placement stays.
        var appCss = Read("wwwroot", "app.css");
        Assert.DoesNotContain(".media-tile-actions .media-tile-action {", appCss);
        Assert.DoesNotContain(".media-tile-actions .app-overflow-menu__trigger", appCss);
        Assert.Matches(@"\.media-tile-actions \{[^}]*top:[^}]*right:", appCss);
    }

    [Fact]
    public void TheRatedThumbIsFilledAtRest()
    {
        var unrated = Render<MediaRateControl>();
        Assert.Contains("app-icon-button--plain", unrated.Find("button[aria-label=Rate]").ClassName);
        Assert.DoesNotContain("app-icon-button--filled", unrated.Find("button[aria-label=Rate]").ClassName);

        var rated = Render<MediaRateControl>(p => p.Add(c => c.Positive, true));
        var thumb = rated.Find("button[aria-label=Rate]");
        Assert.Contains("app-icon-button--filled", thumb.ClassName);
        Assert.Equal("true", thumb.GetAttribute("data-rated"));
        Assert.Empty(rated.FindAll("[class*='media-rate-control__trigger']"));
        Assert.DoesNotContain(".hero-icon-circle", Read("Components", "Shared", "MediaRateControl.razor.css"));
    }

    [Fact]
    public void TheHeroMainButtonIsTheLight56PixelButtonOnHomeAndDetails()
    {
        // Details: the shared row on its own.
        var details = Render<HeroActionRow>(p => p.Add(c => c.PrimaryActions, (IReadOnlyList<DetailAction>)[new DetailAction { Key = "watch", Label = "Watch", Icon = "play_arrow", IsPrimary = true }]));
        Assert.Single(details.FindAll(".tl-detail-primary-actions .tl-detail-action--primary"));

        // Home: the carousel hands its Play button to the same row.
        var home = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [new DiscoveryHeroViewModel
        {
            Title = "Sample Talk",
            MediaKind = "Video",
            WorkId = Guid.NewGuid(),
            PrimaryActionLabel = "Play",
            PrimaryNavigationUrl = "/watch/player/x",
        }]));
        Assert.NotNull(home.FindComponent<HeroActionRow>());
        Assert.Single(home.FindAll(".tl-detail-action--primary"));

        // One height, one fill, from tokens; no second hero button rule anywhere.
        var tokens = Read("wwwroot", "tuvima.tokens.css");
        Assert.Contains("--tl-hero-action-height: 56px;", tokens);
        Assert.Contains("--tl-hero-action-bg: #F5F7FB;", tokens);
        var rowCss = Read("Components", "Details", "HeroActionRow.razor.css");
        Assert.Contains("height:var(--tl-hero-action-height)", rowCss);
        Assert.Contains("background:var(--tl-hero-action-bg)", rowCss);
        Assert.Contains("color:var(--tl-hero-action-text)", rowCss);
        Assert.DoesNotContain("accent-primary);", Regex.Match(rowCss, @"\.tl-detail-action--primary \{[^}]*\}").Value);
        Assert.DoesNotContain("tl-detail-action--primary", Read("wwwroot", "app.css"));
    }

    [Fact]
    public void AVideoFolderHeroShowsOpenAsAButtonWithAFolderIcon()
    {
        var home = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [new DiscoveryHeroViewModel
        {
            Title = "Python Course",
            MediaKind = VideoFolderWording.FolderMediaType,
            RepresentativeEntityId = Guid.NewGuid(),
            PrimaryActionLabel = "Play",
            PrimaryNavigationUrl = "/watch/player/next",
            SecondaryNavigationUrl = "/details/tvshow/folder?context=watch",
        }]));

        var open = home.Find(".hero-open-folder");
        Assert.Contains("app-icon-button--plain", open.ClassName);
        Assert.Equal("Open folder", open.GetAttribute("aria-label"));
        Assert.Equal("/details/tvshow/folder?context=watch", open.GetAttribute("href"));
        Assert.Contains("M10 4H4c-1.1 0-1.99.9-1.99 2L2 18", open.InnerHtml);
        // On Home the companions are icon-only: no label under the button, so the tooltip stays and names the action.
        Assert.Null(open.Closest(".app-icon-button-stack"));
        Assert.Equal("Open folder", open.Closest("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip"));
        Assert.Empty(home.FindAll("a.cinematic-hero-carousel__folder-link:not(.app-icon-button)"));
    }

    [Fact]
    public void NoStyleSheetOutsideTheSharedPartsDefinesAnIconButtonOrHeroButtonRule()
    {
        // The shared parts own these rules. The player files are the only older page-local buttons left; they move onto the
        // shared button with the Player v2 build, and this list only goes down.
        string[] shared =
        [
            "Components/Shared/PlaybackIconButton.razor.css",
            "Components/Details/HeroActionRow.razor.css",
        ];
        string[] pending =
        [
            "Components/Watch/VideoPlaybackHost.razor.css",
            "Components/Listen/ListenNowPlayingBar.razor.css",
        ];
        var pattern = new Regex(@"(?:icon-button|hero-icon-circle|hero-action|hero-open-folder|tl-detail-action--primary|folder-link)", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Web, "*.razor.css", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Web, path).Replace('\\', '/');
            if (relative.StartsWith("obj/") || relative.StartsWith("bin/") || shared.Contains(relative) || pending.Contains(relative))
            {
                continue;
            }

            foreach (var rule in File.ReadAllText(path).Split('{').Select(chunk => chunk.Split('}').Last()).Where(selector => pattern.IsMatch(selector)))
            {
                offenders.Add($"{relative}: {rule.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "Icon-button and hero-button rules belong to the shared parts (AppIconButton, HeroActionRow): " + string.Join(" | ", offenders));
    }
}
