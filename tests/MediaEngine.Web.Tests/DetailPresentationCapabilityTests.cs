using MediaEngine.Web.Components.Shared;
using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class DetailPresentationCapabilityTests : AsyncBunitContext
{
    public DetailPresentationCapabilityTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Theory]
    [InlineData(DetailEntityType.MusicAlbum, true, false, "tl-detail-stage--music-album")]
    [InlineData(DetailEntityType.Audiobook, false, true, "tl-detail-stage--audiobook")]
    [InlineData(DetailEntityType.Book, false, false, "tl-detail-stage--bookish")]
    [InlineData(DetailEntityType.Movie, false, false, "tl-detail-stage--watch")]
    public void Capabilities_KeepAlbumAndAudiobookPresentationDistinct(
        DetailEntityType entityType,
        bool isMusicAlbum,
        bool isAudiobook,
        string stageModifier)
    {
        var capabilities = DetailPresentationCapabilities.For(entityType);

        Assert.Equal(isMusicAlbum, capabilities.IsMusicAlbum);
        Assert.Equal(isAudiobook, capabilities.IsAudiobook);
        Assert.Equal(stageModifier, capabilities.StageModifier);
        Assert.Equal(isMusicAlbum, capabilities.SupportsCanonicalMissingItems);
    }

    [Fact]
    public void HeroGenreList_ShowsTwoGenresAndKeyboardAccessibleOverflow()
    {
        var popovers = Render<AppPopoverHost>();
        var genres = new[]
        {
            Genre("Science Fiction"),
            Genre("Neo-Noir"),
            Genre("Thriller"),
            Genre("Mystery"),
        };

        var cut = Render<HeroGenreList>(parameters => parameters
            .Add(component => component.Genres, genres));

        Assert.Equal(2, cut.FindAll(".tl-detail-hero-genre").Count);
        var overflow = cut.Find(".tl-detail-hero-genres__overflow-trigger");
        Assert.Equal("+2", overflow.TextContent.Trim());
        Assert.Equal("Show 2 more genres", overflow.GetAttribute("aria-label"));
        overflow.Click();
        Assert.Equal(2, cut.FindAll("[role='menuitem']").Count);
    }

    [Fact]
    public void HeroMetadata_PrimaryFactsKeepRatingAndDoNotRepeatBoxedStats()
    {
        var metadata = new[]
        {
            new MetadataPill { Label = "2026", Kind = "year" },
            new MetadataPill { Label = "4.6", Kind = "rating" },
            new MetadataPill { Label = "Audiobook", Kind = "type" },
            Genre("Alternative Pop"),
            Genre("Synth-Pop"),
            Genre("Electronic"),
            Genre("Dream Pop"),
        };

        var cut = Render<HeroMetadataPills>(parameters => parameters
            .Add(component => component.Metadata, metadata)
            .Add(component => component.EntityType, DetailEntityType.Audiobook)
            .Add(component => component.UsePrimaryHeroChrome, true));

        var facts = cut.FindAll(".tl-detail-watch-metadata-row--facts .tl-detail-watch-metadata-item");
        Assert.Equal(2, facts.Count);
        Assert.Equal("2026", facts[0].TextContent.Trim());
        Assert.Equal("4.6", facts[1].TextContent.Trim());
        Assert.Contains("tl-detail-watch-metadata-item--rating", facts[1].ClassList);
        Assert.Empty(cut.FindAll(".tl-detail-metadata-row"));
        Assert.DoesNotContain("Audiobook", cut.Markup);
        Assert.Equal(2, cut.FindAll(".tl-detail-hero-genre").Count);
        Assert.Equal("+2", cut.Find(".tl-detail-hero-genres__overflow-trigger").TextContent.Trim());
    }

    [Fact]
    public void HeroProgressBlock_UsesStructuredListeningProgressSemantics()
    {
        var cut = Render<HeroProgressBlock>(parameters => parameters
            .Add(component => component.Progress, new ProgressViewModel
            {
                Kind = DetailProgressKind.Listening,
                Percent = 13,
                Label = "Continue listening",
                ContextLabel = "Track 6 of 50",
                PercentLabel = "13% complete",
                RemainingLabel = "11h 46m remaining",
                SecondaryLabel = "44 tracks remaining",
            }));

        var progressbar = cut.Find("[role='progressbar']");
        Assert.Equal("13", progressbar.GetAttribute("aria-valuenow"));
        Assert.Equal("Continue listening", progressbar.GetAttribute("aria-label"));
        Assert.Contains("Track 6 of 50", cut.Markup);
        Assert.Contains("44 tracks remaining", cut.Markup);
    }

    [Fact]
    public void HeroActionRow_KeepsMusicModeExplicit()
    {
        var cut = Render<HeroActionRow>(parameters => parameters
            .Add(component => component.IsMusicAlbum, true)
            .Add(component => component.PrimaryActions, new[]
            {
                new DetailAction { Key = "play-album", Label = "Play", Icon = "play_arrow", IsPrimary = true },
                new DetailAction { Key = "shuffle", Label = "Shuffle", Icon = "shuffle", IsPrimary = true },
            }));

        Assert.Single(cut.FindAll(".tl-detail-actions"));
        Assert.Single(cut.FindAll(".tl-detail-action--primary"));
        Assert.Single(cut.FindAll("button[aria-label=Shuffle]"));
    }

    [Fact]
    public void HeroActionRow_EmbedsProgressInTheMainAction()
    {
        var cut = Render<HeroActionRow>(parameters => parameters
            .Add(component => component.PrimaryActions, new[]
            {
                new DetailAction { Key = "listen", Label = "Continue Listening", Icon = "headphones", IsPrimary = true },
            })
            .Add(component => component.Progress, new ProgressViewModel
            {
                Kind = DetailProgressKind.Listening,
                Percent = 98,
                Label = "Continue listening - 98% listened",
            }));

        var primary = cut.Find(".tl-detail-action--has-progress");
        var progress = primary.QuerySelector("[role='progressbar']");

        Assert.NotNull(progress);
        Assert.Equal("98", progress!.GetAttribute("aria-valuenow"));
        Assert.Equal("Continue listening - 98% listened", progress.GetAttribute("aria-label"));
        Assert.Contains("98% listened", primary.TextContent);
        Assert.Contains("width:98%", primary.InnerHtml);
    }

    [Fact]
    public void HeroActionRow_WatchPairKeepsProgressOnlyInsideResumeAndPreservesRestart()
    {
        var cut = Render<HeroActionRow>(p => p
            .Add(c => c.PrimaryActions, new[] { new DetailAction { Key = "watch", Label = "Resume S1 E3", IsPrimary = true } })
            .Add(c => c.OverflowActions, new[] { new DetailAction { Key = "play-from-beginning", Label = "Restart from beginning", Route = "/watch/player/123?restart=true" } })
            .Add(c => c.Progress, new ProgressViewModel { Kind = DetailProgressKind.Watching, Percent = 42 }));
        Assert.Single(cut.FindAll(".tl-detail-action--primary"));
        Assert.Single(cut.FindAll(".tl-detail-action--primary [role=progressbar]"));
        Assert.Equal("42", cut.Find("[role=progressbar]").GetAttribute("aria-valuenow"));
        Assert.Empty(cut.FindAll(".tl-detail-action--restart"));
        var restart = Assert.Single(cut.FindComponent<OverflowActionMenu>().Instance.Actions);
        Assert.Equal("Restart from beginning", restart.Label);
        Assert.EndsWith("?restart=true", restart.Route);
    }

    [Fact]
    public void HeroActionRow_RateMenuCanBeOpenedByClickAndClosesAfterSelection()
    {
        Render<AppPopoverHost>();
        DetailAction? selected = null;
        var cut = Render<HeroActionRow>(parameters => parameters
            .Add(component => component.SecondaryActions, new[]
            {
                new DetailAction
                {
                    Key = "reaction-menu",
                    Label = "Rate",
                    Icon = "thumbs_up_down",
                    Children =
                    [
                        new DetailAction { Key = "reaction-like", Label = "I like this", Icon = "thumb_up" },
                        new DetailAction { Key = "reaction-dislike", Label = "Not for me", Icon = "thumb_down" },
                    ],
                },
            })
            .Add(component => component.OnActionSelected, action => selected = action));

        var trigger = cut.Find("button[aria-label=Rate]");

        trigger.Click();

        Assert.Contains("is-open", cut.Find(".media-rate-control").ClassList);
        Assert.Equal("true", cut.Find("button[aria-label=Rate]").GetAttribute("aria-expanded"));

        cut.Find("[role='menuitemcheckbox'][aria-label='I like this']").Click();

        Assert.Equal("reaction-like", selected?.Key);
        Assert.DoesNotContain("is-open", cut.Find(".media-rate-control").ClassList);
    }

    private static MetadataPill Genre(string label) => new()
    {
        Label = label,
        Kind = "genre",
        Route = $"/search?genre={Uri.EscapeDataString(label)}",
    };
}
