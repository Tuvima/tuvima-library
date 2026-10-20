using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.System;

/// <summary>
/// Which development-only pages this running Engine can serve. Both are <see langword="true"/> only in a development build
/// started in the Development environment, because the debug and development actions behind them are mapped only then;
/// a release Engine answers <see langword="false"/> for both. Provider Tester is not listed: it uses ordinary search actions.
/// </summary>
/// <param name="EnrichmentTester">The Engine offers the debug actions the Enrichment Tester page calls.</param>
/// <param name="DevelopmentData">The Engine offers the development actions the Development data page calls (reset, seed, rescan, real-media checks).</param>
public sealed record DevelopmentToolsResponse(
    [property: JsonPropertyName("enrichment_tester")] bool EnrichmentTester,
    [property: JsonPropertyName("development_data")] bool DevelopmentData);
