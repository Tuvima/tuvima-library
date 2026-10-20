using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Tests for the named value-transform libraryItem used by config-driven adapters.
/// </summary>
public class ValueTransformCatalogTests
{
    // ════════════════════════════════════════════════════════════════════════
    //  Simple transforms (no args)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("1965-06-01T00:00:00Z", "1965")]
    [InlineData("2023-12-25", "2023")]
    [InlineData("20", "20")]
    public void YearFromIso_ExtractsFourDigitYear(string input, string expected)
    {
        Assert.Equal(expected, ValueTransformCatalog.Apply("year_from_iso", input));
    }

    [Theory]
    [InlineData("2012", "2012")]
    [InlineData("Sept 2012 (reprint 2014)", "2012")]
    [InlineData("1999-03-14", "1999")]
    [InlineData("c. 1887", null)]
    [InlineData("12345", null)]
    public void FirstYear_ExtractsTheFirstPlausibleYear(string input, string? expected)
    {
        Assert.Equal(expected, ValueTransformCatalog.Apply("first_year", input));
    }

    [Theory]
    [InlineData("Book 3", "3")]
    [InlineData("Part 12 of 20", "12")]
    [InlineData("42", "42")]
    [InlineData("3.5", "3.5")]
    public void NumericPortion_ExtractsFirstNumber(string input, string expected)
    {
        Assert.Equal(expected, ValueTransformCatalog.Apply("numeric_portion", input));
    }

    [Fact]
    public void StripEntityUri_RemovesWikidataPrefix()
    {
        var result = ValueTransformCatalog.Apply("strip_entity_uri",
            "http://www.wikidata.org/entity/Q83853");
        Assert.Equal("Q83853", result);
    }

    [Fact]
    public void StripEntityUri_PassesThroughNonUri()
    {
        Assert.Equal("Q83853", ValueTransformCatalog.Apply("strip_entity_uri", "Q83853"));
    }

    [Fact]
    public void CommonsUrl_BuildsThumbnailUrl()
    {
        var result = ValueTransformCatalog.Apply("commons_url", "Frank Herbert portrait.jpg");
        Assert.Contains("commons.wikimedia.org", result);
        Assert.Contains("Special:FilePath", result);
        Assert.Contains("width=300", result);
        Assert.Contains("Frank_Herbert_portrait.jpg", Uri.UnescapeDataString(result!));
    }

    [Fact]
    public void ToString_PassesThrough()
    {
        Assert.Equal("hello", ValueTransformCatalog.Apply("to_string", "hello"));
    }

    [Theory]
    [InlineData("218149", "3.636")]
    [InlineData("60000", "1")]
    [InlineData("0", "0")]
    public void MillisecondsToMinutes_NormalizesDuration(string input, string expected)
    {
        Assert.Equal(expected, ValueTransformCatalog.Apply("milliseconds_to_minutes", input));
    }

    [Fact]
    public void MillisecondsToMinutes_RejectsInvalidInput()
    {
        Assert.Null(ValueTransformCatalog.Apply("milliseconds_to_minutes", "not-a-duration"));
    }

    [Fact]
    public void StripHtml_RemovesTags()
    {
        var html = "<p>Hello <b>world</b>!</p>";
        var result = ValueTransformCatalog.Apply("strip_html", html);
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
        Assert.Contains("Hello", result);
        Assert.Contains("world", result);
    }

    [Fact]
    public void StripHtml_DecodesEntities()
    {
        var html = "A &amp; B &lt; C";
        var result = ValueTransformCatalog.Apply("strip_html", html);
        Assert.Contains("A & B < C", result);
    }

    [Fact]
    public void NullTransformName_ReturnsRawValue()
    {
        Assert.Equal("raw", ValueTransformCatalog.Apply(null, "raw"));
    }

    [Fact]
    public void UnknownTransformName_ReturnsRawValue()
    {
        Assert.Equal("raw", ValueTransformCatalog.Apply("nonexistent_transform", "raw"));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Parameterised transforms (with args)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void UrlTemplate_SubstitutesValue()
    {
        var result = ValueTransformCatalog.Apply("url_template", "12345",
            "https://covers.openlibrary.org/b/id/{value}-L.jpg");
        Assert.Equal("https://covers.openlibrary.org/b/id/12345-L.jpg", result);
    }

    [Fact]
    public void UrlTemplate_NullArgs_ReturnsRaw()
    {
        Assert.Equal("12345", ValueTransformCatalog.Apply("url_template", "12345", null));
    }

    [Fact]
    public void RegexReplace_AppliesPattern()
    {
        var result = ValueTransformCatalog.Apply("regex_replace", "ISBN: 978-0-306-40615-7",
            @"\D+|");
        Assert.Equal("9780306406157", result);
    }

    [Fact]
    public void RegexReplace_NullArgs_ReturnsRaw()
    {
        Assert.Equal("abc", ValueTransformCatalog.Apply("regex_replace", "abc", null));
    }

    [Fact]
    public void FirstNChars_TruncatesLongString()
    {
        var result = ValueTransformCatalog.Apply("first_n_chars",
            "A very long description that goes on forever", "10");
        Assert.Equal("A very lon", result);
    }

    [Fact]
    public void FirstNChars_ShortString_ReturnsUnchanged()
    {
        var result = ValueTransformCatalog.Apply("first_n_chars", "Short", "100");
        Assert.Equal("Short", result);
    }

    [Fact]
    public void FirstNChars_InvalidArgs_ReturnsRaw()
    {
        Assert.Equal("data", ValueTransformCatalog.Apply("first_n_chars", "data", "not_a_number"));
    }

    [Fact]
    public void Literal_ReturnsConfiguredArgument()
    {
        Assert.Equal("MainSequence", ValueTransformCatalog.Apply("literal", "ignored", "MainSequence"));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  IsKnown
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("year_from_iso")]
    [InlineData("numeric_portion")]
    [InlineData("strip_entity_uri")]
    [InlineData("commons_url")]
    [InlineData("to_string")]
    [InlineData("milliseconds_to_minutes")]
    [InlineData("strip_html")]
    [InlineData("url_template")]
    [InlineData("regex_replace")]
    [InlineData("first_n_chars")]
    [InlineData("fallback_key")]
    [InlineData("prefer_isbn13")]
    [InlineData("array_join")]
    [InlineData("array_nested_join")]
    [InlineData("literal")]
    public void IsKnown_RecognisedTransforms(string name)
    {
        Assert.True(ValueTransformCatalog.IsKnown(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonexistent")]
    public void IsKnown_UnrecognisedTransforms(string? name)
    {
        Assert.False(ValueTransformCatalog.IsKnown(name));
    }

    [Fact]
    public void IsKnown_CaseInsensitive()
    {
        Assert.True(ValueTransformCatalog.IsKnown("YEAR_FROM_ISO"));
        Assert.True(ValueTransformCatalog.IsKnown("Strip_Html"));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Parameterised Apply falls back to simple
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ParameterisedApply_FallsBackToSimpleTransform()
    {
        // "to_string" only exists in simple libraryItem, not args libraryItem
        // But calling with 3 args should still find it via fallback
        var result = ValueTransformCatalog.Apply("to_string", "hello", "ignored_args");
        Assert.Equal("hello", result);
    }
}
