using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

// A Vault library is never an account grant (Videos D55b, Vault sections leak 9, Library pages D10): a hidden-libraries list that names one is
// refused exactly like an id that names no library, whether the Vault is known from the Vault's own list or from the library's access value.
public sealed class AccountVaultLibraryGrantTests
{
    [Fact]
    public async Task AHiddenLibrariesListNamingAVaultLibraryIsRefusedLikeAnIdThatNamesNoLibrary()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-vault-grant-{Guid.NewGuid():N}.db");
        var configPath = Path.Combine(Path.GetTempPath(), $"tuvima-vault-grant-{Guid.NewGuid():N}");
        var ordinary = Guid.NewGuid();
        var vaultByList = Guid.NewGuid();
        var vaultByAccess = Guid.NewGuid();
        try
        {
            using var database = new DatabaseConnection(databasePath);
            using var configuration = new ConfigurationDirectoryLoader(configPath);
            database.InitializeSchema();
            Directory.CreateDirectory(configPath);
            File.WriteAllText(Path.Combine(configPath, "libraries.json"), System.Text.Json.JsonSerializer.Serialize(new LibrariesConfiguration
            {
                StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Path.GetTempPath(), AllowWrite = true }],
                Libraries = [Library(ordinary, "Movies", LibraryAccessLevels.Standard), Library(vaultByList, "Private TV", LibraryAccessLevels.Standard, "tv", "TV"),
                    Library(vaultByAccess, "Private Music", LibraryAccessLevels.Vault)],
            }));
            var accounts = new AccountRepository(database);
            var identities = new IdentityRepository(database);
            var profiles = new ProfileRepository(database);
            var firstParty = new FirstPartyIdentityService(
                identities, accounts, profiles, new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
                TimeProvider.System, new ConfigurationAuthenticationPolicyProvider(configuration));
            var mutations = AccountSetupKit.Attach(new AccountAccessMutationService(
                accounts, new ProfileAccessRepository(database), identities, profiles, configuration, new AccountEmailRequiredTests.AllowAdministratorDecisions(),
                new AccountEmailRequiredTests.AllowEvaluator(), new AccountEmailRequiredTests.NoOpInvalidation(), new AccountEmailRequiredTests.NoOpAudit(), firstParty,
                new NoProfilePhotos(), TimeProvider.System, new AlwaysUsableAdministrators(), new AuthenticationPolicyMutationGate(),
                new FixedVaultLibraries(vaultByList.ToString("D"))), accounts, new ProfileAccessRepository(database), firstParty);
            var actor = new RequestAuthority(
                PrincipalKind.Human, true, AccountId: Guid.NewGuid(), ActiveProfileId: Guid.NewGuid(), SessionId: Guid.NewGuid(),
                AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileIsServerAdministrator: true);

            var account = await mutations.CreateAsync(actor, new CreateAccountAccessCommand(
                "member@example.com", IsAdministrator: false, ProfileId: null, NewProfile: new NewAccountProfileCommand("Member", "#7C4DFF"),
                Features: new HashSet<AccountFeatureId>(), HiddenLibraries: new HashSet<Guid>()));

            Task Hide(Guid library) => mutations.ReplaceProfileAccessAsync(
                actor, account.ProfileId, new HashSet<AccountFeatureId>(), new HashSet<Guid> { library });

            var byList = await Assert.ThrowsAsync<ArgumentException>(() => Hide(vaultByList));
            var byAccess = await Assert.ThrowsAsync<ArgumentException>(() => Hide(vaultByAccess));
            var unknown = await Assert.ThrowsAsync<ArgumentException>(() => Hide(Guid.NewGuid()));
            Assert.StartsWith("Unknown catalogued library", byList.Message, StringComparison.Ordinal);
            Assert.StartsWith("Unknown catalogued library", byAccess.Message, StringComparison.Ordinal);
            Assert.StartsWith("Unknown catalogued library", unknown.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestTemp.ReleaseSqlitePool(databasePath);
            try { File.Delete(databasePath); } catch (IOException) { }
            try { Directory.Delete(configPath, recursive: true); } catch (IOException) { }
        }
    }

    private static LibraryFolderConfig Library(Guid id, string name, string access, string kindKey = "movies", string mediaType = "Movies") => new()
    {
        Id = id.ToString("D"),
        Name = name,
        Category = mediaType,
        Kind = LibraryKinds.Catalogued,
        KindKey = kindKey,
        Area = "watch",
        MediaTypes = [mediaType],
        Access = access,
    };
}
