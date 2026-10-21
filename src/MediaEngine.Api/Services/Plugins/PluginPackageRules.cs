using System.IO.Compression;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>Why a package was refused, in a sentence an administrator can read.</summary>
public sealed class PluginInstallException(string message) : Exception(message);

/// <summary>
/// The fixed rules for what a plugin package may be (Developer tools spec WP19): where it may come from, how big it may
/// be and how it is unpacked. A package comes from the internet, so every limit here fails closed.
/// </summary>
internal static partial class PluginPackageRules
{
    public const long MaxPackageBytes = 64L * 1024 * 1024;
    public const long MaxUnpackedBytes = 256L * 1024 * 1024;
    public const int MaxEntries = 2000;

    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9._-]{0,126}[A-Za-z0-9])?$")]
    private static partial Regex SafeIdPattern();

    // Names Windows reserves for devices; as a folder or file name they would not mean a file.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>
    /// A plugin id that is safe to use as a folder and file name: letters, digits, dot, dash and underscore, starting and ending
    /// with a letter or digit (a trailing dot is the same folder as none on Windows), no path parts and no reserved device name.
    /// </summary>
    public static bool IsSafeId(string? id) =>
        id is not null
        && SafeIdPattern().IsMatch(id)
        && !id.Contains("..", StringComparison.Ordinal)
        && !id.Split('.').Any(ReservedNames.Contains);

    /// <summary>Packages may only be fetched over https from GitHub (the repository, its release files and its raw files).</summary>
    public static bool IsAllowedPackageAddress(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort)
        {
            return false;
        }

        var host = uri.Host;
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Unpacks a package into an empty folder. Refuses a package with too many files, too many bytes, a path that leaves
    /// the folder, or anything that is not a plain file. Sizes are counted as bytes are written, never taken from the
    /// package's own headers. Returns the folder that holds <c>plugin.json</c> (the folder itself, or its one top-level folder).
    /// </summary>
    public static string Unpack(byte[] package, string destination)
    {
        try
        {
            return UnpackCore(package, destination);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            // A damaged compressed stream shows up while reading an entry, not when the archive opens.
            throw new PluginInstallException("The downloaded file is not a valid plugin package.");
        }
    }

    private static string UnpackCore(byte[] package, string destination)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new PluginInstallException("The downloaded file is not a plugin package.");
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxEntries)
            {
                throw new PluginInstallException("The plugin package has too many files.");
            }

            Directory.CreateDirectory(destination);
            var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long written = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/'))
                {
                    continue;
                }

                if (name.StartsWith('/') || name.Split('/').Any(part => part is ".." or "") || name.Contains(':', StringComparison.Ordinal))
                {
                    throw new PluginInstallException("The plugin package has a file path that is not allowed.");
                }

                // A link or other special file shows up as an attribute on the entry; only plain files are unpacked.
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType != 0 && unixType != 0x8000)
                {
                    throw new PluginInstallException("The plugin package contains a link or special file, which is not allowed.");
                }

                var target = Path.GetFullPath(Path.Combine(destination, name));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                {
                    throw new PluginInstallException("The plugin package has a file path that is not allowed.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = File.Create(target);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > MaxUnpackedBytes)
                    {
                        throw new PluginInstallException("The plugin package is larger than allowed once unpacked.");
                    }

                    output.Write(buffer, 0, read);
                }
            }
        }

        string top;
        if (File.Exists(Path.Combine(destination, "plugin.json")))
        {
            top = destination;
        }
        else
        {
            var folders = Directory.GetDirectories(destination);
            if (folders.Length != 1 || !File.Exists(Path.Combine(folders[0], "plugin.json")))
            {
                throw new PluginInstallException("The plugin package has no plugin.json at its top.");
            }

            top = folders[0];
        }

        // The plugins folder is scanned for every plugin.json under it, so a second one would register a plugin nobody was shown.
        var manifests = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Count(file => string.Equals(Path.GetFileName(file), "plugin.json", StringComparison.OrdinalIgnoreCase));
        if (manifests != 1)
        {
            throw new PluginInstallException("The plugin package holds more than one plugin.json, which is not allowed.");
        }

        return top;
    }

    /// <summary>A hash over every file in a plugin folder (sorted relative paths and contents), so a swapped or added file changes it.</summary>
    public static string HashTree(string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .Select(path => (Full: path, Relative: Path.GetFullPath(path)[root.Length..].Replace('\\', '/')))
                     .OrderBy(item => item.Relative, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file.Relative + "\n"));
            hash.AppendData(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file.Full)));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
