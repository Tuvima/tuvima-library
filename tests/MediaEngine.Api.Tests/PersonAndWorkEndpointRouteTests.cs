using System.IO;

namespace MediaEngine.Api.Tests;

public sealed class PersonAndWorkEndpointRouteTests
{
    [Fact]
    public void PersonEndpoints_ExposeRichPersonShellAndLibraryCredits()
    {
        var source =
            File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/PersonEndpoints.cs"))
            + File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Contracts/Persons/PersonResponses.cs"));

        Assert.Contains("group.MapGet(\"/{id:guid}/library-credits\"", source, StringComparison.Ordinal);
        Assert.Contains("IPersonCreditReadService personCreditReadService", source, StringComparison.Ordinal);
        Assert.Contains("personCreditReadService.GetLibraryCreditsAsync(id, ct)", source, StringComparison.Ordinal);
        Assert.Contains("group_members", source, StringComparison.Ordinal);
        Assert.Contains("member_of_groups", source, StringComparison.Ordinal);
        Assert.Contains("date_of_birth", source, StringComparison.Ordinal);
        Assert.Contains("place_of_birth", source, StringComparison.Ordinal);
        Assert.Contains("nationality", source, StringComparison.Ordinal);
        Assert.Contains("banner_url", source, StringComparison.Ordinal);
        Assert.Contains("background_url", source, StringComparison.Ordinal);
        Assert.Contains("logo_url", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Results.Redirect(remoteUri.ToString())", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterRoleQuery_UsesStoredWorkQidInsteadOfInferringFirstWork()
    {
        var endpointSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/CharacterEndpoints.cs"));
        var querySource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/PersonCreditReadService.cs"));

        Assert.Contains("group.MapGet(\"/persons/{personId:guid}/character-roles\"", endpointSource, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/portraits/{portraitId:guid}\"", endpointSource, StringComparison.Ordinal);
        Assert.Contains("ApiImageUrls.BuildCharacterPortraitUrl", endpointSource, StringComparison.Ordinal);
        Assert.Contains("IPersonCreditReadService personCreditReadService", endpointSource, StringComparison.Ordinal);
        Assert.Contains("personCreditReadService.GetCharacterRolesAsync(personId, ct)", endpointSource, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN works w", querySource, StringComparison.Ordinal);
        Assert.Contains("ON cpl.work_qid = COALESCE(", querySource, StringComparison.Ordinal);
        Assert.Contains("NULLIF(TRIM(w.wikidata_qid), '')", querySource, StringComparison.Ordinal);
        Assert.Contains("cpl.work_qid IS NOT NULL", querySource, StringComparison.Ordinal);
        Assert.Contains("ApiImageUrls.BuildCharacterPortraitUrl", querySource, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkAndCollectionCastEndpoints_ShareTheSameCastCreditShape()
    {
        var workEndpointSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/WorkEndpoints.cs"));
        var collectionEndpointSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs"));
        var dtoSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Contracts/Collections/CollectionGroups.cs"));
        var programSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/DependencyInjection/ApiEndpointRouteBuilderExtensions.cs"));

        Assert.Contains("group.MapGet(\"/{workId:guid}/cast\"", workEndpointSource, StringComparison.Ordinal);
        Assert.Contains("personCreditReadService.BuildForWorkAsync(workId, ct)", workEndpointSource, StringComparison.Ordinal);
        Assert.Contains("personCreditReadService.BuildForCollectionRootAsync", collectionEndpointSource, StringComparison.Ordinal);
        Assert.Contains("public List<CastCreditDto> TopCast { get; set; } = [];", dtoSource, StringComparison.Ordinal);
        Assert.Contains("app.MapWorkEndpoints();", programSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CastCreditQuery_QualifiesWorkIdentityColumns()
    {
        var querySource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/PersonCreditReadService.cs"));

        Assert.Contains("SELECT w.id AS WorkId", querySource, StringComparison.Ordinal);
        Assert.Contains(")                                      AS WorkQid", querySource, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT id AS WorkId", querySource, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionGroupDetail_ExposesAudioPaletteAndCompleteAlbumTrackData()
    {
        // The album-track manifest logic moved out of the endpoint file into
        // AlbumTrackManifestService in stage 5A; scan both as one surface.
        var endpointSource =
            File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs"))
            + File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/Collections/AlbumTrackManifestService.cs"))
            + File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Services/AppleAlbumManifestJson.cs"));
        var dtoSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Contracts/Collections/CollectionGroups.cs"));

        Assert.Contains("PrimaryColor", dtoSource, StringComparison.Ordinal);
        Assert.Contains("SecondaryColor", dtoSource, StringComparison.Ordinal);
        Assert.Contains("AccentColor", dtoSource, StringComparison.Ordinal);
        Assert.Contains("DurationSeconds", dtoSource, StringComparison.Ordinal);
        Assert.Contains("EnsureAlbumTrackManifestAsync", endpointSource, StringComparison.Ordinal);
        Assert.Contains("AppleRetailClient appleRetailClient", endpointSource, StringComparison.Ordinal);
        Assert.Contains("MetadataFieldConstants.ChildEntitiesJson", endpointSource, StringComparison.Ordinal);
        Assert.Contains("Status = \"Missing\"", endpointSource, StringComparison.Ordinal);
        Assert.Contains("AppleAlbumManifestJson.Build", endpointSource, StringComparison.Ordinal);
        Assert.Contains("provider_collection_id", endpointSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkEndpoints_ExposeReadOnlyWorkAndEditionIdentitySurfaces()
    {
        var workEndpointSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/WorkEndpoints.cs"));
        var dtoSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Contracts/Collections/LegacyCollectionContracts.cs"));

        Assert.Contains("group.MapGet(\"/{workId:guid}\", async (", workEndpointSource, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"GetWorkDetail\")", workEndpointSource, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/{workId:guid}/editions\", async (", workEndpointSource, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"GetWorkEditions\")", workEndpointSource, StringComparison.Ordinal);
        Assert.Contains("public sealed class WorkDetailDto", dtoSource, StringComparison.Ordinal);
        Assert.Contains("public sealed class EditionDto", dtoSource, StringComparison.Ordinal);
        Assert.Contains("public sealed class EditionAssetDto", dtoSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PersonAndUniverseEndpoints_PreferLocalHeadshotRoutes()
    {
        var personSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/PersonEndpoints.cs"));
        var graphSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs"));

        Assert.Contains("ApiImageUrls.BuildPersonHeadshotUrl", personSource, StringComparison.Ordinal);
        Assert.Contains("ApiImageUrls.BuildPersonHeadshotUrl", graphSource, StringComparison.Ordinal);
    }

    [Fact]
    public void UniverseCastEndpoint_UsesTypedBatchedPersonRepositoryRead()
    {
        var graphSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs"));

        Assert.Contains("IPersonRepository personRepo", graphSource, StringComparison.Ordinal);
        Assert.Contains("personRepo.GetCharacterPerformersAsync(", graphSource, StringComparison.Ordinal);
        Assert.DoesNotContain("character.Id.ToString()", graphSource, StringComparison.Ordinal);
        Assert.DoesNotContain("character_performer_links", graphSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IDatabaseConnection", graphSource, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
