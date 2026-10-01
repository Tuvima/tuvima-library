using System.Security.Claims;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Tests;

public sealed class TvPairingReviewTokenServiceTests
{
    [Fact]
    public void Token_IsOpaqueBoundToSnapshot_AndRejectsMalformedValue()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new TvPairingReviewTokenService(cache);
        var route = Guid.NewGuid();
        var actor = new RequestAuthority(PrincipalKind.Human, true,
            AccountId: Guid.NewGuid(), ActiveProfileId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(), AccountEnabled: true, GrantEnabled: true);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(TuvimaClaimTypes.PrincipalKind, PrincipalKind.Human.ToString()),
        ], "test"));
        Assert.True(TvPairingReviewTokenService.TryBindActor(http, actor, out var credential));
        Assert.Null(credential);

        var receipt = service.Store(route, actor, null, "42", Guid.NewGuid(),
            new Dictionary<Guid, PairingAssetRow>(),
            new Dictionary<string, TvPairingLocalTarget>(), []);
        Assert.Equal(48, receipt.Token.Length);
        Assert.Equal(route, service.Get(receipt.Token)?.RouteEntityId);
        Assert.Null(service.Get("not-a-token"));
        Assert.False(TvPairingReviewTokenService.TryBindActor(http,
            actor with { SessionId = null }, out _));
    }

    [Fact]
    public void LocalTargetResolver_RequiresExactShowAndDefaultSeasonPlacement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_pairing_targets_{Guid.NewGuid():N}.db");
        using var database = new DatabaseConnection(path);
        try
        {
            database.InitializeSchema();
            var show = Guid.NewGuid();
            var season = Guid.NewGuid();
            var right = Guid.NewGuid();
            var wrongSeason = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                connection.Execute("""
                    INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@show, 'TV', 'parent', 'Owned');
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                    VALUES (@season, 'TV', 'parent', @show, 1, 'Owned');
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership)
                    VALUES (@right, 'TV', 'child', @season, 1, 'Owned'),
                           (@wrongSeason, 'TV', 'child', @season, 2, 'Owned');
                    INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES
                      (@seriesBridge, @show, 'tvdb_id', '42'),
                      (@rightBridge, @right, 'tvdb_episode_id', '101'),
                      (@wrongBridge, @wrongSeason, 'tvdb_episode_id', '201');
                    """, new { show, season, right, wrongSeason,
                    seriesBridge = Guid.NewGuid(), rightBridge = Guid.NewGuid(), wrongBridge = Guid.NewGuid() });
            }
            var catalogue = new PairingCatalogueChild[]
            {
                new("101", "42", "tvdb", "Pilot", SeasonNumber: 1, EpisodeNumber: 1),
                new("201", "42", "tvdb", "Wrong season", SeasonNumber: 2, EpisodeNumber: 1),
            };
            var targets = new TvPairingLocalTargetReadService(database)
                .Resolve(show, "42", catalogue, CancellationToken.None);
            Assert.Single(targets);
            Assert.Equal(right, targets["101"].WorkId);
            Assert.DoesNotContain("201", targets.Keys);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
