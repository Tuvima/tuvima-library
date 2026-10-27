using System.Security.Cryptography;
using System.Text.Json;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Services;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// One-button install and update from the plugin directory (Developer tools spec WP19). It downloads the package the
/// directory lists, checks it against the checksum the directory gives, checks the Tuvima signature for Verified and
/// Official plugins, unpacks it safely, checks that it says what the directory said, and only then puts it in the plugins
/// folder. The plugin is never switched on here: switching on is the approval step, and an update that asks for more than
/// was approved leaves the plugin off until it is approved again.
/// </summary>
public sealed class PluginPackageInstaller
{
    public const string HttpClientName = "plugin_package";

    private readonly PluginCatalog _catalog;
    private readonly PluginDirectoryService _directory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PluginSignatureVerifier _verifier;
    private readonly PluginBlocklist? _blocklist;
    private readonly ILogger<PluginPackageInstaller> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PluginPackageInstaller(
        PluginCatalog catalog,
        PluginDirectoryService directory,
        IHttpClientFactory httpClientFactory,
        PluginSignatureVerifier verifier,
        ILogger<PluginPackageInstaller> logger,
        TimeProvider? timeProvider = null,
        PluginBlocklist? blocklist = null)
    {
        _catalog = catalog;
        _directory = directory;
        _httpClientFactory = httpClientFactory;
        _verifier = verifier;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _blocklist = blocklist;
    }

    /// <summary>
    /// Installs the listed plugin, or replaces the installed copy with the listed version. Throws
    /// <see cref="KeyNotFoundException"/> when the plugin is not in the saved directory and <see cref="PluginInstallException"/>
    /// (with a plain sentence) when the package is refused.
    /// </summary>
    public async Task<PluginInstallResponse> InstallAsync(string pluginId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = await _directory.FindEntryAsync(pluginId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Plugin '{pluginId.Trim()}' is not in the plugin directory.");
            return await InstallEntryAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The only kinds of file a JSON-only plugin needs next to its plugin.json; anything else could carry code.
    private static readonly HashSet<string> UploadFileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".txt", ".png" };

    /// <summary>
    /// Adds a plugin from a package file the administrator already has (Plugins and Integrations screens spec D5 and D11).
    /// Only JSON-only (declarative) plugins are accepted: the Library reads them, they cannot run their own code, and nothing in
    /// the package can be an executable or script. The plugin is stored with no install record, so it is Private, and it is
    /// never switched on here: switching on is the approval step. Throws <see cref="PluginInstallException"/> with a plain
    /// sentence when the package is refused; nothing is left behind.
    /// </summary>
    public async Task<PluginInstallResponse> UploadAsync(byte[] package, CancellationToken ct = default)
    {
        if (package.Length == 0)
        {
            throw new PluginInstallException("The file is empty.");
        }

        if (package.Length > PluginPackageRules.MaxPackageBytes)
        {
            throw new PluginInstallException("The plugin package is larger than allowed.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var staging = Path.Combine(_catalog.StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            string unpacked;
            try
            {
                unpacked = PluginPackageRules.Unpack(package, Path.Combine(staging, "new"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "An uploaded plugin package could not be unpacked");
                throw new PluginInstallException("The plugin package could not be unpacked. Check that it is a normal zip file.");
            }

            foreach (var file in Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(unpacked, file).Replace('\\', '/');
                var isManifest = string.Equals(relative, "plugin.json", StringComparison.Ordinal);
                if (!isManifest && (!UploadFileExtensions.Contains(Path.GetExtension(file)) || !LooksLikeItsExtension(file)))
                {
                    throw new PluginInstallException(
                        $"The package contains a file the Library will not accept in an uploaded plugin ({Path.GetFileName(file)}). Uploaded plugins may hold only plugin.json and plain notes or pictures, because anything else could run code.");
                }
            }

            var manifestPath = Path.Combine(unpacked, "plugin.json");
            RequireStrictJsonOnlyManifest(manifestPath);
            var manifest = ReadManifest(manifestPath);
            if (PluginCompatibility.ValidateManifest(manifest) is { } invalid)
            {
                throw new PluginInstallException($"The plugin package is not valid: {invalid}");
            }

            if (!PluginPackageRules.IsSafeId(manifest.Id))
            {
                throw new PluginInstallException("This plugin's id cannot be used, so it cannot be added.");
            }

            if (manifest.Kind != PluginKind.Declarative
                || !string.IsNullOrWhiteSpace(manifest.EntryAssembly) || !string.IsNullOrWhiteSpace(manifest.EntryType)
                || manifest.Service is not null || manifest.Sandbox is not null)
            {
                throw new PluginInstallException("Only plugins described entirely in JSON can be uploaded. A plugin that runs code needs a signature and cannot be uploaded.");
            }

            if (_catalog.Get(manifest.Id) is { } existing)
            {
                throw new PluginInstallException(existing.IsBuiltIn
                    ? $"{existing.Manifest.Name} is built into the Library, so it cannot be replaced."
                    : $"A plugin with this id ({existing.Manifest.Name}) is already installed. Remove it first to upload another.");
            }

            if (_blocklist?.Find(manifest.Id, manifest.Version) is { } block)
            {
                throw new PluginInstallException(string.IsNullOrWhiteSpace(block.Reason) ? "This plugin has been blocked." : $"Blocked: {block.Reason}");
            }

            var compatibility = PluginCompatibility.Evaluate(manifest, LibraryVersion.Current, PluginCompatibility.CurrentRid);
            if (!compatibility.IsCompatible)
            {
                throw new PluginInstallException(compatibility.Problem ?? "This plugin does not work with this Library.");
            }

            var target = Path.Combine(_catalog.PluginRoot, manifest.Id.ToLowerInvariant());
            if (Directory.Exists(target))
            {
                throw new PluginInstallException("A folder for this plugin already exists. Remove it first to upload another.");
            }

            try
            {
                Directory.CreateDirectory(_catalog.PluginRoot);
                Directory.Move(unpacked, target);
                // No install record: a plugin with none is Private, the least trusted level.
                _catalog.InstallRecords.Delete(manifest.Id);
                _catalog.Reload();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Uploading plugin {PluginId} failed while writing files", manifest.Id);
                TryDelete(target);
                throw new PluginInstallException("The plugin could not be written to the plugins folder.");
            }

            var added = _catalog.Get(manifest.Id);
            if (added is null || added.Trust != PluginTrustLevel.Private || added.Enabled || !string.IsNullOrWhiteSpace(added.LoadError))
            {
                // The Engine's own detail can name folders on this computer, so it goes to the log and the person gets a plain sentence.
                _logger.LogWarning("Uploaded plugin {PluginId} was rolled back: {Problem}", manifest.Id, added?.LoadError);
                TryDelete(target);
                _catalog.Reload();
                throw new PluginInstallException("The plugin could not be loaded, so it was not added. Check its plugin.json.");
            }

            return new PluginInstallResponse
            {
                PluginId = manifest.Id,
                Name = manifest.Name,
                Version = manifest.Version,
                Message = $"{manifest.Name} was added as a Private plugin. It is switched off; review what it can do and switch it on from Plugins when you are ready.",
            };
        }
        finally
        {
            TryDelete(staging);
            _gate.Release();
        }
    }

    private async Task<PluginInstallResponse> InstallEntryAsync(PluginDirectoryEntryDto entry, CancellationToken ct)
    {
        if (!PluginPackageRules.IsSafeId(entry.Id))
        {
            throw new PluginInstallException("This plugin's id cannot be used, so it cannot be installed.");
        }

        var existing = _catalog.Get(entry.Id);
        if (existing is { IsBuiltIn: true })
        {
            throw new PluginInstallException($"{existing.Manifest.Name} is built into the Library, so there is nothing to install.");
        }

        var wasUpdate = false;
        if (existing is not null)
        {
            if (_catalog.InstallRecords.Find(entry.Id) is null)
            {
                throw new PluginInstallException($"{existing.Manifest.Name} was added by hand, so the directory will not replace it. Remove it first to install the listed one.");
            }

            if (PluginCompatibility.TryParseVersion(entry.Version, out var listed)
                && PluginCompatibility.TryParseVersion(existing.Manifest.Version, out var installedVersion)
                && listed <= installedVersion)
            {
                throw new PluginInstallException($"{existing.Manifest.Name} is already up to date.");
            }

            wasUpdate = true;
        }

        if (_blocklist?.Find(entry.Id, entry.Version) is { } block)
        {
            throw new PluginInstallException(string.IsNullOrWhiteSpace(block.Reason)
                ? "This plugin has been removed from the plugin directory."
                : $"Blocked: {block.Reason}");
        }

        var trust = entry.TrustLevel;
        var kind = entry.Kind;
        // An in-process plugin is code that runs with the Library's own access, so only a signed package may be one.
        if (kind == "official" && trust == "community")
        {
            throw new PluginInstallException("A plugin that runs inside the Library must be signed as verified before it can be installed.");
        }

        if (string.IsNullOrWhiteSpace(entry.PackageUrl) || !Uri.TryCreate(entry.PackageUrl, UriKind.Absolute, out var packageUri))
        {
            throw new PluginInstallException("The directory gives no download for this plugin.");
        }

        if (!PluginPackageRules.IsAllowedPackageAddress(packageUri))
        {
            throw new PluginInstallException("The plugin's download is not on GitHub, so the Library will not fetch it.");
        }

        if (string.IsNullOrWhiteSpace(entry.Sha256))
        {
            throw new PluginInstallException("The directory gives no checksum for this plugin, so it cannot be checked.");
        }

        var manifestFromList = ListedManifest(entry);
        var compatibility = PluginCompatibility.Evaluate(manifestFromList, LibraryVersion.Current, PluginCompatibility.CurrentRid);
        if (!compatibility.IsCompatible)
        {
            throw new PluginInstallException(compatibility.Problem ?? "This plugin does not work with this Library.");
        }

        var package = await DownloadAsync(packageUri, ct).ConfigureAwait(false);

        // Checksum: the package must be byte for byte the one the directory describes.
        var expected = Convert.FromHexString(entry.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(package), expected))
        {
            throw new PluginInstallException("The downloaded package does not match the checksum in the plugin directory, so it was not installed.");
        }

        // Signature: Verified and Official packages must carry a Tuvima signature; Community packages are checksum-only.
        var verified = trust is "verified" or "official";
        if (verified && !_verifier.Verify(package, entry.Signature))
        {
            throw new PluginInstallException(
                _verifier.HasKeys
                    ? "This plugin is listed as verified, but its signature does not verify, so it was not installed."
                    : "This plugin is listed as verified, but this Library has no signing key to confirm it, so it was not installed.");
        }

        var staging = Path.Combine(_catalog.StagingRoot, Guid.NewGuid().ToString("N"));
        var keepStaging = false;
        try
        {
            var unpacked = PluginPackageRules.Unpack(package, Path.Combine(staging, "new"));
            var manifestPath = Path.Combine(unpacked, "plugin.json");
            var manifest = ReadManifest(manifestPath);
            CheckManifestMatchesList(manifest, entry);

            var target = Path.Combine(_catalog.PluginRoot, entry.Id.ToLowerInvariant());
            var oldCopy = Path.Combine(staging, "old");
            Directory.CreateDirectory(_catalog.PluginRoot);
            var hadOldCopy = false;
            if (Directory.Exists(target))
            {
                Directory.Move(target, oldCopy);
                hadOldCopy = true;
            }

            try
            {
                Directory.Move(unpacked, target);
                _catalog.InstallRecords.Save(new PluginInstallRecord
                {
                    PluginId = entry.Id,
                    Version = manifest.Version,
                    Trust = verified ? "verified" : "community",
                    ManifestSha256 = PluginInstallRecords.HashFile(Path.Combine(target, "plugin.json")),
                    TreeSha256 = PluginPackageRules.HashTree(target),
                    PackageSha256 = entry.Sha256.ToLowerInvariant(),
                    InstalledAt = _time.GetUtcNow(),
                });
            }
            catch
            {
                try
                {
                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target, recursive: true);
                    }

                    if (hadOldCopy)
                    {
                        Directory.Move(oldCopy, target);
                    }
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    // The earlier copy is still in the staging folder: leave it there for recovery rather than delete it.
                    keepStaging = true;
                    _logger.LogError(rollback, "Rolling back the install of plugin {PluginId} failed; the earlier copy is kept at {Path}", entry.Id, oldCopy);
                }

                throw;
            }

            _catalog.Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Installing plugin {PluginId} failed while writing files", entry.Id);
            throw new PluginInstallException("The plugin could not be written to the plugins folder.");
        }
        finally
        {
            if (!keepStaging)
            {
                TryDelete(staging);
            }
        }

        var installed = _catalog.Get(entry.Id);
        var needsApprovalAgain = wasUpdate && installed is { NeedsApproval: true };
        return new PluginInstallResponse
        {
            PluginId = entry.Id,
            Name = entry.Name,
            Version = entry.Version,
            WasUpdate = wasUpdate,
            NeedsApprovalAgain = needsApprovalAgain,
            Message = (wasUpdate, needsApprovalAgain) switch
            {
                (true, true) => $"{entry.Name} was updated to {entry.Version}. It now asks for more than you approved, so it is switched off until you approve it again.",
                (true, false) => $"{entry.Name} was updated to {entry.Version}.",
                _ => $"{entry.Name} was installed. It is switched off; switch it on from Plugins when you are ready.",
            },
        };
    }

    private async Task<byte[]> DownloadAsync(Uri address, CancellationToken ct)
    {
        try
        {
            // The whole download, body included, gets two minutes; a trickling server must not hold every install.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            ct = limit.Token;
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            // GitHub hands release files over to its storage hosts; the file is only accepted when it still came from GitHub.
            if (response.RequestMessage?.RequestUri is { } final && !PluginPackageRules.IsAllowedPackageAddress(final))
            {
                throw new PluginInstallException("The plugin's download was redirected away from GitHub, so it was not installed.");
            }

            if (response.Content.Headers.ContentLength > PluginPackageRules.MaxPackageBytes)
            {
                throw new PluginInstallException("The plugin package is larger than allowed.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > PluginPackageRules.MaxPackageBytes)
                {
                    throw new PluginInstallException("The plugin package is larger than allowed.");
                }
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The plugin package could not be downloaded from {Address}", address.Host);
            throw new PluginInstallException("The plugin could not be downloaded. Check the connection and try again.");
        }
    }

    private static readonly HashSet<string> CodeManifestKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "entry_assembly", "entry_type", "service", "sandbox",
    };

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // The extension alone proves nothing: a program renamed to .png must not get in. Pictures need the PNG signature and
    // notes must be plain text without binary bytes.
    private static bool LooksLikeItsExtension(string file)
    {
        var head = new byte[8192];
        int read;
        using (var stream = File.OpenRead(file))
        {
            read = stream.Read(head, 0, head.Length);
        }

        if (string.Equals(Path.GetExtension(file), ".png", StringComparison.OrdinalIgnoreCase))
        {
            return read >= PngSignature.Length && head.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);
        }

        return !head.AsSpan(0, read).Contains((byte)0);
    }

    // The Engine reads plugin.json twice, once to check an upload and once to load it, with different rules for letter case
    // and repeated names. A manifest that reads as JSON-only to one and as a program to the other would slip through, so an
    // upload must name every top-level field once, in lowercase, and must not carry any code field at all.
    private static void RequireStrictJsonOnlyManifest(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PluginInstallException("The plugin package's plugin.json cannot be read.");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) || property.Name != property.Name.ToLowerInvariant())
                {
                    throw new PluginInstallException("The plugin package's plugin.json repeats a field or writes a field name in capitals, so it cannot be checked safely.");
                }

                if (CodeManifestKeys.Contains(property.Name))
                {
                    throw new PluginInstallException("Only plugins described entirely in JSON can be uploaded. A plugin that runs code needs a signature and cannot be uploaded.");
                }
            }
        }
        catch (JsonException)
        {
            throw new PluginInstallException("The plugin package's plugin.json cannot be read.");
        }
    }

    private static PluginManifest ReadManifest(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return document.Deserialize<PluginManifest>(MediaEngineJson.Web)
                ?? throw new PluginInstallException("The plugin package's plugin.json is empty.");
        }
        catch (JsonException)
        {
            throw new PluginInstallException("The plugin package's plugin.json cannot be read.");
        }
    }

    // What the person was shown before installing came from the directory, so the package must not ask for more than that.
    private static void CheckManifestMatchesList(PluginManifest manifest, PluginDirectoryEntryDto entry)
    {
        if (PluginCompatibility.ValidateManifest(manifest) is { } invalid)
        {
            throw new PluginInstallException($"The plugin package is not valid: {invalid}");
        }

        if (!string.Equals(manifest.Id, entry.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.Version, entry.Version, StringComparison.OrdinalIgnoreCase)
            || ToKindWord(manifest.Kind) != entry.Kind)
        {
            throw new PluginInstallException("The plugin package does not match what the plugin directory lists (id, version or kind), so it was not installed.");
        }

        var listedPermissions = new HashSet<string>(entry.Permissions, StringComparer.OrdinalIgnoreCase);
        if (manifest.Permissions.Any(permission => !listedPermissions.Contains(permission)))
        {
            throw new PluginInstallException("The plugin package asks for permissions the plugin directory does not list, so it was not installed.");
        }

        var listedHosts = new HashSet<string>(entry.Network.Select(destination => destination.Host), StringComparer.OrdinalIgnoreCase);
        if (manifest.Network.Any(destination => !listedHosts.Contains(destination.Host)))
        {
            throw new PluginInstallException("The plugin package contacts addresses the plugin directory does not list, so it was not installed.");
        }
    }

    private static PluginManifest ListedManifest(PluginDirectoryEntryDto entry) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        Version = entry.Version,
        Kind = entry.Kind switch
        {
            "declarative" => PluginKind.Declarative,
            "service" => PluginKind.Service,
            "sandboxed" => PluginKind.Sandboxed,
            _ => PluginKind.Official,
        },
        MinimumLibraryVersion = entry.MinimumLibraryVersion,
        MaximumLibraryVersion = entry.MaximumLibraryVersion,
        SupportedPlatforms = entry.SupportedPlatforms,
    };

    private static string ToKindWord(PluginKind kind) => kind switch
    {
        PluginKind.Declarative => "declarative",
        PluginKind.Service => "service",
        PluginKind.Sandboxed => "sandboxed",
        _ => "official",
    };

    private void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The plugin staging folder {Path} could not be cleaned up", path);
        }
    }
}
