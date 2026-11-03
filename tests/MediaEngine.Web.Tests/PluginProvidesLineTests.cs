using Bunit;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Web.Components.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>"What it provides" (Plugins and Integrations screens v5 D53): each of the eleven kinds has its own icon, accent colour and sentence.</summary>
public sealed class PluginProvidesLineTests : BunitContext
{
    public PluginProvidesLineTests() => Services.AddLocalization();

    private static PluginSummaryResponse PluginWithEveryKind() => new()
    {
        Id = "everything",
        Name = "Everything",
        Provides = [.. PluginProvides.All],
        ProvidesCards = [.. PluginProvides.All.Select(kind => new PluginProvidesCardDto { Kind = kind, Sentence = $"Sentence for {kind}." })],
        MediaTypes = ["books", "movies"],
    };

    [Fact]
    public void CardMode_ShowsAllElevenKinds_EachWithItsOwnIconColourTokenAndSentence()
    {
        var plugin = PluginWithEveryKind();

        var cut = Render<PluginProvidesLine>(parameters => parameters
            .Add(p => p.Plugin, plugin)
            .Add(p => p.Mode, PluginProvidesMode.Card));

        var cards = cut.FindAll("[data-testid='plugin-provides-card-item']");
        Assert.Equal(11, cards.Count);
        Assert.Equal(11, cards.Select(card => card.ClassName).Distinct().Count());
        Assert.Equal(11, cards.Select(card => card.QuerySelector("svg")!.InnerHtml).Distinct().Count());
        for (var i = 0; i < PluginProvides.All.Count; i++)
        {
            var kind = PluginProvides.All[i];
            Assert.Contains($"plugin-provides-kind--{PluginProvidesLine.KindKey(kind)}", cards[i].ClassName);
            Assert.Contains(kind, cards[i].TextContent);
            Assert.Contains($"Sentence for {kind}.", cards[i].TextContent);
            Assert.Contains("Books", cards[i].TextContent);
            Assert.Contains("Movies", cards[i].TextContent);
        }
    }

    [Fact]
    public void EveryKind_HasADefinedColourTokenInTheTokensFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css")))
        {
            directory = directory.Parent;
        }

        var tokens = File.ReadAllText(Path.Combine(directory!.FullName, "src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css"));
        foreach (var kind in PluginProvides.All)
        {
            Assert.Contains($"{PluginProvidesLine.AccentToken(kind)}:", tokens);
        }
    }

    [Fact]
    public void IconsMode_ShowsColouredIconsOnly_AndNamedModeAddsTheNames()
    {
        var plugin = PluginWithEveryKind();

        var icons = Render<PluginProvidesLine>(parameters => parameters.Add(p => p.Plugin, plugin).Add(p => p.Mode, PluginProvidesMode.Icons));
        var named = Render<PluginProvidesLine>(parameters => parameters.Add(p => p.Plugin, plugin));

        Assert.Equal(11, icons.FindAll(".plugin-provides__item svg").Count);
        Assert.DoesNotContain("Identification", icons.Find(".plugin-provides").TextContent);
        Assert.Contains("Identification", named.Find(".plugin-provides").TextContent);
        Assert.Contains("Account sync", named.Find(".plugin-provides").TextContent);
    }

    [Fact]
    public void CardMode_WithNoMediaTypes_SaysEveryType()
    {
        var plugin = PluginWithEveryKind();
        plugin.MediaTypes = [];

        var cut = Render<PluginProvidesLine>(parameters => parameters.Add(p => p.Plugin, plugin).Add(p => p.Mode, PluginProvidesMode.Card));

        Assert.Contains("Every type", cut.Markup);
    }
}
