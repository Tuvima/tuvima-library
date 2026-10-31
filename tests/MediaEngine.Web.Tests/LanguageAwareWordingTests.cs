using System.Globalization;
using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Localization;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Language-aware wording: one household language and regional spelling for every screen, English as the master copy,
/// and the English word (never a key or a blank) when a translation is missing.
/// </summary>
public sealed class LanguageAwareWordingTests : AsyncBunitContext
{
    public LanguageAwareWordingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    private static string Word(string key, string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            using var provider = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            return provider.GetRequiredService<IStringLocalizer<SharedStrings>>()[key].Value;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("en-US", "Favorites")]
    [InlineData("en", "Favorites")]
    [InlineData("en-GB", "Favourites")]
    [InlineData("en-AU", "Favourites")]
    [InlineData("en-CA", "Favourites")]
    public void TheSameIdea_IsSpelledTheWayTheHouseholdSpellsIt(string culture, string expected) =>
        Assert.Equal(expected, Word("Glossary_Favorites", culture));

    [Fact]
    public void ACanadianServer_KeepsTheZSpellingsCanadiansUse()
    {
        Assert.Equal("Organizations", Word("Universe_GuideFilterOrgs", "en-CA"));
        Assert.Equal("Organisations", Word("Universe_GuideFilterOrgs", "en-GB"));
        Assert.Equal("Organisations", Word("Universe_GuideFilterOrgs", "en-AU"));
        Assert.Equal("Organizations", Word("Universe_GuideFilterOrgs", "en-US"));
    }

    [Fact]
    public void AWordWithoutATranslation_ShowsTheEnglishWordNeverTheKey()
    {
        // "Glossary_Favorites" has no Japanese copy yet.
        Assert.Equal("Favorites", Word("Glossary_Favorites", "ja"));
        Assert.DoesNotContain("Glossary_", Word("Glossary_Series", "de"));
    }

    [Theory]
    [InlineData("en", "en-US")]
    [InlineData("EN", "en-US")]
    [InlineData("en-GB", "en-GB")]
    [InlineData("en_AU", "en-AU")]
    [InlineData("en-NZ", "en-US")]
    [InlineData("fr-CA", "fr-CA")]
    [InlineData("fr-BE", "fr")]
    [InlineData("de-AT", "de")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("tlh", "en-US")]
    [InlineData("", "en-US")]
    [InlineData(null, "en-US")]
    public void AnyLanguageTag_BecomesTheClosestSupportedOne(string? tag, string expected) =>
        Assert.Equal(expected, AppCultures.Resolve(tag));

    [Fact]
    public void TheFirstRunDefault_FollowsTheBrowser_OtherwiseUsEnglish()
    {
        Assert.Equal("en-GB", AppCultures.FirstRunDefault("en", new CultureInfo("en-GB")));
        Assert.Equal("fr", AppCultures.FirstRunDefault("en", new CultureInfo("fr-FR")));
        Assert.Equal("en-US", AppCultures.FirstRunDefault("en", new CultureInfo("tlh")));
        Assert.Equal("en-US", AppCultures.FirstRunDefault(null, null));
        // A choice the administrator already made is kept.
        Assert.Equal("de", AppCultures.FirstRunDefault("de", new CultureInfo("en-GB")));
    }

    [Fact]
    public void RegionsAreOfferedOnlyForLanguagesThatHaveRegionalSpellings()
    {
        Assert.Equal(["en-US", "en-GB", "en-CA", "en-AU"], AppCultures.RegionsFor("en").OrderBy(t => t == "en-US" ? 0 : t == "en-GB" ? 1 : t == "en-CA" ? 2 : 3));
        Assert.Contains("fr-CA", AppCultures.RegionsFor("fr"));
        Assert.Empty(AppCultures.RegionsFor("de"));
    }

    [Fact]
    public void EveryOfferedCulture_IsKnownToTheRuntime() =>
        Assert.All(AppCultures.Tags, tag => Assert.NotEmpty(CultureInfo.GetCultureInfo(tag).Name));

    [Fact]
    public void ThePicker_ShowsRegionForEnglish_AndNoRegionForGerman()
    {
        Services.AddLocalization();
        var english = Render<DisplayLanguagePicker>(p => p.Add(x => x.Value, "en-GB"));
        Assert.Equal(2, english.FindAll("select").Count);

        var german = Render<DisplayLanguagePicker>(p => p.Add(x => x.Value, "de"));
        Assert.Single(german.FindAll("select"));
    }

    [Fact]
    public void ChoosingARegion_ReportsTheFullTag()
    {
        Services.AddLocalization();
        string? chosen = null;
        var cut = Render<DisplayLanguagePicker>(p => p
            .Add(x => x.Value, "en-US")
            .Add(x => x.ValueChanged, (string tag) => chosen = tag));

        cut.FindAll("select")[1].Change("en-GB");

        Assert.Equal("en-GB", chosen);
    }

    [Fact]
    public void TheLanguagePanel_SavesTheHouseholdLanguageAndRegion()
    {
        Services.AddLocalization();
        ServerGeneralSettingsDto? saved = null;
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetServerGeneralAsync),
                _ => Task.FromResult<ServerGeneralSettingsDto?>(new ServerGeneralSettingsDto(DisplayLanguage: "en-US")));
            stub.SetHandler(nameof(IEngineApiClient.UpdateServerGeneralAsync), args =>
            {
                saved = (ServerGeneralSettingsDto)args![0]!;
                return Task.FromResult(true);
            });
        }));

        var cut = Render<LanguageRegionPanel>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("select").Count));
        cut.FindAll("select")[1].Change("en-GB");

        cut.WaitForAssertion(() => Assert.Equal("en-GB", saved?.DisplayLanguage));
        Assert.Contains("/culture/set?culture=en-GB", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal);
    }
}
