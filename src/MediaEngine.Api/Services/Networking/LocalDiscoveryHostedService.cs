using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Networking;

/// <summary>
/// Small mDNS announcer/responder for the Tuvima service. It advertises only the
/// local Dashboard address and contains no library or profile information.
/// </summary>
public sealed class LocalDiscoveryHostedService : BackgroundService
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastAddress, 5353);
    private readonly IConfigurationLoader _configuration;
    private readonly INetworkEnvironmentService _environment;
    private readonly IScheduledTaskRegistry _registry;
    private readonly ILogger<LocalDiscoveryHostedService> _logger;

    public LocalDiscoveryHostedService(
        IConfigurationLoader configuration,
        INetworkEnvironmentService environment,
        IScheduledTaskRegistry registry,
        ILogger<LocalDiscoveryHostedService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(2);
        var task = _registry.Register(new ScheduledTaskDefinition(
            "local-discovery",
            "Announce the server on your home network",
            "Lets apps on your home network find this server by name.",
            ScheduleDescriber.Every(interval)));
        UdpClient? client = null;
        try
        {
            client = CreateClient();
            var receiveTask = ReceiveLoopAsync(client, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await AnnounceAsync(client, stoppingToken);
                task.Done();
                task.NextRunIn(interval);
                await Task.Delay(interval, stoppingToken);
            }
            await receiveTask;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Local discovery could not start; direct local addresses remain available");
            task.Failed(ex);
            task.NextRunAt(null);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var query = await client.ReceiveAsync(ct);
                if (!_configuration.LoadNetwork().Local.DiscoveryEnabled)
                {
                    continue;
                }

                var ascii = Encoding.ASCII.GetString(query.Buffer);
                if (ascii.Contains("_tuvima", StringComparison.OrdinalIgnoreCase)
                    || ascii.Contains(_configuration.LoadNetwork().Local.PreferredServerName, StringComparison.OrdinalIgnoreCase))
                {
                    await AnnounceAsync(client, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Local discovery query receive failed");
            }
        }
    }

    private async Task AnnounceAsync(UdpClient client, CancellationToken ct)
    {
        var settings = _configuration.LoadNetwork();
        if (!settings.Local.DiscoveryEnabled)
        {
            return;
        }

        var address = _environment.GetUsableAddresses(includeIpv6: false).FirstOrDefault()?.Address;
        if (!IPAddress.TryParse(address, out var ipAddress))
        {
            return;
        }

        var packet = BuildAnnouncement(settings.Local.PreferredServerName, settings.Local.Port, ipAddress);
        await client.SendAsync(packet, MulticastEndpoint, ct);
    }

    private static UdpClient CreateClient()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.ExclusiveAddressUse = false;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
        client.JoinMulticastGroup(MulticastAddress);
        client.MulticastLoopback = false;
        return client;
    }

    internal static byte[] BuildAnnouncement(string preferredName, int port, IPAddress address)
    {
        var host = $"{preferredName}.local";
        const string service = "_tuvima._tcp.local";
        const string instance = "Tuvima Library._tuvima._tcp.local";
        using var stream = new MemoryStream();
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0x8400);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 4);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WritePtr(stream, service, instance);
        WriteSrv(stream, instance, host, port);
        WriteTxt(stream, instance, ["path=/", "product=Tuvima Library"]);
        WriteAddress(stream, host, address);
        return stream.ToArray();
    }

    private static void WritePtr(Stream stream, string name, string target)
    {
        WriteName(stream, name);
        WriteRecordHeader(stream, 12, 120, GetNameLength(target));
        WriteName(stream, target);
    }

    private static void WriteSrv(Stream stream, string name, string target, int port)
    {
        WriteName(stream, name);
        WriteRecordHeader(stream, 33, 120, checked((ushort)(6 + GetNameLength(target))));
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, checked((ushort)port));
        WriteName(stream, target);
    }

    private static void WriteTxt(Stream stream, string name, IReadOnlyList<string> values)
    {
        var encoded = values.Select(Encoding.UTF8.GetBytes).ToList();
        WriteName(stream, name);
        WriteRecordHeader(stream, 16, 120, checked((ushort)encoded.Sum(value => value.Length + 1)));
        foreach (var value in encoded)
        {
            stream.WriteByte(checked((byte)value.Length));
            stream.Write(value);
        }
    }

    private static void WriteAddress(Stream stream, string name, IPAddress address)
    {
        WriteName(stream, name);
        var bytes = address.GetAddressBytes();
        WriteRecordHeader(stream, 1, 120, checked((ushort)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteRecordHeader(Stream stream, ushort type, uint ttl, ushort dataLength)
    {
        WriteUInt16(stream, type);
        WriteUInt16(stream, 0x8001);
        Span<byte> ttlBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ttlBytes, ttl);
        stream.Write(ttlBytes);
        WriteUInt16(stream, dataLength);
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            stream.WriteByte(checked((byte)bytes.Length));
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private static ushort GetNameLength(string name) =>
        checked((ushort)(name.Split('.', StringSplitOptions.RemoveEmptyEntries).Sum(label => Encoding.UTF8.GetByteCount(label) + 1) + 1));

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
