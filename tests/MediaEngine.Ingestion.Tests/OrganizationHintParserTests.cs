using MediaEngine.Domain;
using MediaEngine.Ingestion.Detection;
using Xunit;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Tests the path-prescan hint parser that short-circuits Stage 1 for
/// curated Plex / Jellyfin libraries. Spec: side-by-side-with-Plex plan §G.
/// </summary>
public class OrganizationHintParserTests
{
    // Extras detection walks real directory components, so the root must be valid for the host OS.
    private static readonly string Root = OperatingSystem.IsWindows() ? @"D:\" : "/";

    [Fact]
    public void Parse_PlexImdbBracket_ExtractsImdbId()
    {
        var path = @"D:\Movies\Blade Runner 2049 (2017) {imdb-tt1856101}\Blade Runner 2049 (2017).mkv";

        var hints = OrganizationHintParser.Parse(path);

        Assert.True(hints.HasHints);
        Assert.Equal("tt1856101", hints.BridgeIds[BridgeIdKeys.ImdbId]);
    }

    [Fact]
    public void Parse_PlexTvdbBracket_ExtractsTvdbId()
    {
        var path = @"D:\TV\Severance (2022) {tvdb-371980}\Season 01\Severance - s01e01.mkv";

        var hints = OrganizationHintParser.Parse(path);

        Assert.Equal("371980", hints.BridgeIds[BridgeIdKeys.TvdbId]);
    }

    [Fact]
    public void Parse_PlexTmdbBracket_ExtractsTmdbId()
    {
        var path = @"D:\Movies\Dune (2021) {tmdb-438631}\Dune.mkv";

        var hints = OrganizationHintParser.Parse(path);

        Assert.Equal("438631", hints.BridgeIds[BridgeIdKeys.TmdbId]);
    }

    [Fact]
    public void Parse_JellyfinImdbidBracket_ExtractsImdbId()
    {
        var path = @"D:\Movies\Dune (2021) [imdbid-tt1160419]\Dune.mkv";

        var hints = OrganizationHintParser.Parse(path);

        Assert.Equal("tt1160419", hints.BridgeIds[BridgeIdKeys.ImdbId]);
    }

    [Fact]
    public void Parse_JellyfinTvdbidBracket_ExtractsTvdbId()
    {
        var path = @"D:\TV\The Expanse [tvdbid-280619]\Season 1\The Expanse S01E01.mkv";

        var hints = OrganizationHintParser.Parse(path);

        Assert.Equal("280619", hints.BridgeIds[BridgeIdKeys.TvdbId]);
    }

    [Fact]
    public void Parse_BareWikidataQidPath_DoesNotExtractWikidataQid()
    {
        var path = @"D:\Books\Frank Herbert\Dune (Q165666)\Dune.epub";

        var hints = OrganizationHintParser.Parse(path);

        Assert.False(hints.BridgeIds.ContainsKey(BridgeIdKeys.WikidataQid));
    }

    [Fact]
    public void Parse_PlexEditionLabel_ExtractsLabel()
    {
        var path = @"D:\Movies\Blade Runner (1982) {imdb-tt0083658} {edition-Final Cut}\Blade Runner.mkv";

        var hints = OrganizationHintParser.Parse(path);

        Assert.Equal("Final Cut", hints.EditionLabel);
        Assert.Equal("tt0083658", hints.BridgeIds[BridgeIdKeys.ImdbId]);
    }

    [Fact]
    public void Parse_ExtrasSubfolder_MarksAsExtras()
    {
        var path = Path.Combine(Root, "Movies", "Blade Runner (1982) {imdb-tt0083658}", "Behind The Scenes", "Making Of.mkv");

        var hints = OrganizationHintParser.Parse(path);

        Assert.True(hints.IsExtras);
    }

    [Fact]
    public void Parse_TrailersSubfolder_MarksAsExtras()
    {
        var path = Path.Combine(Root, "Movies", "Dune (2021) {imdb-tt1160419}", "Trailers", "Official Trailer.mp4");

        var hints = OrganizationHintParser.Parse(path);

        Assert.True(hints.IsExtras);
    }

    [Fact]
    public void Parse_NoHints_ReturnsEmpty()
    {
        var path = @"F:\Mess\bladerunner.mkv";

        var hints = OrganizationHintParser.Parse(path);

        Assert.False(hints.HasHints);
        Assert.Empty(hints.BridgeIds);
        Assert.Null(hints.EditionLabel);
        Assert.False(hints.IsExtras);
    }

    [Fact]
    public void Parse_EmptyPath_ReturnsEmpty()
    {
        var hints = OrganizationHintParser.Parse(string.Empty);

        Assert.False(hints.HasHints);
    }

    [Fact]
    public void Parse_PlexImdbCaseInsensitive_Matches()
    {
        var path = @"D:\Movies\Dune (2021) {IMDB-tt1160419}\Dune.mkv";

        var hints = OrganizationHintParser.Parse(path);

        Assert.Equal("tt1160419", hints.BridgeIds[BridgeIdKeys.ImdbId]);
    }

    [Fact]
    public void Parse_MultipleBrackets_ExtractsAll()
    {
        var path = @"D:\Movies\Dune (2021) {imdb-tt1160419} {tmdb-438631}\Dune.mkv";

        var hints = OrganizationHintParser.Parse(path);

        Assert.Equal("tt1160419", hints.BridgeIds[BridgeIdKeys.ImdbId]);
        Assert.Equal("438631", hints.BridgeIds[BridgeIdKeys.TmdbId]);
    }
}
