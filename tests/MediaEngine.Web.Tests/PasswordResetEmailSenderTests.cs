using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class PasswordResetEmailSenderTests
{
    [Fact]
    public async Task UserTriggeredTestEmail_UsesConfiguredSmtpWithoutExternalDelivery()
    {
        using var configDirectory = new TempDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var conversation = ReceiveOneMessageAsync(listener, timeout.Token);
        var sender = new PasswordResetEmailSender(new PasswordResetDeliverySettings
        {
            Mode = "Smtp",
            SmtpHost = "127.0.0.1",
            SmtpPort = port,
            FromAddress = "library@example.test",
            FromName = "Tuvima Library",
            UseStartTls = false,
        }, Reader(configDirectory, "https://library.example.test"), NullLogger<PasswordResetEmailSender>.Instance);

        var identity = new DashboardIdentityClient(new AccountClientFactory("owner@example.test"));
        var sent = await DashboardAuthenticationEndpoints.SendCurrentAccountTestEmailAsync(
            identity, sender, timeout.Token);
        var transcript = await conversation;

        Assert.True(sent);
        Assert.Contains("RCPT TO:<owner@example.test>", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Subject: Tuvima Library email test", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No action is required", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetLinks_UseThePublicAddress()
    {
        using var configDirectory = new TempDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var conversation = ReceiveOneMessageAsync(listener, timeout.Token);
        var sender = new PasswordResetEmailSender(new PasswordResetDeliverySettings
        {
            Mode = "Smtp",
            SmtpHost = "127.0.0.1",
            SmtpPort = port,
            FromAddress = "library@example.test",
            UseStartTls = false,
        }, Reader(configDirectory, "https://library.example.test"), NullLogger<PasswordResetEmailSender>.Instance);

        var sent = await sender.SendAsync("owner@example.test", "reset-token", timeout.Token);
        // SMTP may quoted-printable encode "=" and soft-wrap long lines; undo both before comparing.
        var transcript = (await conversation).Replace("=\n", string.Empty).Replace("=3D", "=");

        Assert.True(sent);
        Assert.Contains("https://library.example.test/auth/reset?token=reset-token", transcript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://library.example.test")]
    [InlineData("https://library.example.test/tuvima")]
    public void SenderIsNotConfigured_WithoutAValidPublicAddress(string publicAddress)
    {
        using var configDirectory = new TempDirectory();
        var sender = new PasswordResetEmailSender(new PasswordResetDeliverySettings
        {
            Mode = "Smtp",
            SmtpHost = "127.0.0.1",
            FromAddress = "library@example.test",
        }, Reader(configDirectory, publicAddress), NullLogger<PasswordResetEmailSender>.Instance);

        Assert.False(sender.IsConfigured);
    }

    [Fact]
    public void ChangedPublicAddressApplies_WithoutRestart()
    {
        using var configDirectory = new TempDirectory();
        var reader = Reader(configDirectory, "");
        var sender = new PasswordResetEmailSender(new PasswordResetDeliverySettings
        {
            Mode = "Smtp",
            SmtpHost = "127.0.0.1",
            FromAddress = "library@example.test",
        }, reader, NullLogger<PasswordResetEmailSender>.Instance);
        Assert.False(sender.IsConfigured);

        File.WriteAllText(Path.Combine(configDirectory.Path, "network.json"),
            "{\"remote\":{\"public_hostname\":\"https://library.example.test\"}}");

        Assert.True(sender.IsConfigured);
    }

    private static DashboardConfigurationReader Reader(TempDirectory directory, string publicAddress)
    {
        File.WriteAllText(Path.Combine(directory.Path, "network.json"),
            $"{{\"remote\":{{\"public_hostname\":\"{publicAddress}\"}}}}");
        return new DashboardConfigurationReader(directory.Path);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tuvima-reset-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class AccountClientFactory(string email) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new AccountHandler(email))
        {
            BaseAddress = new Uri("https://engine.example.test"),
        };
    }

    private sealed class AccountHandler(string email) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/access/self-service", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AccountSelfServiceResponse(
                    Guid.NewGuid(), email, Guid.NewGuid(), Guid.NewGuid(), [], ["password"],
                    new AccountSecurityCapabilitiesResponse(
                        true, false, false, true, true, true, false, []))),
            });
        }
    }

    private static async Task<string> ReceiveOneMessageAsync(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n",
        };
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
            else if (line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250 OK");
            }
            else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                while (await reader.ReadLineAsync(ct) is { } bodyLine && bodyLine != ".")
                {
                    transcript.Add(bodyLine);
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
}
