using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace MediaEngine.Api.Services.Networking;

public sealed class RemoteAddressProbe(ILogger<RemoteAddressProbe> logger) : IRemoteAddressProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    public async Task<RemoteAddressProbeResult> ProbeAsync(Uri address, CancellationToken ct)
    {
        IPAddress[] resolved;
        try
        {
            resolved = await Dns.GetHostAddressesAsync(address.DnsSafeHost, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            logger.LogInformation(ex, "Remote check: {Host} did not resolve", address.DnsSafeHost);
            return new RemoteAddressProbeResult(false, [], false, null, null);
        }

        var addresses = resolved.Select(ip => ip.ToString()).ToArray();
        if (addresses.Length == 0)
        {
            return new RemoteAddressProbeResult(false, [], false, null, null);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            using var client = new TcpClient();
            await client.ConnectAsync(address.DnsSafeHost, address.Port, timeout.Token).ConfigureAwait(false);

            var errors = SslPolicyErrors.None;
            DateTimeOffset? expiresAt = null;
            using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, policyErrors) =>
            {
                // Accept here so the handshake finishes and the certificate can be read; the outcome is reported, not hidden.
                errors = policyErrors;
                if (certificate is X509Certificate2 typed)
                {
                    expiresAt = typed.NotAfter.ToUniversalTime();
                }
                else if (certificate is not null)
                {
                    using var copy = new X509Certificate2(certificate);
                    expiresAt = copy.NotAfter.ToUniversalTime();
                }

                return true;
            });
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = address.DnsSafeHost }, timeout.Token).ConfigureAwait(false);
            return new RemoteAddressProbeResult(true, addresses, true, errors == SslPolicyErrors.None, expiresAt);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                   || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Remote check: {Host} could not be reached securely", address.DnsSafeHost);
            return new RemoteAddressProbeResult(true, addresses, false, null, null);
        }
    }
}
