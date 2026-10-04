using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Providers;
using Microsoft.Extensions.Logging.Abstractions;
namespace MediaEngine.Providers.Tests;
public sealed class LrclibDurationTests
{
    [Theory]
    [InlineData(0,1)] [InlineData(2,1)] [InlineData(3,.7)] [InlineData(10,0)]
    public async Task DurationBandsKeepCloseSyncAndReduceAlternateConfidence(double delta,double score)
    {
        var candidate=Assert.Single(await Provider(180+delta,true).SearchAsync(Lookup()));
        Assert.Equal(score,candidate.DurationMatchScore!.Value,5);
        Assert.Equal(score,candidate.Confidence,5);
    }
    [Fact] public async Task LargeDurationMismatchRejectsTimedLyricsButRetainsOrdinaryText()
    {
        Assert.Empty(await Provider(191,true).SearchAsync(Lookup()));
        var plain=Assert.Single(await Provider(191,false).SearchAsync(Lookup()));
        Assert.Equal("txt",plain.SourceFormat);
    }
    private static TextTrackLookup Lookup()=>new(new MediaAsset {Id=Guid.NewGuid(),FilePathRoot="fixture.wav"},MediaType.Music,"Fixture song","Artist","Album",null,"en",180,new Dictionary<string,string>());
    private static LrclibTextTrackProvider Provider(double duration,bool synced)=>new(new ProviderConfiguration {Name="LRCLIB",Enabled=true},new Factory(JsonSerializer.Serialize(new {id=1,duration,syncedLyrics=synced?"[00:01]Line":null,plainLyrics="Line"})),new Cache(),NullProviderHealthMonitor.Instance,NullLogger<LrclibTextTrackProvider>.Instance);
    private sealed class Factory(string json):IHttpClientFactory {public HttpClient CreateClient(string name)=>new(new Handler(json));}
    private sealed class Handler(string json):HttpMessageHandler {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")});}
    private sealed class Cache:IProviderResponseCacheRepository
    {
        public Task<CachedResponse?> FindAsync(string key,CancellationToken ct=default)=>Task.FromResult<CachedResponse?>(null);
        public Task UpsertAsync(string key,string provider,string query,string json,string? etag,int ttl,CancellationToken ct=default)=>Task.CompletedTask;
        public Task<string?> FindExpiredEtagAsync(string key,CancellationToken ct=default)=>Task.FromResult<string?>(null);
        public Task RefreshExpiryAsync(string key,int ttl,CancellationToken ct=default)=>Task.CompletedTask;
        public Task<int> PurgeExpiredAsync(CancellationToken ct=default)=>Task.FromResult(0);
        public Task<int> ClearAllAsync(CancellationToken ct=default)=>Task.FromResult(0);
        public Task<CacheStats> GetStatsAsync(CancellationToken ct=default)=>Task.FromResult(new CacheStats(0,0,null));
    }
}
