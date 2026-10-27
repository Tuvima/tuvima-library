using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Install, update and remove from the plugin directory (Developer tools spec WP19): a tampered package is refused, a
/// checked plugin needs a valid Tuvima signature, an update that asks for more leaves the plugin off until approved
/// again, and removing a plugin that is the only source for something says so first.
/// </summary>
public sealed class PluginPackageInstallerTests : IDisposable
{
    private const string ListUrl = "https://raw.githubusercontent.com/Tuvima/tuvima-library/main/docs/reference/approved-plugins.json";
    private const string PackageUrl = "https://github.com/acme/one/releases/download/v1/one.zip";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-install-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginSettingsService _settings;
    private readonly Router _router = new();
    private readonly RecordingApprovals _approvals = new();
    private PluginDirectoryService? _directory;
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public PluginPackageInstallerTests()
    {
        var configRoot = Path.Combine(_root, "config");
        _loader = new ConfigurationDirectoryLoader(configRoot, new ConfigFolderSecretsStore(configRoot));
        var core = _loader.LoadCore();
        core.LibraryRoot = Path.Combine(_root, "library");
        core.PluginCatalog.Enabled = true;
        core.PluginCatalog.ApprovedPluginsUrl = ListUrl;
        _loader.SaveCore(core);
        Directory.CreateDirectory(core.LibraryRoot);
        _settings = new PluginSettingsService(_loader);
    }

    [Fact]
    public async Task ACommunityPlugin_WithAMatchingChecksum_IsInstalledSwitchedOff_AndTrustedOnlyAsCommunity()
    {
        var package = Package("acme.one", "1.0.0");
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", package, trust: "community"), package);

        var result = await installer.InstallAsync("acme.one");

        Assert.False(result.WasUpdate);
        var registration = catalog.Get("acme.one")!;
        Assert.Equal(PluginTrustLevel.Community, registration.Trust);
        Assert.False(registration.Enabled);
        Assert.NotNull(catalog.InstallRecords.Find("acme.one"));
    }

    [Fact]
    public async Task ATamperedPackage_IsRefused_AndNothingIsLeftBehind()
    {
        var real = Package("acme.one", "1.0.0");
        var tampered = Package("acme.one", "1.0.0", extra: "evil.txt");
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", real), tampered);

        var ex = await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));

        Assert.Contains("checksum", ex.Message, StringComparison.Ordinal);
        Assert.Null(catalog.Get("acme.one"));
        Assert.False(Directory.Exists(Path.Combine(catalog.PluginRoot, "acme.one")));
    }

    [Fact]
    public async Task ACheckedPlugin_WithAValidTuvimaSignature_IsInstalledAsVerified()
    {
        var package = Package("acme.one", "1.0.0");
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", package, trust: "verified", signature: Sign(package)), package, trustedKeys: [PublicKey()]);

        await installer.InstallAsync("acme.one");

        Assert.Equal(PluginTrustLevel.Verified, catalog.Get("acme.one")!.Trust);
    }

    [Fact]
    public async Task ACheckedPlugin_WithABadSignature_IsRefused()
    {
        var package = Package("acme.one", "1.0.0");
        var other = ECDsa.Create(ECCurve.NamedCurves.nistP256).SignData(Encoding.UTF8.GetBytes("not the package"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var (installer, catalog) = Build(
            Listing("acme.one", "1.0.0", package, trust: "verified", signature: Convert.ToBase64String(other)), package, trustedKeys: [PublicKey()]);

        var ex = await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));

        Assert.Contains("signature", ex.Message, StringComparison.Ordinal);
        Assert.Null(catalog.Get("acme.one"));
    }

    [Fact]
    public async Task ACheckedPlugin_WhenTheLibraryHasNoSigningKey_IsRefusedRatherThanTrusted()
    {
        var package = Package("acme.one", "1.0.0");
        var (installer, _) = Build(Listing("acme.one", "1.0.0", package, trust: "verified", signature: Sign(package)), package);

        await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));
    }

    [Fact]
    public async Task ACodePluginThatIsNotChecked_IsRefused()
    {
        var package = Package("acme.one", "1.0.0", kind: "official");
        var (installer, _) = Build(Listing("acme.one", "1.0.0", package, kind: "official"), package);

        var ex = await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));

        Assert.Contains("signed as verified", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackageThatAsksForMoreThanTheDirectoryListed_IsRefused()
    {
        var package = Package("acme.one", "1.0.0", permissions: ["library-events"]);
        var (installer, _) = Build(Listing("acme.one", "1.0.0", package), package);

        var ex = await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));

        Assert.Contains("permissions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackageWhoseIdDiffersFromTheListing_IsRefused()
    {
        var package = Package("acme.other", "1.0.0");
        var (installer, _) = Build(Listing("acme.one", "1.0.0", package), package);

        await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../escape.txt")]
    public async Task APackageWithAPathThatLeavesItsFolder_IsRefused(string badPath)
    {
        var package = Package("acme.one", "1.0.0", rawEntry: badPath);
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", package), package);

        await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));

        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.Null(catalog.Get("acme.one"));
    }

    [Fact]
    public async Task ADownloadAddressOffGitHub_IsRefusedWithoutAnyRequest()
    {
        var package = Package("acme.one", "1.0.0");
        var (installer, _) = Build(Listing("acme.one", "1.0.0", package, packageUrl: "https://evil.example.net/one.zip"), package);

        await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));

        Assert.DoesNotContain(_router.Requested, address => address.Contains("evil.example.net", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADownloadRedirectedAwayFromGitHub_IsRefused()
    {
        var package = Package("acme.one", "1.0.0");
        _router.RedirectPackageTo = "https://evil.example.net/one.zip";
        var (installer, _) = Build(Listing("acme.one", "1.0.0", package), package);

        await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));
    }

    [Fact]
    public async Task AnUpdateThatAddsAPermission_LeavesThePluginOffUntilApprovedAgain()
    {
        var v1 = Package("acme.one", "1.0.0", permissions: ["library-events"]);
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", v1, permissions: ["library-events"]), v1);
        await installer.InstallAsync("acme.one");
        _approvals.Approve(catalog.Get("acme.one")!);
        catalog.SetEnabled("acme.one", true);
        Assert.True(catalog.Get("acme.one")!.Enabled);

        var v2 = Package("acme.one", "1.1.0", permissions: ["library-events", "watch-activity"]);
        _router.Listing = Listing("acme.one", "1.1.0", v2, permissions: ["library-events", "watch-activity"]);
        _router.PackageBytes = v2;
        await _directory!.CheckNowAsync();
        var result = await installer.InstallAsync("acme.one");

        Assert.True(result.WasUpdate);
        Assert.True(result.NeedsApprovalAgain);
        var registration = catalog.Get("acme.one")!;
        Assert.Equal("1.1.0", registration.Manifest.Version);
        Assert.False(registration.Enabled);
        Assert.True(registration.NeedsApproval);
    }

    [Fact]
    public async Task AnUpdateThatAsksForNothingNew_KeepsThePluginSwitchedOn()
    {
        var v1 = Package("acme.one", "1.0.0");
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", v1), v1);
        await installer.InstallAsync("acme.one");
        _approvals.Approve(catalog.Get("acme.one")!);
        catalog.SetEnabled("acme.one", true);

        var v2 = Package("acme.one", "1.1.0");
        _router.Listing = Listing("acme.one", "1.1.0", v2);
        _router.PackageBytes = v2;
        await _directory!.CheckNowAsync();
        var result = await installer.InstallAsync("acme.one");

        Assert.False(result.NeedsApprovalAgain);
        Assert.True(catalog.Get("acme.one")!.Enabled);
    }

    [Fact]
    public async Task AnInstalledCopyThatWasEditedAfterTheCheck_NoLongerCountsAsCommunity()
    {
        var package = Package("acme.one", "1.0.0");
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", package), package);
        await installer.InstallAsync("acme.one");
        var manifest = Path.Combine(catalog.PluginRoot, "acme.one", "plugin.json");

        File.AppendAllText(manifest, " ");
        catalog.Reload();

        Assert.Equal(PluginTrustLevel.Private, catalog.Get("acme.one")!.Trust);
    }

    [Fact]
    public async Task AFileAddedToTheInstalledFolder_MeansItIsNoLongerTheCheckedPackage()
    {
        var package = Package("acme.one", "1.0.0");
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", package), package);
        await installer.InstallAsync("acme.one");

        File.WriteAllText(Path.Combine(catalog.PluginRoot, "acme.one", "swapped.dll"), "x");
        catalog.Reload();

        Assert.Equal(PluginTrustLevel.Private, catalog.Get("acme.one")!.Trust);
    }

    [Fact]
    public async Task ACopyOfTheManifestInAnotherFolder_DoesNotInheritTheInstalledTrust()
    {
        var package = Package("acme.one", "1.0.0");
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", package), package);
        await installer.InstallAsync("acme.one");
        var copy = Path.Combine(catalog.PluginRoot, "elsewhere");
        Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(catalog.PluginRoot, "acme.one", "plugin.json"), Path.Combine(copy, "plugin.json"));

        var trust = catalog.InstallRecords.TrustFor(catalog.Get("acme.one")!.Manifest, Path.Combine(copy, "plugin.json"));

        Assert.Equal(PluginTrustLevel.Private, trust);
    }

    [Fact]
    public async Task APackageHoldingASecondManifest_IsRefused()
    {
        var package = Package("acme.one", "1.0.0", rawEntry: "nested/plugin.json");
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", package), package);

        var ex = await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));

        Assert.Contains("more than one plugin.json", ex.Message, StringComparison.Ordinal);
        Assert.Null(catalog.Get("acme.one"));
    }

    [Theory]
    [InlineData("foo.")]
    [InlineData("nul")]
    [InlineData("acme.com1")]
    [InlineData("-lead")]
    public void UnsafeIds_AreNotAllowedToBecomeFolderNames(string id)
    {
        Assert.False(PluginPackageRules.IsSafeId(id));
    }

    [Fact]
    public void ACorruptCompressedEntry_IsRefusedWithAPlainSentence_NotAnUnexpectedError()
    {
        var good = Package("acme.one", "1.0.0", extra: "data.bin");
        // Damage the compressed bytes of the last entry without touching the archive's directory.
        for (var i = 40; i < 60; i++)
        {
            good[i] ^= 0xFF;
        }

        var destination = Path.Combine(_root, "corrupt");
        var thrown = Record.Exception(() => PluginPackageRules.Unpack(good, destination));

        Assert.True(thrown is null or PluginInstallException, thrown?.ToString());
    }

    [Fact]
    public async Task APluginAddedByHand_IsNotReplacedByTheDirectory()
    {
        var package = Package("acme.one", "1.1.0");
        var (installer, catalog) = Build(Listing("acme.one", "1.1.0", package), package);
        var folder = Path.Combine(catalog.PluginRoot, "acme.one");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), """{ "id": "acme.one", "name": "Mine", "version": "1.0.0", "kind": "declarative", "declarative": {} }""");
        catalog.Reload();

        var ex = await Assert.ThrowsAsync<PluginInstallException>(() => installer.InstallAsync("acme.one"));

        Assert.Contains("by hand", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_DeletesTheFilesAndTheInstallRecord_ButSavedPluginDataStays()
    {
        var package = Package("acme.one", "1.0.0");
        var (installer, catalog) = Build(Listing("acme.one", "1.0.0", package), package);
        await installer.InstallAsync("acme.one");

        catalog.DeletePlugin("acme.one");

        Assert.Null(catalog.Get("acme.one"));
        Assert.Null(catalog.InstallRecords.Find("acme.one"));
        Assert.False(Directory.Exists(Path.Combine(catalog.PluginRoot, "acme.one")));
    }

    [Fact]
    public void RemovingTheOnlySourceOfSomething_SaysSoFirst_AndAnotherSourceMakesItSafe()
    {
        var catalog = NewCatalog([], out _);
        WritePlugin(catalog, "acme.art", "TV artwork");
        WritePlugin(catalog, "acme.art2", "TV artwork");
        catalog.Reload();
        _approvals.Approve(catalog.Get("acme.art")!);
        _approvals.Approve(catalog.Get("acme.art2")!);
        catalog.SetEnabled("acme.art", true);

        Assert.Equal(["TV artwork would have no source."], catalog.DescribeRemovalImpact("acme.art"));

        catalog.SetEnabled("acme.art2", true);
        Assert.Empty(catalog.DescribeRemovalImpact("acme.art"));
    }

    [Fact]
    public async Task AnUploadedJsonPlugin_IsAddedAsPrivate_SwitchedOff_WithNoInstallRecord()
    {
        var (installer, catalog) = Build(Listing("x.y", "1.0.0", Package("x.y", "1.0.0")), Package("x.y", "1.0.0"));

        var result = await installer.UploadAsync(Package("acme.up", "1.0.0", extra: "README.md"));

        Assert.Equal("acme.up", result.PluginId);
        var added = catalog.Get("acme.up")!;
        Assert.Equal(PluginTrustLevel.Private, added.Trust);
        Assert.False(added.Enabled);
        Assert.Null(catalog.InstallRecords.Find("acme.up"));
    }

    [Fact]
    public async Task AnUploadedPluginThatRunsCode_IsRefused_AndNothingIsLeftBehind()
    {
        var (installer, catalog) = Build(Listing("x.y", "1.0.0", Package("x.y", "1.0.0")), Package("x.y", "1.0.0"));

        var ex = await Assert.ThrowsAsync<PluginInstallException>(() => installer.UploadAsync(Package("acme.up", "1.0.0", kind: "official")));

        Assert.Contains("JSON", ex.Message, StringComparison.Ordinal);
        Assert.Null(catalog.Get("acme.up"));
        Assert.False(Directory.Exists(Path.Combine(catalog.PluginRoot, "acme.up")));
    }

    [Theory]
    [InlineData("lib.dll")]
    [InlineData("run.exe")]
    [InlineData("go.sh")]
    [InlineData("module.wasm")]
    [InlineData("nested/tool.py")]
    public async Task AnUploadedPackage_WithAnythingThatCouldRunCode_IsRefused(string extra)
    {
        var (installer, catalog) = Build(Listing("x.y", "1.0.0", Package("x.y", "1.0.0")), Package("x.y", "1.0.0"));

        var ex = await Assert.ThrowsAsync<PluginInstallException>(() => installer.UploadAsync(Package("acme.up", "1.0.0", extra: extra)));

        Assert.Contains("could run code", ex.Message, StringComparison.Ordinal);
        Assert.Null(catalog.Get("acme.up"));
    }

    [Fact]
    public async Task AnUploadedPackage_WithAnUnsafePath_AnEmptyFile_OrNotAZip_IsRefused()
    {
        var (installer, catalog) = Build(Listing("x.y", "1.0.0", Package("x.y", "1.0.0")), Package("x.y", "1.0.0"));

        await Assert.ThrowsAsync<PluginInstallException>(() => installer.UploadAsync(Package("acme.up", "1.0.0", rawEntry: "../escape.txt")));
        await Assert.ThrowsAsync<PluginInstallException>(() => installer.UploadAsync([]));
        await Assert.ThrowsAsync<PluginInstallException>(() => installer.UploadAsync(Encoding.UTF8.GetBytes("not a zip")));
        Assert.Null(catalog.Get("acme.up"));
    }

    [Fact]
    public async Task AnUploadedPlugin_WithAnIdThatIsAlreadyInstalled_IsRefused_AndTheFirstOneStays()
    {
        var (installer, catalog) = Build(Listing("x.y", "1.0.0", Package("x.y", "1.0.0")), Package("x.y", "1.0.0"));
        await installer.UploadAsync(Package("acme.up", "1.0.0"));

        var ex = await Assert.ThrowsAsync<PluginInstallException>(() => installer.UploadAsync(Package("acme.up", "2.0.0")));

        Assert.Contains("already installed", ex.Message, StringComparison.Ordinal);
        Assert.Equal("1.0.0", catalog.Get("acme.up")!.Manifest.Version);
    }

    [Fact]
    public async Task AnUploadedPlugin_WithAnUnsafeId_OrTooLarge_IsRefused()
    {
        var (installer, _) = Build(Listing("x.y", "1.0.0", Package("x.y", "1.0.0")), Package("x.y", "1.0.0"));

        await Assert.ThrowsAsync<PluginInstallException>(() => installer.UploadAsync(Package("con", "1.0.0")));
        await Assert.ThrowsAsync<PluginInstallException>(() => installer.UploadAsync(new byte[(64 * 1024 * 1024) + 1]));
    }

    [Theory]
    // The parser-differential attack: the upload check reads capitals and the last repeated name, the loader does not.
    [InlineData("""{ "id": "acme.up", "name": "x", "version": "1.0.0", "kind": "official", "entry_assembly": "payload.png", "entry_type": "Evil.P", "Kind": "declarative", "ENTRY_ASSEMBLY": null, "Entry_Type": null }""")]
    [InlineData("""{ "id": "acme.up", "name": "x", "version": "1.0.0", "kind": "official", "kind": "declarative" }""")]
    [InlineData("""{ "id": "acme.up", "name": "x", "version": "1.0.0", "kind": "declarative", "entry_assembly": null }""")]
    [InlineData("""{ "id": "acme.up", "name": "x", "version": "1.0.0", "kind": "declarative", "service": null }""")]
    [InlineData("""{ "id": "acme.up", "name": "x", "version": "1.0.0", "Kind": "declarative" }""")]
    public async Task AnUploadedManifest_ThatReadsDifferentlyToTheLoader_IsRefused_AndNeverLoaded(string manifestJson)
    {
        var (installer, catalog) = Build(Listing("x.y", "1.0.0", Package("x.y", "1.0.0")), Package("x.y", "1.0.0"));

        await Assert.ThrowsAsync<PluginInstallException>(() =>
            installer.UploadAsync(Package("acme.up", "1.0.0", manifestJson: manifestJson, binary: ("payload.png", [0x4D, 0x5A, 0x90, 0x00]))));

        Assert.Null(catalog.Get("acme.up"));
        Assert.False(Directory.Exists(Path.Combine(catalog.PluginRoot, "acme.up")));
    }

    [Theory]
    [InlineData("tool.png", new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 })]
    [InlineData("notes.txt", new byte[] { 0x4D, 0x5A, 0x00, 0x03 })]
    [InlineData("notes.md", new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x00 })]
    public async Task AnUploadedFile_ThatIsNotWhatItsNameSays_IsRefused(string name, byte[] data)
    {
        var (installer, catalog) = Build(Listing("x.y", "1.0.0", Package("x.y", "1.0.0")), Package("x.y", "1.0.0"));

        var ex = await Assert.ThrowsAsync<PluginInstallException>(() => installer.UploadAsync(Package("acme.up", "1.0.0", binary: (name, data))));

        Assert.Contains("could run code", ex.Message, StringComparison.Ordinal);
        Assert.Null(catalog.Get("acme.up"));
    }

    [Fact]
    public async Task AnUploadedPicture_WithTheRealPngSignature_IsAccepted()
    {
        var (installer, catalog) = Build(Listing("x.y", "1.0.0", Package("x.y", "1.0.0")), Package("x.y", "1.0.0"));

        await installer.UploadAsync(Package("acme.up", "1.0.0", binary: ("icon.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00])));

        Assert.NotNull(catalog.Get("acme.up"));
    }

    // ── Support ──────────────────────────────────────────────────────────────

    private (PluginPackageInstaller Installer, PluginCatalog Catalog) Build(string listing, byte[] package, IReadOnlyList<string>? trustedKeys = null)
    {
        _router.Listing = listing;
        _router.PackageBytes = package;
        var catalog = NewCatalog(trustedKeys, out var directory);
        var installer = new PluginPackageInstaller(
            catalog, directory, new RoutedFactory(_router), new PluginSignatureVerifier(trustedKeys ?? []),
            NullLogger<PluginPackageInstaller>.Instance);
        _directory = directory;
        directory.CheckNowAsync().GetAwaiter().GetResult();
        return (installer, catalog);
    }

    private PluginCatalog NewCatalog(IReadOnlyList<string>? trustedKeys, out PluginDirectoryService directory)
    {
        var catalog = new PluginCatalog([], _settings, _loader, _approvals, NullLogger<PluginCatalog>.Instance, "1.0.0", "linux-x64");
        directory = new PluginDirectoryService(
            new RoutedFactory(_router), _loader, new TuvimaDataPaths(Path.Combine(_root, "data")),
            NullLogger<PluginDirectoryService>.Instance, TimeProvider.System, null, catalog);
        return catalog;
    }

    private static void WritePlugin(PluginCatalog catalog, string id, string capabilityName)
    {
        var folder = Path.Combine(catalog.PluginRoot, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            { "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "kind": "declarative", "permissions": ["ai.infer"], "ai_permissions": [{ "role": "text_fast" }],
              "declarative": { "ai_steps": [{ "key": "s", "name": "S", "role": "text_fast", "prompt": "p" }] },
              "capabilities": [ { "kind": "artwork-source", "name": "{{capabilityName}}" } ] }
            """);
    }

    // Every test plugin is a JSON-only AI plugin (the smallest valid kind), so it always holds the Local AI permission.
    private static string[] WithAi(string[]? permissions) => ["ai.infer", .. permissions ?? []];

    private string PublicKey() => Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo());

    private string Sign(byte[] package) =>
        Convert.ToBase64String(_signingKey.SignData(package, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static byte[] Package(string id, string version, string kind = "declarative", string[]? permissions = null, string? extra = null, string? rawEntry = null, string? manifestJson = null, (string Name, byte[] Data)? binary = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = zip.CreateEntry("plugin.json");
            using (var writer = new StreamWriter(manifest.Open()))
            {
                var perms = string.Join(", ", WithAi(permissions).Select(permission => $"\"{permission}\""));
                if (manifestJson is not null)
                {
                    writer.Write(manifestJson);
                }
                else
                writer.Write($$"""{ "id": "{{id}}", "name": "One", "version": "{{version}}", "kind": "{{kind}}", "permissions": [ {{perms}} ], "ai_permissions": [{ "role": "text_fast" }], "declarative": { "ai_steps": [{ "key": "s", "name": "S", "role": "text_fast", "prompt": "p" }] } }""");
            }

            if (extra is not null)
            {
                using var writer = new StreamWriter(zip.CreateEntry(extra).Open());
                writer.Write("extra");
            }

            if (rawEntry is not null)
            {
                using var writer = new StreamWriter(zip.CreateEntry(rawEntry).Open());
                writer.Write("bad");
            }

            if (binary is { } file)
            {
                using var entry = zip.CreateEntry(file.Name).Open();
                entry.Write(file.Data);
            }
        }

        return stream.ToArray();
    }

    private static string Listing(
        string id, string version, byte[] package, string trust = "community", string kind = "declarative",
        string[]? permissions = null, string? signature = null, string packageUrl = PackageUrl)
    {
        var hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var perms = string.Join(", ", WithAi(permissions).Select(permission => $"\"{permission}\""));
        var signatureField = signature is null ? "" : $$""", "signature": "{{signature}}" """;
        return $$"""
            { "schema_version": "2.0", "plugins": [ { "id": "{{id}}", "name": "One", "version": "{{version}}", "kind": "{{kind}}",
              "trust_level": "{{trust}}", "permissions": [ {{perms}} ], "package_url": "{{packageUrl}}", "sha256": "{{hash}}"{{signatureField}} } ] }
            """;
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Router
    {
        public string Listing { get; set; } = "";

        public byte[] PackageBytes { get; set; } = [];

        public string? RedirectPackageTo { get; set; }

        public List<string> Requested { get; } = [];
    }

    private sealed class RoutedFactory(Router router) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RouterHandler(router), disposeHandler: true);
    }

    private sealed class RouterHandler(Router router) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var address = request.RequestUri!.ToString();
            router.Requested.Add(address);
            if (address == ListUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(router.Listing, Encoding.UTF8, "application/json") });
            }

            var final = router.RedirectPackageTo ?? address;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(router.PackageBytes),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, final),
            });
        }
    }

    /// <summary>Approves exactly the statements it was told to, so the update-asks-again rule can be seen working.</summary>
    private sealed class RecordingApprovals : IPluginApprovalCheck
    {
        private readonly HashSet<string> _approved = new(StringComparer.Ordinal);

        public void Approve(PluginRegistration registration) =>
            _approved.Add(Key(registration.Manifest, registration.Trust));

        public bool IsCovered(PluginManifest manifest, PluginTrustLevel trust) =>
            _approved.Contains(Key(manifest, trust));

        private static string Key(PluginManifest manifest, PluginTrustLevel trust) =>
            manifest.Id + "|" + ApprovalStatementBuilder.Hash(ApprovalStatementBuilder.ForPlugin(manifest, trust));
    }
}
