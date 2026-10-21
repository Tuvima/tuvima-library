using System.Reflection;
using MediaEngine.Domain;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A Work with several <c>part of the series</c> (P179) statements keeps each Series' own position: the Wikidata adapter
/// pairs every P1545 position with the Series it was stated on (<c>series_placement</c> with its <c>_qid</c> companion).
/// </summary>
public sealed class SeriesPlacementClaimTests
{
    private static readonly MethodInfo ExtensionToClaimsMethod = typeof(ReconciliationAdapter)
        .GetMethod("ExtensionToClaims", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ExtensionToClaims method not found on ReconciliationAdapter");

    private static WikidataClaim SeriesStatement(string seriesQid, string label, string? position)
        => new()
        {
            PropertyId = "P179",
            Rank = "normal",
            Value = new WikidataValue
            {
                Kind = WikidataValueKind.EntityId,
                RawValue = label,
                EntityId = seriesQid,
            },
            Qualifiers = position is null
                ? new Dictionary<string, IReadOnlyList<WikidataValue>>()
                : new Dictionary<string, IReadOnlyList<WikidataValue>>
                {
                    ["P1545"] = [new WikidataValue { Kind = WikidataValueKind.String, RawValue = position }],
                },
        };

    private static List<ProviderClaim> ConvertToClaims(params WikidataClaim[] seriesStatements)
    {
        var properties = new Dictionary<string, IReadOnlyList<WikidataClaim>> { ["P179"] = seriesStatements };
        var labels = new Dictionary<string, string> { ["P179"] = "series" };
        var result = ExtensionToClaimsMethod.Invoke(null, ["Q1234", properties, labels, true, 20, null, false]);
        return ((IEnumerable<ProviderClaim>)result!).ToList();
    }

    [Fact]
    public void AWorkInTwoSeries_PairsEachPositionWithItsOwnSeries()
    {
        var claims = ConvertToClaims(
            SeriesStatement("Q111", "The Trilogy", "3"),
            SeriesStatement("Q222", "The Big Cycle", "7.5"));

        var pairs = claims
            .Where(c => c.Key == MetadataFieldConstants.SeriesPlacement + MetadataFieldConstants.CompanionQidSuffix)
            .Select(c => c.Value)
            .ToList();
        Assert.Equal(["Q111::3", "Q222::7.5"], pairs);
        Assert.Equal(
            ["3", "7.5"],
            claims.Where(c => c.Key == MetadataFieldConstants.SeriesPlacement).Select(c => c.Value).ToList());
    }

    [Fact]
    public void ASeriesStatementWithoutAPosition_AddsNoPair_ButStillNamesTheSeries()
    {
        var claims = ConvertToClaims(SeriesStatement("Q111", "The Trilogy", position: null));

        Assert.DoesNotContain(claims, c => c.Key.StartsWith(MetadataFieldConstants.SeriesPlacement, StringComparison.Ordinal));
        Assert.Contains(claims, c => c.Key == "series" && c.Value == "The Trilogy");
        Assert.Contains(claims, c => c.Key == "series_qid" && c.Value == "Q111::The Trilogy");
    }

    [Fact]
    public void AnUnsupportedContainerLabel_AddsNoPair()
    {
        var claims = ConvertToClaims(SeriesStatement("Q65071834", "list of Sony Pictures Animation productions", "4"));

        Assert.DoesNotContain(claims, c => c.Key.StartsWith(MetadataFieldConstants.SeriesPlacement, StringComparison.Ordinal));
        Assert.DoesNotContain(claims, c => c.Key == MetadataFieldConstants.SeriesPosition);
    }

    [Fact]
    public void ThePairsAreAdditional_TheExistingSeriesPositionClaimsAreStillEmitted()
    {
        var claims = ConvertToClaims(
            SeriesStatement("Q111", "The Trilogy", "3"),
            SeriesStatement("Q222", "The Big Cycle", "7.5"));

        Assert.Equal(
            ["3", "7.5"],
            claims.Where(c => c.Key == MetadataFieldConstants.SeriesPosition).Select(c => c.Value).ToList());
    }

    [Fact]
    public void TheImmediateBridgeSeries_PairEachPositionWithItsSeries_AndSkipASeriesWithNoPosition()
    {
        var method = typeof(ReconciliationAdapter).GetMethod("AddImmediateBridgeSeriesClaims", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AddImmediateBridgeSeriesClaims method not found on ReconciliationAdapter");
        IReadOnlyList<BridgeSeriesInfo> series =
        [
            new BridgeSeriesInfo { SeriesQid = "Q111", SeriesLabel = "The Trilogy", Position = "3", IsImmediateSeries = true, Confidence = 0.9 },
            new BridgeSeriesInfo { SeriesQid = "Q222", SeriesLabel = "The Big Cycle", Position = "7.5", IsImmediateSeries = true, Confidence = 0.9 },
            new BridgeSeriesInfo { SeriesQid = "Q333", SeriesLabel = "No Number", Position = null, IsImmediateSeries = true, Confidence = 0.9 },
        ];

        var claims = ((IReadOnlyList<ProviderClaim>)method.Invoke(null, [new List<ProviderClaim>(), series])!).ToList();

        Assert.Equal(
            ["Q111::3", "Q222::7.5"],
            claims.Where(c => c.Key == MetadataFieldConstants.SeriesPlacement + MetadataFieldConstants.CompanionQidSuffix)
                .Select(c => c.Value).ToList());
        Assert.Contains(claims, c => c.Key == "series_qid" && c.Value == "Q333::No Number");
    }

    [Fact]
    public void ThePlacementKeysAreMultiValued_SoTheyLandInTheArrayTableNotTheScalarOne()
    {
        Assert.True(MetadataFieldConstants.IsMultiValued(MetadataFieldConstants.SeriesPlacement));
        Assert.True(MetadataFieldConstants.IsMultiValued(
            MetadataFieldConstants.SeriesPlacement + MetadataFieldConstants.CompanionQidSuffix));
        Assert.False(MetadataFieldConstants.IsMultiValued(MetadataFieldConstants.SeriesPosition));
    }
}
