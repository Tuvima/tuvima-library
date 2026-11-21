using System.Net;
using System.Net.Sockets;
using MediaEngine.Api.Services.Notifications;
using MediaEngine.Contracts.Notifications;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>The mail server form (Notifications and delivery spec WP1): save, never show the password, test without saving.</summary>
public sealed class MailServerSettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tuvima_mail_server_{Guid.NewGuid():N}");
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly MailServerSettingsService _service;

    public MailServerSettingsServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _loader = new ConfigurationDirectoryLoader(_directory);
        _service = new MailServerSettingsService(_loader, NullLogger<MailServerSettingsService>.Instance);
    }

    [Fact]
    public async Task Save_StoresThePasswordInTheKeyFileAndNeverReturnsIt()
    {
        var problem = await _service.SaveAsync(Request(password: "app-secret"), CancellationToken.None);

        Assert.Null(problem);
        var saved = _service.Get();
        Assert.True(saved.Configured);
        Assert.True(saved.HasPassword);
        Assert.Equal("smtp.example.test", saved.Host);
        Assert.DoesNotContain("app-secret", File.ReadAllText(Path.Combine(_directory, "core.json")), StringComparison.Ordinal);
        Assert.Contains("app-secret", File.ReadAllText(Path.Combine(_directory, ".secrets", "email.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_WithNoPassword_KeepsTheSavedOne()
    {
        await _service.SaveAsync(Request(password: "app-secret"), CancellationToken.None);

        await _service.SaveAsync(Request(password: null, host: "smtp.other.test"), CancellationToken.None);

        var saved = _service.Get();
        Assert.Equal("smtp.other.test", saved.Host);
        Assert.True(saved.HasPassword);
    }

    [Theory]
    [InlineData("", 587, "me@example.test")]
    [InlineData("smtp.example.test", 0, "me@example.test")]
    [InlineData("smtp.example.test", 587, "not an address")]
    public async Task Save_RefusesAFormThatCannotWork(string host, int port, string from)
    {
        var problem = await _service.SaveAsync(Request(host: host, port: port, from: from), CancellationToken.None);

        Assert.NotNull(problem);
        Assert.False(_service.Get().Configured);
    }

    [Fact]
    public async Task Test_WithoutAnAdministratorAddress_SaysSoAndSendsNothing()
    {
        var result = await _service.TestAsync(Request(), string.Empty, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no email address", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_SendsWithTheFormValuesAndSavesNothing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var conversation = ReceiveOneMessageAsync(listener, timeout.Token);

        var result = await _service.TestAsync(Request(host: "127.0.0.1", port: port, encrypted: false), "owner@example.test", timeout.Token);
        var transcript = await conversation;

        Assert.True(result.Success, result.Message);
        Assert.Contains("RCPT TO:<owner@example.test>", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.False(_service.Get().Configured);
    }

    [Fact]
    public async Task Test_WhenTheServerCannotBeReached_ExplainsInPlainWordsWithoutTheAddress()
    {
        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        var result = await _service.TestAsync(Request(host: "127.0.0.1", port: port, encrypted: false), "owner@example.test", CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("127.0.0.1", result.Message, StringComparison.Ordinal);
    }

    private static MailServerRequest Request(string? password = null, string host = "smtp.example.test", int port = 587, string from = "library@example.test", bool encrypted = true) => new()
    {
        Host = host,
        Port = port,
        Encrypted = encrypted,
        Username = "library@example.test",
        Password = password,
        FromAddress = from,
        FromName = "Library",
    };

    private static async Task<string> ReceiveOneMessageAsync(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        var transcript = new List<string>();
        await writer.WriteLineAsync("220 localhost ESMTP ready");
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            transcript.Add(line);
            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250-localhost");
                await writer.WriteLineAsync("250 8BITMIME");
            }
            else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                while (await reader.ReadLineAsync(ct) is { } body && body != ".")
                {
                    transcript.Add(body);
                }

                await writer.WriteLineAsync("250 queued");
            }
            else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("221 bye");
                break;
            }
            else
            {
                await writer.WriteLineAsync("250 OK");
            }
        }

        return string.Join('\n', transcript);
    }

    public void Dispose()
    {
        _loader.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
