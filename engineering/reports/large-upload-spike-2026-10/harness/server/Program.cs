// WP11 upload spike harness. Throwaway; not product code.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 30L * 1024 * 1024); // Kestrel default, raised per endpoint
var app = builder.Build();

var store = Environment.GetEnvironmentVariable("SPIKE_STORE") ?? Path.Combine(Path.GetTempPath(), "spike-store");
Directory.CreateDirectory(store);
var sinkDiscard = Environment.GetEnvironmentVariable("SPIKE_SINK") == "discard";
double rateMbps = 0; // 0 = unthrottled; otherwise emulates a network of this many MB/s
var log = new ConcurrentQueue<object>();
var sessions = new ConcurrentDictionary<string, ChunkSession>();
long dropAt = -1; // absolute byte offset at which to cut the connection once
var proc = Process.GetCurrentProcess();
long peakWs = 0;
_ = Task.Run(async () =>
{
    while (true)
    {
        proc.Refresh();
        var ws = proc.WorkingSet64;
        if (ws > Interlocked.Read(ref peakWs)) Interlocked.Exchange(ref peakWs, ws);
        await Task.Delay(100);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/metrics", () =>
{
    proc.Refresh();
    return Results.Json(new { workingSetMb = proc.WorkingSet64 / 1048576.0, peakWorkingSetMb = Interlocked.Read(ref peakWs) / 1048576.0, gcHeapMb = GC.GetTotalMemory(false) / 1048576.0, storeBytes = Directory.EnumerateFiles(store).Sum(f => new FileInfo(f).Length) });
});
app.MapPost("/metrics/reset", () => { GC.Collect(); proc.Refresh(); Interlocked.Exchange(ref peakWs, proc.WorkingSet64); return Results.Ok(); });
app.MapPost("/fault", (long at) => { Interlocked.Exchange(ref dropAt, at); return Results.Ok(); });
app.MapPost("/config", (string? sink, double? mbps) => { if (sink is not null) sinkDiscard = sink == "discard"; if (mbps is not null) rateMbps = mbps.Value; return Results.Ok(new { sinkDiscard, rateMbps }); });
app.MapGet("/log", () => Results.Json(log.ToArray()));
app.MapPost("/clean", () => { log.Clear(); foreach (var f in Directory.EnumerateFiles(store)) File.Delete(f); sessions.Clear(); return Results.Ok(); });

Stream OpenSink(string path, long offset)
{
    if (sinkDiscard) return Stream.Null;
    var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
    fs.Position = offset;
    return fs;
}

// Copy with fault injection: returns bytes copied; aborts the connection once when the absolute offset passes dropAt.
async Task<long> CopyAsync(HttpContext ctx, Stream sink, long startOffset, CancellationToken ct)
{
    var buf = new byte[1 << 20];
    long copied = 0;
    int n;
    var clock = Stopwatch.StartNew();
    while ((n = await ctx.Request.Body.ReadAsync(buf, ct)) > 0)
    {
        await sink.WriteAsync(buf.AsMemory(0, n), ct);
        copied += n;
        if (rateMbps > 0)
        {
            var due = TimeSpan.FromSeconds(copied / (rateMbps * 1048576));
            if (due > clock.Elapsed) await Task.Delay(due - clock.Elapsed);
        }
        var at = Interlocked.Read(ref dropAt);
        if (at >= 0 && startOffset + copied >= at)
        {
            Interlocked.Exchange(ref dropAt, -1);
            await sink.FlushAsync();
            ctx.Abort();
            throw new IOException("injected connection drop");
        }
    }
    return copied;
}

// Plain: whole file in one request body. A dropped connection loses everything.
app.MapPost("/plain", async (HttpContext ctx, string name, long size) =>
{
    ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
    var partial = Path.Combine(store, name + ".partial");
    var sw = Stopwatch.StartNew();
    long copied = 0;
    try
    {
        await using (var sink = OpenSink(partial, 0)) copied = await CopyAsync(ctx, sink, 0, ctx.RequestAborted);
        if (!sinkDiscard) File.Move(partial, Path.Combine(store, name), true);
        log.Enqueue(new { mode = "plain", name, size, copied, seconds = sw.Elapsed.TotalSeconds, ok = copied == size });
        return Results.Ok(new { copied });
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException)
    {
        if (File.Exists(partial)) File.Delete(partial);
        log.Enqueue(new { mode = "plain", name, size, copied, seconds = sw.Elapsed.TotalSeconds, ok = false, error = ex.Message });
        return Results.StatusCode(499);
    }
});

// Chunked and resumable (tus-like): create, ask offset, append at offset, cancel.
app.MapPost("/chunked", (string name, long size) =>
{
    var id = Guid.NewGuid().ToString("N");
    sessions[id] = new ChunkSession(name, size, Path.Combine(store, name + ".partial"));
    return Results.Json(new { id });
});
app.MapGet("/chunked/{id}", (string id) => sessions.TryGetValue(id, out var s) ? Results.Json(new { offset = s.Offset }) : Results.NotFound());
// Cancel stops the piece in flight first, then deletes: Windows refuses to delete a file that is still open.
app.MapDelete("/chunked/{id}", async (string id) =>
{
    if (!sessions.TryRemove(id, out var s)) return Results.Ok();
    s.Cancel.Cancel();
    await s.Gate.WaitAsync();
    try
    {
        if (File.Exists(s.Path)) File.Delete(s.Path);
    }
    finally { s.Gate.Release(); }
    return Results.Ok();
});
app.MapPut("/chunked/{id}", async (HttpContext ctx, string id, long offset) =>
{
    ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 512L * 1024 * 1024;
    if (!sessions.TryGetValue(id, out var s)) return Results.NotFound();
    if (!await s.Gate.WaitAsync(0)) return Results.Conflict(new { offset = s.Offset });
    try
    {
        if (offset != s.Offset) return Results.Conflict(new { offset = s.Offset });
        long copied = 0;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, s.Cancel.Token);
            await using var sink = OpenSink(s.Path, offset);
            copied = await CopyAsync(ctx, sink, offset, linked.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException)
        {
            // Keep what reached the disk (flushed by the stream's dispose), so the client resumes from there.
            s.Offset = sinkDiscard ? offset + copied : new FileInfo(s.Path).Length;
            s.Drops++;
            return Results.StatusCode(499);
        }
        s.Offset = offset + copied;
        if (s.Offset == s.Size)
        {
            if (!sinkDiscard) File.Move(s.Path, Path.Combine(store, s.Name), true);
            log.Enqueue(new { mode = "chunked", name = s.Name, size = s.Size, drops = s.Drops, seconds = s.Clock.Elapsed.TotalSeconds, ok = true });
            sessions.TryRemove(id, out _);
        }
        return Results.Json(new { offset = s.Offset });
    }
    finally { s.Gate.Release(); }
});

// Relay mode: behaves like a Dashboard proxy endpoint, streaming the browser's body on to the Engine
// with a server-side HttpClient (default 100 s timeout unless SPIKE_RELAY_TIMEOUT is set).
var upstream = Environment.GetEnvironmentVariable("SPIKE_UPSTREAM");
if (upstream is not null)
{
    var relayClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(upstream) };
    if (Environment.GetEnvironmentVariable("SPIKE_RELAY_TIMEOUT") is { } t) relayClient.Timeout = TimeSpan.FromSeconds(double.Parse(t));
    app.Map("/relay/{**rest}", async (HttpContext ctx, string rest) =>
    {
        ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
        var msg = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), rest + ctx.Request.QueryString);
        if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        {
            msg.Content = new StreamContent(ctx.Request.Body, 1 << 20);
            if (ctx.Request.ContentLength is { } len) msg.Content.Headers.ContentLength = len;
        }
        try
        {
            using var resp = await relayClient.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString();
            await resp.Content.CopyToAsync(ctx.Response.Body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            log.Enqueue(new { mode = "relay", rest, error = ex.GetType().Name + ": " + ex.Message });
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502;
        }
    });
}

app.Run();

sealed class ChunkSession(string name, long size, string path)
{
    public string Name { get; } = name;
    public long Size { get; } = size;
    public string Path { get; } = path;
    public long Offset { get; set; }
    public int Drops { get; set; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public CancellationTokenSource Cancel { get; } = new();
    public Stopwatch Clock { get; } = Stopwatch.StartNew();
}
