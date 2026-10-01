using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientPairingPreviewTests
{
    [Fact]
    public async Task PreviewPostsOnlySelectedAssetIdsAndTargetAndReadsWarning()
    {
        var entityId = Guid.NewGuid();
        var assets = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var revisions = assets.ToDictionary(id => id, id => $"revision-{id:D}");
        var handler = new CaptureHandler(HttpStatusCode.OK,
            """{"mediaKind":"tv_episode","provider":"tvdb","targetParentId":"426321","catalogueComplete":false,"catalogueWarning":"Review every row.","rows":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await api.PreviewMediaEditorPairingAsync(entityId,
            new MediaEditorPairingPreviewRequestDto(assets, "426321", revisions));

        Assert.NotNull(result);
        Assert.False(result.CatalogueComplete);
        Assert.Equal("Review every row.", result.CatalogueWarning);
        Assert.Equal($"/metadata/{entityId}/pairing-preview", handler.Path);
        Assert.Equal(HttpMethod.Post, handler.Method);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(2, json.RootElement.GetProperty("assetIds").GetArrayLength());
        Assert.Equal("426321", json.RootElement.GetProperty("targetParentId").GetString());
        Assert.Equal($"revision-{assets[0]:D}",
            json.RootElement.GetProperty("expectedSelectionRevisions").GetProperty(assets[0].ToString("D")).GetString());
    }

    [Fact]
    public async Task ConflictExposesReadableReviewMessage()
    {
        var handler = new CaptureHandler(HttpStatusCode.Conflict,
            """{"title":"Conflict.","detail":"Selected files do not share one matched parent."}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await api.PreviewMediaEditorPairingAsync(Guid.NewGuid(),
            new MediaEditorPairingPreviewRequestDto([Guid.NewGuid()], null, new Dictionary<Guid, string>()));

        Assert.Null(result);
        Assert.Equal("Selected files do not share one matched parent.", api.LastError);
    }

    [Fact]
    public async Task SavePostsReviewedPartitionAndKeepsPendingSyncReceipt()
    {
        var entityId = Guid.NewGuid();
        var accepted = Guid.NewGuid();
        var excluded = Guid.NewGuid();
        var operation = Guid.NewGuid().ToString("D");
        var handler = new CaptureHandler(HttpStatusCode.OK,
            $$"""{"outcome":"Committed","rows":[{"assetId":"{{accepted}}","outcome":"Committed","syncState":"pending","conflictReason":null}]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var receipt = await api.SaveMediaEditorPairingAsync(entityId,
            new MediaEditorPairingSaveRequestDto("review-token", operation,
                [new MediaEditorPairingAcceptedDto(accepted, "tvdb-episode")], [excluded],
                "artwork-review", "shared-artwork-review"));

        Assert.Equal("Committed", receipt?.Outcome);
        Assert.Equal("pending", Assert.Single(receipt!.Rows).SyncState);
        Assert.Equal($"/metadata/{entityId}/pairing-save", handler.Path);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("review-token", json.RootElement.GetProperty("reviewToken").GetString());
        Assert.Equal(operation, json.RootElement.GetProperty("operationToken").GetString());
        Assert.Equal(accepted, json.RootElement.GetProperty("accepted")[0].GetProperty("assetId").GetGuid());
        Assert.Equal(excluded, json.RootElement.GetProperty("excludedAssetIds")[0].GetGuid());
        Assert.Equal("artwork-review", json.RootElement.GetProperty("artworkReviewToken").GetString());
        Assert.Equal("shared-artwork-review", json.RootElement.GetProperty("sharedArtworkReviewToken").GetString());
    }

    [Fact]
    public async Task EpisodeStillPreviewPostsReviewedChoiceAndReadsFullImpact()
    {
        var entityId = Guid.NewGuid();
        var accepted = Guid.NewGuid();
        var excluded = Guid.NewGuid();
        var artworkId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var secondAffected = Guid.NewGuid();
        var handler = new CaptureHandler(HttpStatusCode.OK,
            $$"""{"artworkReviewToken":"artwork-review","expiresAt":"2030-01-01T00:00:00Z","ownerWorkId":"{{owner}}","artworkAssetId":"{{artworkId}}","preferenceRevision":"revision-1","affectedFiles":[{"assetId":"{{accepted}}","libraryId":"{{Guid.NewGuid()}}"},{"assetId":"{{secondAffected}}","libraryId":"{{Guid.NewGuid()}}"}],"scope":"episode"}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var review = await api.PreviewMediaEditorPairingArtworkAsync(entityId,
            new MediaEditorPairingArtworkPreviewRequestDto("pairing-review",
                [new MediaEditorPairingAcceptedDto(accepted, "episode-201")], [excluded],
                "episode-201", artworkId));

        Assert.Equal($"/metadata/{entityId}/pairing-artwork-preview", handler.Path);
        Assert.Equal(2, review?.AffectedFiles.Count);
        Assert.Equal(owner, review?.OwnerWorkId);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("pairing-review", json.RootElement.GetProperty("reviewToken").GetString());
        Assert.Equal("episode-201", json.RootElement.GetProperty("targetCandidateId").GetString());
        Assert.Equal(artworkId, json.RootElement.GetProperty("artworkAssetId").GetGuid());
        Assert.Equal(excluded, json.RootElement.GetProperty("excludedAssetIds")[0].GetGuid());
    }

    [Fact]
    public async Task SharedArtworkPreviewPostsExactOwnerRoleAndReviewedChoice()
    {
        var entityId = Guid.NewGuid();
        var accepted = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var artworkId = Guid.NewGuid();
        var handler = new CaptureHandler(HttpStatusCode.OK,
            $$"""{"sharedArtworkReviewToken":"shared-review","expiresAt":"2030-01-01T00:00:00Z","ownerWorkId":"{{owner}}","scope":"TvSeason","role":"Primary","artworkAssetId":"{{artworkId}}","preferenceRevision":"revision-1","affectedFiles":[{"assetId":"{{accepted}}","libraryId":"{{Guid.NewGuid()}}"}]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var review = await api.PreviewMediaEditorPairingSharedArtworkAsync(entityId,
            new MediaEditorPairingSharedArtworkPreviewRequestDto("pairing-review",
                [new MediaEditorPairingAcceptedDto(accepted, "episode-201")], [],
                owner, "TvSeason", "Primary", artworkId));

        Assert.Equal($"/metadata/{entityId}/pairing-shared-artwork-preview", handler.Path);
        Assert.Equal(owner, review?.OwnerWorkId);
        Assert.Equal("shared-review", review?.SharedArtworkReviewToken);
        Assert.Single(review!.AffectedFiles);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(owner, json.RootElement.GetProperty("ownerWorkId").GetGuid());
        Assert.Equal("TvSeason", json.RootElement.GetProperty("scope").GetString());
        Assert.Equal("Primary", json.RootElement.GetProperty("role").GetString());
        Assert.Equal(accepted, json.RootElement.GetProperty("accepted")[0].GetProperty("assetId").GetGuid());
    }

    [Fact]
    public async Task SaveConflictReceiptRemainsReviewable()
    {
        var asset = Guid.NewGuid();
        var handler = new CaptureHandler(HttpStatusCode.Conflict,
            $$"""{"outcome":"Conflict","rows":[{"assetId":"{{asset}}","outcome":"Conflict","syncState":null,"conflictReason":"Identity revision changed."}]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var receipt = await api.SaveMediaEditorPairingAsync(Guid.NewGuid(),
            new MediaEditorPairingSaveRequestDto("review-token", Guid.NewGuid().ToString("D"),
                [new MediaEditorPairingAcceptedDto(asset, "episode")], []));

        Assert.Equal("Conflict", receipt?.Outcome);
        Assert.Equal("Identity revision changed.", Assert.Single(receipt!.Rows).ConflictReason);
    }

    [Fact]
    public async Task ChildSearchPostsReviewTokenFileAndCrossSeasonQuery()
    {
        var entityId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var handler = new CaptureHandler(HttpStatusCode.OK,
            $$"""{"assetId":"{{assetId}}","totalCount":1,"items":[{"child":{"childId":"episode-201","parentId":"show","provider":"tvdb","title":"Second season","seasonNumber":2,"episodeNumber":1,"discNumber":null,"trackNumber":null,"recordingId":null},"canSave":true,"saveLimitation":null}]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await api.SearchMediaEditorPairingChildrenAsync(entityId,
            new MediaEditorPairingChildSearchRequestDto("review-token", assetId, "S02E01", Offset: 0, Limit: 50));

        Assert.Equal($"/metadata/{entityId}/pairing-children", handler.Path);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.True(Assert.Single(result!.Items).CanSave);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("review-token", json.RootElement.GetProperty("reviewToken").GetString());
        Assert.Equal(assetId, json.RootElement.GetProperty("assetId").GetGuid());
        Assert.Equal("S02E01", json.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task SelectionSnapshotUsesCurrentFiltersAndReadsFrozenRevisions()
    {
        var entityId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var handler = new CaptureHandler(HttpStatusCode.OK,
            $$"""{"parent_entity_id":"{{entityId}}","count":1,"items":[{"asset_id":"{{assetId}}","selection_revision":"revision-1"}]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var snapshot = await api.GetMediaEditorOwnedChildSelectionSnapshotAsync(entityId,
            "season two", season: 2, matchStatus: "Matched");

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Contains($"/metadata/{entityId}/owned-children/selection-snapshot?", handler.Path);
        Assert.Contains("q=season%20two", handler.Path);
        Assert.Contains("season=2", handler.Path);
        Assert.Equal("revision-1", Assert.Single(snapshot!.Items).SelectionRevision);
    }

    private sealed class CaptureHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.PathAndQuery;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
