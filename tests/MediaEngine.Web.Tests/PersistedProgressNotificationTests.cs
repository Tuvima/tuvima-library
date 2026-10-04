using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Progress;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class PersistedProgressNotificationTests
{
    [Fact]
    public async Task AcknowledgedDirectSaveNotifiesOnceAndSubscriberFailureDoesNotFailSave()
    {
        var profile=Guid.NewGuid();var asset=Guid.NewGuid(); var accessor=new ActiveProfileAccessor();accessor.SetProfile(profile);
        var notifier=new UserProgressChangeNotifier();var seen=new List<(Guid,Guid)>();notifier.Changed+=(_,_)=>throw new InvalidOperationException();notifier.Changed+=(p,a)=>seen.Add((p,a));
        using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(Response(new UserStateResponse(profile,asset,"hash",42,null,[]) {Revision=7})))) {BaseAddress=new Uri("http://engine.test")};
        using var client=new EngineApiClient(http,NullLogger<EngineApiClient>.Instance,notifier,accessor);
        Assert.True(await client.SaveProgressAsync(asset,progressPct:42));Assert.Equal([(profile,asset)],seen);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LateDirectOrHeartbeatResponseAfterProfileSwitchCannotNotifyNewProfile(bool heartbeat)
    {
        var profile=Guid.NewGuid();var other=Guid.NewGuid();var asset=Guid.NewGuid();var accessor=new ActiveProfileAccessor();accessor.SetProfile(profile);
        var pending=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifier=new UserProgressChangeNotifier();var calls=0;notifier.Changed+=(_,_)=>calls++;
        long? requestedRevision=null; var requestCount=0;
        using var http=new HttpClient(new Handler(async (request,_)=>{ if(request.Method==HttpMethod.Put) { using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); requestedRevision=body.RootElement.GetProperty("expected_revision").GetInt64(); } started.TrySetResult();if (++requestCount > 1) return Response(new UserStateResponse(other,asset,"hash",42,null,[]) {Revision=1});return await pending.Task;})) {BaseAddress=new Uri("http://engine.test")};
        using var client=new EngineApiClient(http,NullLogger<EngineApiClient>.Instance,notifier,accessor);
        Task request=heartbeat?client.PostPlayerHeartbeatAsync(new PlayerHeartbeatDto {AssetId=asset,ProfileId=profile}):client.SaveProgressAsync(asset,progressPct:42);
        await started.Task;accessor.SetProfile(other);
        pending.SetResult(heartbeat?Response(new PlayerStateDto {ProfileId=profile}):Response(new UserStateResponse(profile,asset,"hash",42,null,[]) {Revision=8}));
        await request;Assert.Equal(0,calls);
        if (!heartbeat) { Assert.True(await client.SaveProgressAsync(asset,progressPct:42)); Assert.Equal(0,requestedRevision); Assert.Equal(1,calls); }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FailedResponsesDoNotNotify(bool heartbeat)
    {
        var accessor=new ActiveProfileAccessor();accessor.SetProfile(Guid.NewGuid());var notifier=new UserProgressChangeNotifier();var calls=0;notifier.Changed+=(_,_)=>calls++;
        using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))) {BaseAddress=new Uri("http://engine.test")};
        using var client=new EngineApiClient(http,NullLogger<EngineApiClient>.Instance,notifier,accessor);
        if(heartbeat) Assert.Null(await client.PostPlayerHeartbeatAsync(new PlayerHeartbeatDto {AssetId=Guid.NewGuid()}));else Assert.False(await client.SaveProgressAsync(Guid.NewGuid()));
        Assert.Equal(0,calls);
    }
    [Fact]
    public async Task AcknowledgedMatchingHeartbeatNotifiesExactAsset()
    {
        var profile=Guid.NewGuid();var asset=Guid.NewGuid();var accessor=new ActiveProfileAccessor();accessor.SetProfile(profile);var notifier=new UserProgressChangeNotifier();Guid? notified=null;notifier.Changed+=(p,a)=>{Assert.Equal(profile,p);notified=a;};
        using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(Response(new PlayerStateDto {ProfileId=profile})))) {BaseAddress=new Uri("http://engine.test")};
        using var client=new EngineApiClient(http,NullLogger<EngineApiClient>.Instance,notifier,accessor);
        Assert.NotNull(await client.PostPlayerHeartbeatAsync(new PlayerHeartbeatDto {AssetId=asset,ProfileId=profile}));Assert.Equal(asset,notified);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CancelledWritesDoNotNotify(bool heartbeat)
    {
        var accessor=new ActiveProfileAccessor();accessor.SetProfile(Guid.NewGuid());var notifier=new UserProgressChangeNotifier();var calls=0;notifier.Changed+=(_,_)=>calls++;
        using var http=new HttpClient(new Handler((_,ct)=>Task.FromCanceled<HttpResponseMessage>(ct))) {BaseAddress=new Uri("http://engine.test")};
        using var client=new EngineApiClient(http,NullLogger<EngineApiClient>.Instance,notifier,accessor);
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        if(heartbeat) Assert.Null(await client.PostPlayerHeartbeatAsync(new PlayerHeartbeatDto {AssetId=Guid.NewGuid()}, cancellation.Token));else Assert.False(await client.SaveProgressAsync(Guid.NewGuid(),ct:cancellation.Token));
        Assert.Equal(0,calls);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task HeartbeatWithMismatchedRequestOrResponseProfileDoesNotNotify(bool mismatchRequest)
    {
        var profile=Guid.NewGuid();var other=Guid.NewGuid();var accessor=new ActiveProfileAccessor();accessor.SetProfile(profile);var notifier=new UserProgressChangeNotifier();var calls=0;notifier.Changed+=(_,_)=>calls++;
        using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(Response(new PlayerStateDto {ProfileId=mismatchRequest?profile:other})))) {BaseAddress=new Uri("http://engine.test")};
        using var client=new EngineApiClient(http,NullLogger<EngineApiClient>.Instance,notifier,accessor);
        Assert.NotNull(await client.PostPlayerHeartbeatAsync(new PlayerHeartbeatDto {AssetId=Guid.NewGuid(),ProfileId=mismatchRequest?other:profile}));Assert.Equal(0,calls);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LateProgressReadCannotReplaceOrRemoveNewProfilesRevision(bool missing)
    {
        var profile=Guid.NewGuid();var other=Guid.NewGuid();var asset=Guid.NewGuid();var accessor=new ActiveProfileAccessor();accessor.SetProfile(profile);
        var pending=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);long? requestedRevision=null;
        using var http=new HttpClient(new Handler(async (request,_)=>{
            if(request.Method==HttpMethod.Get) {started.TrySetResult();return await pending.Task;}
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());requestedRevision=body.RootElement.GetProperty("expected_revision").GetInt64();
            return Response(new UserStateResponse(other,asset,"hash",42,null,[]) {Revision=3});
        })) {BaseAddress=new Uri("http://engine.test")};
        using var client=new EngineApiClient(http,NullLogger<EngineApiClient>.Instance,new UserProgressChangeNotifier(),accessor);
        var read=client.GetProgressAsync(asset);await started.Task;accessor.SetProfile(other);
        Assert.True(await client.SaveProgressAsync(asset,progressPct:42));Assert.Equal(0,requestedRevision);
        pending.SetResult(missing?new HttpResponseMessage(HttpStatusCode.NotFound):Response(new UserStateResponse(profile,asset,"hash",42,null,[]) {Revision=88}));await read;
        Assert.True(await client.SaveProgressAsync(asset,progressPct:42));Assert.Equal(3,requestedRevision);
    }
    private static HttpResponseMessage Response<T>(T value)=>new(HttpStatusCode.OK) {Content=JsonContent.Create(value)};
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request,ct);}
}
