using System.Net;
using MediaEngine.Contracts.Backups;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Backups;

/// <summary>One destination an administrator added. The login is kept encrypted with this computer's key and is never sent back to the Dashboard.</summary>
internal sealed class ConfiguredDestination
{
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>A folder path, or the web address.</summary>
    public string Location { get; set; } = "";

    public string? Bucket { get; set; }

    public string? Region { get; set; }

    public string? Prefix { get; set; }

    /// <summary>The S3 access key id, or the WebDAV user name.</summary>
    public string? Username { get; set; }

    /// <summary>The S3 secret key or WebDAV password, encrypted by <see cref="ISecretStore"/>.</summary>
    public string? EncryptedSecret { get; set; }
}

/// <summary>
/// Builds the destinations an administrator added (another folder, a network share, S3-compatible storage, WebDAV) from the saved
/// settings (Developer tools spec WP26). They are built when asked for so a change takes effect at once.
/// </summary>
internal sealed class ConfiguredBackupDestinations(BackupSettingsAccessor settings, ISecretStore secrets, IBackupHttp http)
{
    public IReadOnlyList<IBackupDestination> Current() => Build(settings.Load());

    public IReadOnlyList<IBackupDestination> Build(BackupSettingsDocument document) =>
        [.. document.Destinations.Select(Create).OfType<IBackupDestination>()];

    private IBackupDestination? Create(ConfiguredDestination item) => item.Kind switch
    {
        BackupDestinationKinds.Folder or BackupDestinationKinds.Share => new FolderBackupDestination(item.Id, item.Name, item.Location, item.Kind == BackupDestinationKinds.Share),
        BackupDestinationKinds.S3 => new S3BackupDestination(item, Decrypt(item), http),
        BackupDestinationKinds.WebDav => new WebDavBackupDestination(item, Decrypt(item), http),
        _ => null,
    };

    private string Decrypt(ConfiguredDestination item) =>
        string.IsNullOrEmpty(item.EncryptedSecret) ? string.Empty : secrets.Decrypt(item.EncryptedSecret);
}

/// <summary>What may be saved as a destination. Every problem comes back as plain words for the person.</summary>
internal static class BackupDestinationRules
{
    public const int MaxNameLength = 60;

    /// <summary>The host of a web address, or null when the address is not acceptable. <paramref name="problem"/> explains why.</summary>
    public static Uri? ParseWebAddress(string? location, out string? problem)
    {
        problem = null;
        if (!Uri.TryCreate(location?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            problem = "Enter a web address that starts with https:// (or http:// for a device on your own network).";
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            problem = "Leave logins and extra parts out of the address. Enter them in their own boxes.";
            return null;
        }

        if (uri.Scheme == "http" && !IsLocalNetworkHost(uri.Host))
        {
            problem = "Plain http is only allowed for a device on your own network. Use https for anything on the internet.";
            return null;
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IsLinkLocal(address))
        {
            problem = "That address is not a storage service.";
            return null;
        }

        return uri;
    }

    private static bool IsLocalNetworkHost(string host)
    {
        // An address is judged as an address first: a bracketed IPv6 literal has no dot but is not "a plain name on my network".
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168)
                : (bytes[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal;
        }

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cloud metadata and other link-local addresses are never a place to send a backup.</summary>
    private static bool IsLinkLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.IsIPv6LinkLocal
            || address.Equals(IPAddress.Any)
            || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..])
            || address.Equals(IPAddress.Parse("fd00:ec2::254"));
    }

    /// <summary>Reads a server's XML answer without letting it define entities (a hostile or spoofed server could otherwise expand a tiny document into gigabytes), and refuses an oversized answer.</summary>
    public static async Task<System.Xml.Linq.XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxXmlBytes)
        {
            throw new BackupDestinationException("The destination's answer was too large to read.");
        }

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.Length > MaxXmlBytes)
        {
            throw new BackupDestinationException("The destination's answer was too large to read.");
        }

        using var reader = System.Xml.XmlReader.Create(new StringReader(text), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        return System.Xml.Linq.XDocument.Load(reader);
    }

    private const int MaxXmlBytes = 8_000_000;

    /// <summary>A folder path must be complete (it starts at a drive, a share or the root) and hold no <c>..</c> steps.</summary>
    public static string? FolderProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            return "Enter the full path of the folder, for example D:\\Backups or \\\\nas\\backups.";
        }

        return path.Split('\\', '/').Any(part => part == "..") ? "The folder path cannot contain \"..\"." : null;
    }
}
