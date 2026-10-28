using System.Reflection;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Tests for the <c>ExtensionToClaims</c> private static method in
/// <see cref="ReconciliationAdapter"/>.
///
/// Key fix verified here: companion <c>_qid</c> claims must carry a human-readable
/// label (e.g. "Q44413::Frank Herbert"), not a bare QID repeated as both halves
/// (e.g. "Q44413::Q44413").
/// </summary>
public sealed class ExtensionToClaimsTests
{
    private static readonly MethodInfo ExtensionToClaimsMethod = typeof(ReconciliationAdapter)
        .GetMethod("ExtensionToClaims", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
               "ExtensionToClaims method not found on ReconciliationAdapter");

    /// <summary>
    /// Helper: creates a WikidataClaim with an entity value (EntityId kind).
    /// </summary>
    private static WikidataClaim EntityClaim(string propertyId, string entityId, string? label = null) =>
        new()
        {
            PropertyId = propertyId,
            Rank = "normal",
            Value = new WikidataValue
            {
                Kind = WikidataValueKind.EntityId,
                RawValue = label ?? entityId,
                EntityId = entityId,
            }
        };

    /// <summary>
    /// Helper: creates a WikidataClaim with a string value.
    /// </summary>
    private static WikidataClaim StringClaim(string propertyId, string value, string? language = null) =>
        new()
        {
            PropertyId = propertyId,
            Rank = "normal",
            Value = new WikidataValue
            {
                Kind = WikidataValueKind.String,
                RawValue = value,
                Language = language,
            }
        };

    private static WikidataClaim TimeClaim(string value) =>
        new()
        {
            PropertyId = "P577",
            Rank = "normal",
            Value = new WikidataValue
            {
                Kind = WikidataValueKind.Time,
                RawValue = value,
            }
        };

    /// <summary>
    /// Invokes <c>ExtensionToClaims</c> via reflection and materialises the result.
    /// </summary>
    private static List<ProviderClaim> ConvertToClaims(
        string entityQid,
        IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>> properties,
        Dictionary<string, string> propertyLabels,
        bool isWork = true,
        string? metadataLanguage = null,
        bool editionScopedDates = false)
    {
        // ExtensionToClaims(entityQid, properties, propertyLabels, isWork,
        // castMemberLimit, metadataLanguage, editionScopedDates)
        var result = ExtensionToClaimsMethod.Invoke(
            null,
            [entityQid, properties, propertyLabels, isWork, 20, metadataLanguage, editionScopedDates]);
        return ((IEnumerable<ProviderClaim>)result!).ToList();
    }

    // ── Companion _qid claims use human-readable label ────────────────────────

    [Fact]
    public void CompanionQidClaims_UseHumanReadableLabel()
    {
        // Arrange: entity value with both EntityId and label (RawValue).
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P50"] = [EntityClaim("P50", "Q44413", "Frank Herbert")],
        };
        var labels = new Dictionary<string, string> { ["P50"] = "author" };

        // Act
        var claims = ConvertToClaims("Q190159", properties, labels);

        // Assert: the author label claim carries the human-readable name.
        var authorClaim = claims.FirstOrDefault(c => c.Key == "author");
        Assert.NotNull(authorClaim);
        Assert.Equal("Frank Herbert", authorClaim.Value);

        // The companion _qid claim must be "QID::Label", not "QID::QID".
        var qidClaim = claims.FirstOrDefault(c => c.Key == "author_qid");
        Assert.NotNull(qidClaim);
        Assert.Equal("Q44413::Frank Herbert", qidClaim.Value);
    }

    [Fact]
    public void CompanionQidClaim_DoesNotRepeatQidAsLabel()
    {
        // Regression guard: before the fix, fallback was val.Id, producing "Q44413::Q44413".
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P50"] = [EntityClaim("P50", "Q44413", "Frank Herbert")],
        };
        var labels = new Dictionary<string, string> { ["P50"] = "author" };

        var claims = ConvertToClaims("Q190159", properties, labels);

        var qidClaim = claims.First(c => c.Key == "author_qid");
        Assert.NotEqual("Q44413::Q44413", qidClaim.Value);
    }

    [Fact]
    public void P577WorkDates_UseEarliestReleaseAndOriginalKeys()
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P577"] =
            [
                TimeClaim("+2025-02-14T00:00:00Z"),
                TimeClaim("+2014-11-05T00:00:00Z"),
            ],
        };
        var labels = new Dictionary<string, string> { ["P577"] = "release_year" };

        var claims = ConvertToClaims("Q13417189", properties, labels);

        Assert.Contains(claims, claim => claim.Key == "original_release_year" && claim.Value == "2014");
        Assert.Contains(claims, claim => claim.Key == "original_release_date" && claim.Value == "2014-11-05");
        Assert.DoesNotContain(claims, claim => claim.Key == "release_year");
    }

    [Fact]
    public void P577EditionDates_StaySeparateFromOriginalWorkDate()
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P577"] = [TimeClaim("+2007-09-18T00:00:00Z")],
        };
        var labels = new Dictionary<string, string> { ["P577"] = "release_year" };

        var claims = ConvertToClaims(
            "Q92076495",
            properties,
            labels,
            editionScopedDates: true);

        Assert.Contains(claims, claim => claim.Key == "edition_release_year" && claim.Value == "2007");
        Assert.Contains(claims, claim => claim.Key == "edition_release_date" && claim.Value == "2007-09-18");
        Assert.DoesNotContain(claims, claim => claim.Key.StartsWith("original_", StringComparison.Ordinal));
    }

    // ── Label absent: falls back to QID ──────────────────────────────────────

    [Fact]
    public void CompanionQidClaim_FallsBackToQidWhenLabelIsNull()
    {
        // When label (RawValue) matches the EntityId, the QID itself is used as the label.
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P50"] = [EntityClaim("P50", "Q44413")],
        };
        var labels = new Dictionary<string, string> { ["P50"] = "author" };

        var claims = ConvertToClaims("Q190159", properties, labels);

        var qidClaim = claims.FirstOrDefault(c => c.Key == "author_qid");
        Assert.NotNull(qidClaim);
        // Should still include the QID on the left-hand side.
        Assert.StartsWith("Q44413::", qidClaim.Value);
    }

    // ── Multiple entity values → individual claims each ───────────────────────

    [Fact]
    public void MultiValuedEntityProperty_EmitsOneClaimPerValue()
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P50"] =
            [
                EntityClaim("P50", "Q46248", "Terry Pratchett"),
                EntityClaim("P50", "Q210112", "Neil Gaiman"),
            ],
        };
        var labels = new Dictionary<string, string> { ["P50"] = "author" };

        var claims = ConvertToClaims("Q1234", properties, labels);

        var authorClaims = claims.Where(c => c.Key == "author").ToList();
        Assert.Equal(2, authorClaims.Count);
        Assert.Contains(authorClaims, c => c.Value == "Terry Pratchett");
        Assert.Contains(authorClaims, c => c.Value == "Neil Gaiman");
    }

    [Fact]
    public void MultiValuedEntityProperty_EmitsCompanionQidPerValue()
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P50"] =
            [
                EntityClaim("P50", "Q46248", "Terry Pratchett"),
                EntityClaim("P50", "Q210112", "Neil Gaiman"),
            ],
        };
        var labels = new Dictionary<string, string> { ["P50"] = "author" };

        var claims = ConvertToClaims("Q1234", properties, labels);

        var qidClaims = claims.Where(c => c.Key == "author_qid").ToList();
        Assert.Equal(2, qidClaims.Count);
        Assert.Contains(qidClaims, c => c.Value == "Q46248::Terry Pratchett");
        Assert.Contains(qidClaims, c => c.Value == "Q210112::Neil Gaiman");
    }

    // ── P18 guard: image property suppressed for Work entities ────────────────

    [Fact]
    public void P18Image_SuppressedForWorkEntities()
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P18"] = [StringClaim("P18", "Frank_Herbert.jpg")],
        };
        // Map P18 → headshot_url (as in the real provider config).
        var labels = new Dictionary<string, string> { ["P18"] = "headshot_url" };

        var claims = ConvertToClaims("Q190159", properties, labels, isWork: true);

        // P18 is not emitted for Work entities.
        Assert.DoesNotContain(claims, c => c.Key == "headshot_url");
    }

    [Fact]
    public void P18Image_EmittedForPersonEntities()
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P18"] = [StringClaim("P18", "Frank_Herbert.jpg")],
        };
        var labels = new Dictionary<string, string> { ["P18"] = "headshot_url" };

        var claims = ConvertToClaims("Q44413", properties, labels, isWork: false);

        // P18 is converted to a Wikimedia Commons URL for Person entities.
        var headshotClaim = claims.FirstOrDefault(c => c.Key == "headshot_url");
        Assert.NotNull(headshotClaim);
        Assert.Contains("commons.wikimedia.org", headshotClaim.Value);
        Assert.Contains("Frank_Herbert.jpg", headshotClaim.Value);
    }

    // ── P179 award list filtering ────────────────────────────────────────

    [Fact]
    public void P1476Title_PrefersConfiguredDisplayLanguage()
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P1476"] =
            [
                StringClaim("P1476", "Norwegian Wood", "en"),
                StringClaim("P1476", "ノルウェイの森", "ja"),
            ],
        };
        var labels = new Dictionary<string, string> { ["P1476"] = "title" };

        var claims = ConvertToClaims("Q751348", properties, labels, metadataLanguage: "ja");

        var titleClaims = claims.Where(c => c.Key == "title").ToList();
        Assert.Single(titleClaims);
        Assert.Equal("ノルウェイの森", titleClaims[0].Value);
    }

    [Fact]
    public void P179Series_AwardListPattern_Filtered()
    {
        // P179 pointing to "BBC's 100 Greatest Films of the 21st Century" should be filtered out.
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P179"] = [EntityClaim("P179", "Q123456", "BBC's 100 Greatest Films of the 21st Century")],
        };
        var labels = new Dictionary<string, string> { ["P179"] = "series" };

        var claims = ConvertToClaims("Q155653", properties, labels);

        // Award list patterns are suppressed — no series claim emitted.
        Assert.DoesNotContain(claims, c => c.Key == "series");
        Assert.DoesNotContain(claims, c => c.Key == "series_qid");
    }

    [Fact]
    public void P179Series_WikimediaProductionList_Filtered()
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P179"] = [EntityClaim("P179", "Q65071834", "list of Sony Pictures Animation productions")],
        };
        var labels = new Dictionary<string, string> { ["P179"] = "series" };

        var claims = ConvertToClaims("Q2446049", properties, labels);

        Assert.DoesNotContain(claims, c => c.Key == "series");
        Assert.DoesNotContain(claims, c => c.Key == "series_qid");
    }

    [Fact]
    public void P179Series_NarrativeSeries_NotFiltered()
    {
        // P179 pointing to "Studio Ghibli Feature Films" should NOT be filtered — it's a real series.
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P179"] = [EntityClaim("P179", "Q842256", "Studio Ghibli Feature Films")],
        };
        var labels = new Dictionary<string, string> { ["P179"] = "series" };

        var claims = ConvertToClaims("Q155653", properties, labels);

        // Narrative series should pass through.
        Assert.Contains(claims, c => c.Key == "series" && c.Value == "Studio Ghibli Feature Films");
        Assert.Contains(claims, c => c.Key == "series_qid");
    }

    // ── Unknown property code → no claim emitted ─────────────────────────────

    [Fact]
    public void P179Series_WithP1545Qualifier_EmitsSeriesPosition()
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P179"] =
            [
                new WikidataClaim
                {
                    PropertyId = "P179",
                    Rank = "normal",
                    Value = new WikidataValue
                    {
                        Kind = WikidataValueKind.EntityId,
                        RawValue = "Dune series",
                        EntityId = "Q115652",
                    },
                    Qualifiers = new Dictionary<string, IReadOnlyList<WikidataValue>>
                    {
                        ["P1545"] =
                        [
                            new WikidataValue
                            {
                                Kind = WikidataValueKind.String,
                                RawValue = "2",
                            },
                        ],
                    },
                },
            ],
        };
        var labels = new Dictionary<string, string> { ["P179"] = "series" };

        var claims = ConvertToClaims("Q1234", properties, labels);

        Assert.Contains(claims, c => c.Key == "series" && c.Value == "Dune series");
        Assert.Contains(claims, c => c.Key == "series_position" && c.Value == "2");
    }

    [Fact]
    public void UnknownPropertyCode_SkippedSilently()
    {
        // P999 is not in the labels dictionary.
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P999"] = [StringClaim("P999", "some value")],
        };
        var labels = new Dictionary<string, string>(); // empty — P999 unknown

        var claims = ConvertToClaims("Q190159", properties, labels);

        Assert.Empty(claims);
    }

    private static WikidataClaim QuantityClaim(string propertyId, decimal amount, string? unit) =>
        new()
        {
            PropertyId = propertyId,
            Rank = "normal",
            Value = new WikidataValue
            {
                Kind = WikidataValueKind.Quantity,
                RawValue = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Amount = amount,
                Unit = unit,
            }
        };

    [Theory]
    [InlineData(3.5, "http://www.wikidata.org/entity/Q7727", "210")]
    [InlineData(243, "http://www.wikidata.org/entity/Q11574", "243")]
    [InlineData(243000, "http://www.wikidata.org/entity/Q723733", "243")]
    [InlineData(0.5, "http://www.wikidata.org/entity/Q25235", "1800")]
    public void DurationQuantity_IsStoredInSecondsUsingItsUnit(double amount, string unit, string expectedSeconds)
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P2047"] = [QuantityClaim("P2047", (decimal)amount, unit)],
        };
        var labels = new Dictionary<string, string> { ["P2047"] = "duration" };

        var claims = ConvertToClaims("Q1", properties, labels);

        var claim = Assert.Single(claims);
        Assert.Equal("duration_seconds", claim.Key);
        Assert.Equal(expectedSeconds, claim.Value);
    }

    [Fact]
    public void DurationQuantity_WithAnUnknownUnit_IsDroppedRatherThanGuessed()
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>>
        {
            ["P2047"] = [QuantityClaim("P2047", 3.5m, null)],
        };
        var labels = new Dictionary<string, string> { ["P2047"] = "duration" };

        Assert.Empty(ConvertToClaims("Q1", properties, labels));
    }
}
