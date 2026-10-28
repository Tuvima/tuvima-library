using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The central check for plugin and source actions (Plugin architecture spec PA2): the item's audience (D1), the Vault rule (D4) and the
/// administrator's library limit (D6), asked in that order, with the Vault decided by where the file sits and never by the caller.
/// </summary>
public sealed class PluginAccessGateTests : IDisposable
{
    private const string Movies = "11111111-1111-4111-8111-111111111111";
    private const string Music = "22222222-2222-4222-8222-222222222222";
    private const string VaultSection = "33333333-3333-4333-8333-333333333333";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-access", Guid.NewGuid().ToString("N"));
    private readonly ApprovalTestKit _kit = new();
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginCatalog _catalog;
    private readonly PluginLibraryLimitRepository _limitRepository;

    public PluginAccessGateTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(config, "providers"));
        File.WriteAllText(
            Path.Combine(config, "libraries.json"),
            $$"""
            {
              "storage_locations": [{ "id": "media", "label": "Library data", "path": "{{libraryRoot.Replace('\\', '/')}}", "allow_write": true }],
              "view_storage": { "storage_location_id": "media", "relative_root": "View" },
              "libraries": [
                { "id": "{{Movies}}", "name": "Movies", "category": "Movies" },
                { "id": "{{Music}}", "name": "Music", "category": "Music" },
                { "id": "{{VaultSection}}", "name": "Private", "category": "Movies" }
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(config, "providers", "adult_source.json"),
            """{ "name": "adult_source", "version": "1.0", "enabled": true, "adult_only": true, "endpoints": { "api": "https://adult.example" }, "declaration": { "audience": "administrators" } }""");
        File.WriteAllText(
            Path.Combine(config, "providers", "open_source.json"),
            """{ "name": "open_source", "version": "1.0", "enabled": true, "endpoints": { "api": "https://open.example" }, "declaration": { "audience": "everyone" } }""");
        _loader = new ConfigurationDirectoryLoader(config);
        _catalog = new PluginCatalog(
            [Plugin("test.local-everyone", PluginAudience.Everyone, network: false), Plugin("test.online-everyone", PluginAudience.Everyone, network: true),
             Plugin("test.adults", PluginAudience.Adults, network: false), Plugin("test.undeclared", null, network: false),
             Plugin("test.never-vault", PluginAudience.Everyone, network: false, vault: "never")],
            new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _limitRepository = new PluginLibraryLimitRepository(_kit.Database);
        foreach (var id in new[] { "test.local-everyone", "test.online-everyone", "test.adults", "test.undeclared", "test.never-vault" })
        {
            _catalog.SetEnabled(id, true);
        }
    }

    public void Dispose()
    {
        _kit.Dispose();
        _loader.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    [Theory]
    [InlineData(PluginAudience.Administrators, PluginAccessOutcome.Allowed)]
    [InlineData(PluginAudience.Adults, PluginAccessOutcome.Allowed)]
    [InlineData(PluginAudience.Everyone, PluginAccessOutcome.Allowed)]
    public async Task AnItemForEveryone_ServesAnyone(PluginAudience requester, PluginAccessOutcome expected) =>
        Assert.Equal(expected, await Gate().CheckAsync(new("test.local-everyone", requester, Movies, null)));

    [Fact]
    public async Task AnItemForAdults_RefusesAChildProfileButServesAnAdultAndAnAdministrator()
    {
        var gate = Gate();

        Assert.Equal(PluginAccessOutcome.NotInAudience, await gate.CheckAsync(new("test.adults", PluginAudience.Everyone, Movies, null)));
        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("test.adults", PluginAudience.Adults, Movies, null)));
        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("test.adults", PluginAudience.Administrators, Movies, null)));
    }

    [Fact]
    public async Task AnItemThatStatesNothing_IsForAdministratorsOnly()
    {
        var gate = Gate();

        Assert.Equal(PluginAccessOutcome.NotInAudience, await gate.CheckAsync(new("test.undeclared", PluginAudience.Adults, Movies, null)));
        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("test.undeclared", PluginAudience.Administrators, Movies, null)));
    }

    [Fact]
    public async Task ASourceIsJudgedLikeAPlugin()
    {
        var gate = Gate();

        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("open_source", PluginAudience.Everyone, Movies, null)));
        Assert.Equal(PluginAccessOutcome.NotInAudience, await gate.CheckAsync(new("adult_source", PluginAudience.Adults, VaultSection, null)));
    }

    [Fact]
    public async Task TheLibraryAskingItself_HasNoAudienceToCheck()
    {
        Assert.Equal(PluginAccessOutcome.Allowed, await Gate().CheckAsync(new("test.undeclared", null, Movies, null)));
    }

    [Fact]
    public async Task AnUnknownItem_IsNeverAllowed()
    {
        Assert.Equal(PluginAccessOutcome.Unknown, await Gate().CheckAsync(new("no.such.thing", PluginAudience.Administrators, Movies, null)));
        Assert.Equal(PluginAccessOutcome.Unknown, await Gate().CheckAsync(new(" ", null, Movies, null)));
    }

    [Fact]
    public async Task OnAVaultItem_ALocalOnlyItemWorks_AnItemThatSendsDetailsOutDoesNot_WhileTheSectionLookupIsOff()
    {
        var gate = Gate(vaultIds: [VaultSection]);

        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("test.local-everyone", PluginAudience.Adults, VaultSection, null)));
        Assert.Equal(PluginAccessOutcome.VaultBlocked, await gate.CheckAsync(new("test.online-everyone", PluginAudience.Adults, VaultSection, null)));
        Assert.Equal(PluginAccessOutcome.VaultBlocked, await gate.CheckAsync(new("open_source", PluginAudience.Adults, VaultSection, null)));
    }

    [Fact]
    public async Task OnAVaultItem_AnItemThatSendsDetailsOutWorksOnlyWhenThatSectionsLookupIsOn()
    {
        var lookup = new SectionLookup(VaultSection);
        var gate = Gate(vaultIds: [VaultSection], lookup: lookup);

        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("test.online-everyone", PluginAudience.Adults, VaultSection, null)));
        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("open_source", PluginAudience.Adults, VaultSection, null)));
        Assert.Equal(["33333333-3333-4333-8333-333333333333", "33333333-3333-4333-8333-333333333333"], lookup.Asked);
    }

    [Fact]
    public async Task AnItemThatNeverWorksOnTheVault_IsBlockedEvenWhenNothingLeavesTheMachine()
    {
        var gate = Gate(vaultIds: [VaultSection], lookup: new SectionLookup(VaultSection));

        Assert.Equal(PluginAccessOutcome.VaultBlocked, await gate.CheckAsync(new("test.never-vault", PluginAudience.Administrators, VaultSection, null)));
        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("test.never-vault", PluginAudience.Administrators, Movies, null)));
    }

    [Fact]
    public async Task TheVaultsOwnSource_IsAllowedOnTheVault_AndNeverOnAnOrdinaryItem()
    {
        var gate = Gate(vaultIds: [VaultSection]);

        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("adult_source", PluginAudience.Administrators, VaultSection, null)));
        Assert.Equal(PluginAccessOutcome.VaultBlocked, await gate.CheckAsync(new("adult_source", PluginAudience.Administrators, Movies, null)));
    }

    [Fact]
    public async Task WhenTheVaultSectionIsNotKnown_AnItemThatSendsDetailsOutIsRefused()
    {
        // The item is in the Vault by id alone, so the section's own setting cannot be read; the answer is no.
        var gate = Gate(vaultIds: [VaultSection], lookup: new SectionLookup(VaultSection), entityInVault: true);

        Assert.Equal(PluginAccessOutcome.VaultBlocked, await gate.CheckAsync(new("test.online-everyone", PluginAudience.Adults, null, Guid.NewGuid())));
        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("test.local-everyone", PluginAudience.Adults, null, Guid.NewGuid())));
    }

    [Fact]
    public async Task ALibraryLimit_KeepsAnItemToItsLibraries()
    {
        var limits = Limits([]);
        await limits.SetAsync("test.local-everyone", [Movies]);
        var gate = Gate();

        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("test.local-everyone", PluginAudience.Adults, Movies, null)));
        Assert.Equal(PluginAccessOutcome.LibraryLimited, await gate.CheckAsync(new("test.local-everyone", PluginAudience.Adults, Music, null)));
        Assert.Equal(PluginAccessOutcome.Allowed, await gate.CheckAsync(new("test.online-everyone", PluginAudience.Adults, Music, null)));
    }

    [Fact]
    public async Task ALimitedItem_IsNeverUsedOnTheVault()
    {
        var limits = Limits([VaultSection]);
        await limits.SetAsync("test.local-everyone", [Movies]);
        var gate = Gate(vaultIds: [VaultSection]);

        Assert.Equal(PluginAccessOutcome.LibraryLimited, await gate.CheckAsync(new("test.local-everyone", PluginAudience.Adults, VaultSection, null)));
    }

    [Fact]
    public void WhoIsAsking_IsWorkedOutFromTheRequest_AndAnythingUnclearIsTreatedAsAChild()
    {
        var person = new RequestAuthority(
            PrincipalKind.Human, true, Guid.NewGuid(), Guid.NewGuid(),
            AccountEnabled: true, ActiveProfileInUserHousehold: true);

        Assert.Equal(PluginAudience.Administrators, PluginAccessGate.RequesterOf(person with { ActiveProfileIsServerAdministrator = true }));
        Assert.Equal(PluginAudience.Adults, PluginAccessGate.RequesterOf(person));
        Assert.Equal(PluginAudience.Everyone, PluginAccessGate.RequesterOf(person with { ActiveProfileIsRestricted = true }));
        Assert.Equal(PluginAudience.Everyone, PluginAccessGate.RequesterOf(person with { ActiveProfileIsRestricted = true, ActiveProfileIsServerAdministrator = true }));
        Assert.Equal(PluginAudience.Everyone, PluginAccessGate.RequesterOf(person with { AccountEnabled = false }));
        Assert.Equal(PluginAudience.Everyone, PluginAccessGate.RequesterOf(new RequestAuthority(PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true)));
        Assert.Equal(PluginAudience.Administrators, PluginAccessGate.RequesterOf(new RequestAuthority(PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true, ApplicationIsAdministrator: true)));
        Assert.Equal(PluginAudience.Everyone, PluginAccessGate.RequesterOf(new RequestAuthority(PrincipalKind.Anonymous, false)));

        // A profile that cannot be found is not trusted as an adult, and a paired app's administrator flag does not lift the person using it.
        Assert.Equal(PluginAudience.Everyone, PluginAccessGate.RequesterOf(person with { ActiveProfileInUserHousehold = false }));
        var pairedApp = person with { PrincipalKind = PrincipalKind.DelegatedUserClient, ApplicationId = Guid.NewGuid(), ApplicationEnabled = true, ApplicationIsAdministrator = true, ActiveProfileIsRestricted = true };
        Assert.Equal(PluginAudience.Everyone, PluginAccessGate.RequesterOf(pairedApp));
        Assert.Equal(PluginAudience.Adults, PluginAccessGate.RequesterOf(pairedApp with { ActiveProfileIsRestricted = false }));
        Assert.Equal(PluginAudience.Everyone, PluginAccessGate.RequesterOf(pairedApp with { ActiveProfileIsRestricted = false, ApplicationEnabled = false }));
        Assert.Equal(PluginAudience.Everyone, PluginAccessGate.RequesterOf(new RequestAuthority(PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: false, ApplicationIsAdministrator: true)));
    }

    private PluginLibraryLimitService Limits(string[] vaultIds) => new(_limitRepository, _loader, new FixedVaultLibraries(vaultIds));

    private PluginAccessGate Gate(string[]? vaultIds = null, IVaultSectionOnlineLookup? lookup = null, bool entityInVault = false)
    {
        var ids = vaultIds ?? [];
        IVaultLibraryRepository vault = entityInVault ? new EntityVault(ids) : new FixedVaultLibraries(ids);
        return new PluginAccessGate(_catalog, _loader, vault, lookup ?? new VaultSectionOnlineLookupOff(), Limits(ids));
    }

    private static ITuvimaPlugin Plugin(string id, PluginAudience? audience, bool network, string? vault = null) => new TestPlugin(id, audience, network, vault);

    private sealed class SectionLookup(string onFor) : IVaultSectionOnlineLookup
    {
        public List<string> Asked { get; } = [];

        public Task<bool> IsOnAsync(string libraryId, CancellationToken ct = default)
        {
            Asked.Add(libraryId);
            return Task.FromResult(string.Equals(libraryId, onFor, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>A Vault that says every id is a Vault item (a media asset or work found only by its id).</summary>
    private sealed class EntityVault(string[] ids) : IVaultLibraryRepository
    {
        private readonly FixedVaultLibraries _inner = new(ids);

        public Task<IReadOnlyList<VaultLibrary>> GetAllAsync(CancellationToken ct = default) => _inner.GetAllAsync(ct);

        public Task<IReadOnlySet<string>> GetIdsAsync(CancellationToken ct = default) => _inner.GetIdsAsync(ct);

        public Task<VaultLibrary?> GetAsync(string libraryId, CancellationToken ct = default) => _inner.GetAsync(libraryId, ct);

        public Task<bool> AnyAsync(CancellationToken ct = default) => _inner.AnyAsync(ct);

        public Task<bool> IsInVaultAsync(Guid id, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> MayPersonBeVaultOnlyAsync(Guid personId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> HoldsOrdinaryFilesAsync(string folderPath, CancellationToken ct = default) => _inner.HoldsOrdinaryFilesAsync(folderPath, ct);

        public Task AddAsync(VaultLibrary library, CancellationToken ct = default) => _inner.AddAsync(library, ct);

        public Task<bool> UpdateLookupAsync(string libraryId, string lookupSource, string? lookupServerId, DateTimeOffset now, CancellationToken ct = default) =>
            _inner.UpdateLookupAsync(libraryId, lookupSource, lookupServerId, now, ct);

        public Task<bool> RemoveAsync(string libraryId, CancellationToken ct = default) => _inner.RemoveAsync(libraryId, ct);

        public Task<bool> RemoveAsync(string libraryId, IReadOnlyCollection<string> sourcePaths, CancellationToken ct = default) => _inner.RemoveAsync(libraryId, sourcePaths, ct);
    }

    private sealed class TestPlugin(string id, PluginAudience? audience, bool network, string? vault) : ITuvimaPlugin
    {
        public string Kind => "access-test";

        public PluginManifest Manifest => new()
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            Permissions = network ? [PluginPermissionIds.NetworkHttp] : [],
            Network = network ? [new PluginNetworkDestination { Host = "details.example", Sends = "Sends titles and years" }] : [],
            Declaration = audience is null && vault is null
                ? null
                : new PluginDeclaration { Audience = audience is { } stated ? PluginDeclarationRules.AudienceWord(stated) : null, Vault = vault },
        };

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
    }
}
