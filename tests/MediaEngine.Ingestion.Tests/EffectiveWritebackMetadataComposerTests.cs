using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Services;

namespace MediaEngine.Ingestion.Tests;

public sealed class EffectiveWritebackMetadataComposerTests
{
    [Fact]
    public void WorkAndEditionClaimsDoNotLeakBetweenMovieVersions()
    {
        var workId = Guid.NewGuid();
        var theatricalEditionId = Guid.NewGuid();
        var imaxEditionId = Guid.NewGuid();
        var theatricalAssetId = Guid.NewGuid();
        var imaxAssetId = Guid.NewGuid();
        var values = Values(
            (workId, "title", "Dune: Part Two"),
            (theatricalEditionId, "subtitle", "Theatrical Cut"),
            (imaxEditionId, "subtitle", "IMAX Version"),
            (theatricalAssetId, "codec", "H.264"),
            (imaxAssetId, "codec", "HEVC"));

        var theatrical = Compose(Lineage(theatricalAssetId, theatricalEditionId, workId, MediaType.Movies), values,
            "title", "subtitle", "codec");
        var imax = Compose(Lineage(imaxAssetId, imaxEditionId, workId, MediaType.Movies), values,
            "title", "subtitle", "codec");

        Assert.Equal("Dune: Part Two", theatrical["title"]);
        Assert.Equal("Dune: Part Two", imax["title"]);
        Assert.Equal("H.264", theatrical["codec"]);
        Assert.Equal("HEVC", imax["codec"]);
        Assert.False(theatrical.ContainsKey("subtitle"));
        Assert.False(imax.ContainsKey("subtitle"));
    }

    [Fact]
    public void MultiplePartsShareAudiobookEditionFactsButKeepAssetFacts()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var firstPart = Guid.NewGuid();
        var secondPart = Guid.NewGuid();
        var values = Values(
            (workId, "title", "Project Hail Mary"),
            (editionId, "subtitle", "Ray Porter · Audible Edition"),
            (editionId, "publisher", "Audible"),
            (firstPart, "file_part", "1"),
            (secondPart, "file_part", "2"));

        var first = Compose(Lineage(firstPart, editionId, workId, MediaType.Audiobooks), values,
            "title", "subtitle", "publisher", "file_part");
        var second = Compose(Lineage(secondPart, editionId, workId, MediaType.Audiobooks), values,
            "title", "subtitle", "publisher", "file_part");

        Assert.Equal("Ray Porter · Audible Edition", first["subtitle"]);
        Assert.Equal("Ray Porter · Audible Edition", second["subtitle"]);
        Assert.Equal("Audible", first["publisher"]);
        Assert.Equal("Audible", second["publisher"]);
        Assert.Equal("1", first["file_part"]);
        Assert.Equal("2", second["file_part"]);
    }

    [Fact]
    public void LegacyAssetValueIsUsedWhenDeclaredOwnerHasNoClaim()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var values = Values((assetId, "title", "Locally tagged title"));

        var tags = Compose(Lineage(assetId, editionId, workId, MediaType.Books), values, "title");

        Assert.Equal("Locally tagged title", tags["title"]);
    }

    [Fact]
    public void EpisodeVersionsShareShowAndEpisodeClaimsWithoutSharingAssetClaims()
    {
        var showId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var firstEdition = Guid.NewGuid();
        var secondEdition = Guid.NewGuid();
        var firstAsset = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        var values = Values(
            (showId, "show_name", "Solo Leveling"),
            (episodeId, "episode_number", "3"),
            (firstAsset, "codec", "H.264"),
            (secondAsset, "codec", "HEVC"));

        var first = Compose(new WorkLineage(firstAsset, firstEdition, episodeId, showId, showId, WorkKind.Child, MediaType.TV),
            values, "show_name", "episode_number", "codec");
        var second = Compose(new WorkLineage(secondAsset, secondEdition, episodeId, showId, showId, WorkKind.Child, MediaType.TV),
            values, "show_name", "episode_number", "codec");

        Assert.Equal("Solo Leveling", first["show_name"]);
        Assert.Equal("Solo Leveling", second["show_name"]);
        Assert.Equal("3", first["episode_number"]);
        Assert.Equal("3", second["episode_number"]);
        Assert.Equal("H.264", first["codec"]);
        Assert.Equal("HEVC", second["codec"]);
    }

    private static WorkLineage Lineage(Guid assetId, Guid editionId, Guid workId, MediaType mediaType) =>
        new(assetId, editionId, workId, null, workId, WorkKind.Standalone, mediaType);

    private static IReadOnlyDictionary<string, string> Compose(
        WorkLineage lineage,
        IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>> values,
        params string[] allowed) =>
        EffectiveWritebackMetadataComposer.Compose(
            lineage,
            values,
            new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>> Values(
        params (Guid EntityId, string Key, string Value)[] entries) =>
        entries.GroupBy(entry => entry.EntityId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CanonicalValue>)group
                    .Select(entry => new CanonicalValue
                    {
                        EntityId = entry.EntityId,
                        Key = entry.Key,
                        Value = entry.Value,
                    })
                    .ToList());
}
