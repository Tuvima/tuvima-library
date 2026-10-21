namespace MediaEngine.Api.Services.Backups;

/// <summary>The web connection backup destinations use. One shared connection with no time limit (a large copy takes as long as it takes) and no redirects, so a copy never ends up somewhere the person did not approve.</summary>
internal interface IBackupHttp
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

internal sealed class BackupHttp : IBackupHttp, IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

    public void Dispose() => _client.Dispose();
}

/// <summary>Plain-words problems for a web destination. Nothing here ever includes a login, an address or a response body.</summary>
internal static class BackupHttpMessages
{
    public static BackupDestinationException For(HttpResponseMessage response) => new((int)response.StatusCode switch
    {
        401 or 403 => "The destination refused the login. Check the user name and password (or keys).",
        404 => "The destination or folder was not found. Check the address and the bucket or folder name.",
        409 or 405 => "The destination does not accept that. Check the folder exists and can be written to.",
        507 => "The destination is full.",
        >= 500 => "The destination had a problem of its own. Try again later.",
        _ => "The destination did not accept the request.",
    });

    public static BackupDestinationException Unreachable(Exception ex) => new(
        ex is TaskCanceledException ? "The destination did not answer in time." : "The destination could not be reached. Check the address and your connection.", ex);
}

/// <summary>Lets a request read a stream without closing it, so the same copy can be sent again after a folder is made.</summary>
internal sealed class LeaveOpenStream(Stream inner) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The stream belongs to the caller; leaving it open is the point of this wrapper.
    }
}
