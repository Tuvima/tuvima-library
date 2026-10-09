using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class AuthenticationPolicyTests
{
    [Fact]
    public void SelfServiceSessions_ExcludeRevokedAndExpiredRows()
    {
        var now = DateTimeOffset.Parse("2026-09-13T12:00:00Z");
        var active = Session(now.AddHours(1));
        var expired = Session(now.AddSeconds(-1));
        var revoked = Session(now.AddHours(1));
        revoked.RevokedAt = now.AddMinutes(-1);

        var result = AuthenticationEndpoints.ToActiveSessionResponses([active, expired, revoked], now);

        Assert.Equal(active.Id, Assert.Single(result).Id);
        Assert.All(result, session => Assert.Null(session.RevokedAt));
    }

    [Fact]
    public void PasskeyAvailability_RequiresPolicyCanonicalOriginAndAllowedClient()
    {
        var policy = new AuthSettings
        {
            Mode = "Optional",
            PasskeySignInEnabled = true,
            AllowRemoteSignIn = false,
            PasswordReset = new PasswordResetDeliverySettings { PublicBaseUrl = "https://library.example" },
        };

        Assert.True(AuthenticationEndpoints.IsPasskeyAvailable(policy, true, false));
        Assert.False(AuthenticationEndpoints.IsPasskeyAvailable(policy, false, true));
        policy.AllowRemoteSignIn = true;
        policy.RequireHttpsRemote = true;
        Assert.False(AuthenticationEndpoints.IsPasskeyAvailable(policy, false, false));
        Assert.True(AuthenticationEndpoints.IsPasskeyAvailable(policy, false, true));
        policy.PasswordReset.PublicBaseUrl = string.Empty;
        Assert.False(AuthenticationEndpoints.IsPasskeyAvailable(policy, true, true));
    }

    [Fact]
    public void ClientPolicy_DeniesDisabledAndUntrustedLocalOnlyMethods()
    {
        var policy = new AuthSettings
        {
            AllowRemoteSignIn = false,
            RequireHttpsRemote = true,
        };

        Assert.False(AuthenticationEndpoints.AllowsClient(policy, true, true, methodEnabled: false));
        Assert.False(AuthenticationEndpoints.AllowsClient(policy, false, true, methodEnabled: true));
        Assert.True(AuthenticationEndpoints.AllowsClient(policy, true, false, methodEnabled: true));
    }

    [Fact]
    public void ClientPolicy_RequiresHttpsForRemoteSignInWhenConfigured()
    {
        var policy = new AuthSettings
        {
            AllowRemoteSignIn = true,
            RequireHttpsRemote = true,
        };

        Assert.False(AuthenticationEndpoints.AllowsClient(policy, false, false, methodEnabled: true));
        Assert.True(AuthenticationEndpoints.AllowsClient(policy, false, true, methodEnabled: true));
    }

    [Theory]
    [InlineData("Local", false)]
    [InlineData("DisabledLocalOnly", false)]
    [InlineData("Optional", true)]
    [InlineData("Required", true)]
    public void ExternalSignIn_RequiresAnExternalCapableMode(string mode, bool expected)
    {
        var policy = new AuthSettings { Mode = mode, ExternalSignInEnabled = true };

        Assert.Equal(expected, AuthenticationEndpoints.IsExternalSignInEnabled(policy));
    }

    [Fact]
    public void Readiness_SeparatesConfiguredFromReadyAndNeverSerializesSecrets()
    {
        var auth = new AuthSettings
        {
            ExternalSignInEnabled = true,
            PasskeySignInEnabled = true,
            PasswordReset = new PasswordResetDeliverySettings
            {
                Mode = "Smtp",
                PublicBaseUrl = "http://library.example",
                SmtpHost = "smtp.example",
                SmtpPort = 587,
                FromAddress = "library@example.com",
                Password = "smtp-secret",
            },
            ExternalProviders =
            [
                new ExternalAuthProviderSettings
                {
                    Id = "github",
                    Kind = ExternalAuthProviderKinds.OAuth,
                    Enabled = true,
                    Issuer = "https://github.com",
                    ClientId = "client",
                    ClientSecret = "provider-secret",
                    AuthorizationEndpoint = "https://github.com/login/oauth/authorize",
                    TokenEndpoint = "https://github.com/login/oauth/access_token",
                    UserInformationEndpoint = "https://api.github.com/user",
                },
            ],
        };

        var dto = SettingsEndpoints.ToAuthSettingsDto(auth, restartRequired: true);

        Assert.True(dto.PasswordReset.Configured);
        Assert.False(dto.PasswordReset.Ready);
        Assert.False(dto.PasskeyReady);
        Assert.True(dto.ExternalProviders.Single().Configured);
        Assert.False(dto.ExternalProviders.Single().Ready);
        Assert.True(dto.RestartRequired);
        var json = JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("smtp-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readiness_RequiresSecureCanonicalOrigin()
    {
        var auth = new AuthSettings
        {
            PasskeySignInEnabled = true,
            PasswordReset = new PasswordResetDeliverySettings
            {
                PublicBaseUrl = "https://library.example",
            },
        };

        var dto = SettingsEndpoints.ToAuthSettingsDto(auth, restartRequired: false);

        Assert.True(dto.CanonicalOriginReady);
        Assert.True(dto.PasskeyReady);
        Assert.False(dto.RestartRequired);
    }

    [Fact]
    public void ExternalIdentityTransaction_IsPurposeAndSessionBoundAndSingleUse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-09T12:00:00Z"));
        var service = new ExternalIdentityTransactionService(clock);
        var accountId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var transaction = service.Begin(new BeginExternalIdentityTransactionRequest
        {
            Purpose = ExternalIdentityTransactionPurposes.Link,
            Provider = "github",
            Issuer = "https://github.com",
            Subject = "verified-subject",
        }, accountId, sessionId);

        Assert.Null(service.Consume(
            transaction.Ticket,
            ExternalIdentityTransactionPurposes.Link,
            accountId,
            Guid.NewGuid()));
        Assert.Null(service.Consume(
            transaction.Ticket,
            ExternalIdentityTransactionPurposes.Link,
            accountId,
            sessionId));

        var valid = service.Begin(new BeginExternalIdentityTransactionRequest
        {
            Purpose = ExternalIdentityTransactionPurposes.SignIn,
            Provider = "github",
            Issuer = "https://github.com",
            Subject = "verified-subject",
        }, null, null);
        var consumed = service.Consume(valid.Ticket, ExternalIdentityTransactionPurposes.SignIn);
        Assert.NotNull(consumed);
        Assert.Equal("verified-subject", consumed.Subject);
        Assert.Null(service.Consume(valid.Ticket, ExternalIdentityTransactionPurposes.SignIn));
    }

    [Fact]
    public void ExternalIdentityTransaction_RejectsExpiredAndUnboundLinkTickets()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-09T12:00:00Z"));
        var service = new ExternalIdentityTransactionService(clock);
        Assert.Throws<UnauthorizedAccessException>(() => service.Begin(new BeginExternalIdentityTransactionRequest
        {
            Purpose = ExternalIdentityTransactionPurposes.Link,
            Provider = "google",
            Issuer = "https://accounts.google.com",
            Subject = "verified-subject",
        }, null, null));

        var transaction = service.Begin(new BeginExternalIdentityTransactionRequest
        {
            Purpose = ExternalIdentityTransactionPurposes.SignIn,
            Provider = "google",
            Issuer = "https://accounts.google.com",
            Subject = "verified-subject",
        }, null, null);
        clock.Advance(TimeSpan.FromMinutes(3));

        Assert.Null(service.Consume(transaction.Ticket, ExternalIdentityTransactionPurposes.SignIn));
    }

    [Fact]
    public void ExternalProviderPolicy_RequiresEnabledExactProviderAndIssuer()
    {
        var policy = new AuthSettings
        {
            PasswordReset = new PasswordResetDeliverySettings { PublicBaseUrl = "https://library.example" },
            ExternalProviders =
            [
                new ExternalAuthProviderSettings
                {
                    Id = "google",
                    Kind = ExternalAuthProviderKinds.OpenIdConnect,
                    Enabled = true,
                    Authority = "https://accounts.google.com/",
                    ClientId = "google-client",
                    Scopes = ["openid"],
                },
                new ExternalAuthProviderSettings
                {
                    Id = "github",
                    Kind = ExternalAuthProviderKinds.OAuth,
                    Enabled = false,
                    Issuer = "https://github.com",
                },
            ],
        };

        Assert.True(AuthenticationEndpoints.IsConfiguredProvider(
            policy, "google", "https://accounts.google.com"));
        Assert.False(AuthenticationEndpoints.IsConfiguredProvider(
            policy, "google", "https://attacker.example"));
        Assert.False(AuthenticationEndpoints.IsConfiguredProvider(
            policy, "github", "https://github.com"));
    }

    [Fact]
    public void IsConfiguredProvider_UsesSharedIssuerMatcher_ExactForConfiguredIssuerAndSlashTolerantForAuthority()
    {
        var policy = new AuthSettings
        {
            Mode = "Optional",
            ExternalSignInEnabled = true,
            PasswordReset = new PasswordResetDeliverySettings { PublicBaseUrl = "https://library.example" },
            ExternalProviders =
            [
                new ExternalAuthProviderSettings
                {
                    Id = "oidc", Kind = ExternalAuthProviderKinds.OpenIdConnect, Enabled = true, ClientId = "c",
                    Authority = "https://idp.example/realm", Scopes = ["openid"],
                },
                new ExternalAuthProviderSettings
                {
                    Id = "pinned", Kind = ExternalAuthProviderKinds.OpenIdConnect, Enabled = true, ClientId = "c",
                    Authority = "https://idp.example/realm", Issuer = "https://idp.example/realm", Scopes = ["openid"],
                },
            ],
        };

        Assert.True(AuthenticationEndpoints.IsConfiguredProvider(policy, "oidc", "https://idp.example/realm/"));
        Assert.True(AuthenticationEndpoints.IsConfiguredProvider(policy, "pinned", "https://idp.example/realm"));
        Assert.False(AuthenticationEndpoints.IsConfiguredProvider(policy, "pinned", "https://idp.example/realm/"));
    }

    [Theory]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("consumers")]
    public void ProviderUpdateValidation_RejectsMicrosoftMultiTenantAuthority(string tenant)
    {
        var validate = typeof(SettingsEndpoints).GetMethod(
            "ValidateExternalProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        string? Run(string authority) => (string?)validate.Invoke(null, ["microsoft",
            new MediaEngine.Contracts.Settings.UpdateExternalAuthProviderRequest
            {
                Kind = ExternalAuthProviderKinds.OpenIdConnect,
                DisplayName = "Microsoft",
                ClientId = "client",
                Authority = authority,
                Scopes = ["openid"],
            }]);

        var error = Run($"https://login.microsoftonline.com/{tenant}/v2.0");
        var ok = Run("https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0");

        Assert.Contains("tenant ID", error, StringComparison.Ordinal);
        Assert.Null(ok);
    }

    [Fact]
    public async Task ProviderConfiguration_PersistsSecretOnlyInPrivateOverlayAndReportsConfiguredState()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tuvima-auth-provider-{Guid.NewGuid():N}");
        try
        {
            using var configuration = new ConfigurationDirectoryLoader(directory);
            var core = configuration.LoadCore();
            core.Auth.PasswordReset.PublicBaseUrl = "https://library.example";
            core.Auth.Mode = "Required";
            core.Auth.ExternalProviders =
            [
                new ExternalAuthProviderSettings
                {
                    Id = "github",
                    Kind = ExternalAuthProviderKinds.OAuth,
                    Enabled = true,
                    DisplayName = "GitHub",
                    Issuer = "https://github.com",
                    ClientId = "client-id",
                    AuthorizationEndpoint = "https://github.com/login/oauth/authorize",
                    TokenEndpoint = "https://github.com/login/oauth/access_token",
                    UserInformationEndpoint = "https://api.github.com/user",
                },
            ];
            configuration.SaveCore(core);
            var service = new AuthenticationProviderConfigurationService(configuration);

            await service.UpdateSecretAsync("github", "private-secret", clear: false, CancellationToken.None);
            var loaded = service.LoadWithSecrets();
            var dto = SettingsEndpoints.ToAuthSettingsDto(loaded, restartRequired: true);

            Assert.Equal("private-secret", loaded.ExternalProviders.Single().ClientSecret);
            Assert.True(dto.ExternalProviders.Single().Configured);
            Assert.True(dto.ExternalProviders.Single().Ready);
            Assert.DoesNotContain("private-secret", File.ReadAllText(Path.Combine(directory, "core.json")), StringComparison.Ordinal);
            Assert.Contains("private-secret", File.ReadAllText(Path.Combine(directory, ".secrets", "auth-providers.json")), StringComparison.Ordinal);
            Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(dto), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProviderConfiguration_DoesNotOverwriteMalformedSecretOverlay()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tuvima-auth-provider-{Guid.NewGuid():N}");
        try
        {
            using var configuration = new ConfigurationDirectoryLoader(directory);
            var path = Path.Combine(directory, ".secrets", "auth-providers.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const string malformed = "{ this is not valid json";
            await File.WriteAllTextAsync(path, malformed);
            var service = new AuthenticationProviderConfigurationService(configuration);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.UpdateSecretAsync("github", "replacement", clear: false, CancellationToken.None));

            Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(malformed, await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AdministratorSignInSafety_RequiresAdminGrantAndReadyLinkedProvider()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-auth-safety-{Guid.NewGuid():N}.db");
        try
        {
            using (var database = new DatabaseConnection(databasePath))
            {
                database.InitializeSchema();
                var accounts = new AccountRepository(database);
                var identities = new IdentityRepository(database);
                var external = new AccountExternalLoginService(new AccountExternalLoginRepository(database), accounts);
                var now = DateTimeOffset.Parse("2026-09-09T12:00:00Z");
                var passwordAdmin = await CreateAdministratorAsync(accounts, now, adminGrant: true);
                await identities.UpsertAccountCredentialAsync(new AccountCredential
                {
                    Id = Guid.NewGuid(),
                    AccountId = passwordAdmin.Id,
                    Kind = AccountCredentialKind.Password,
                    SecretHash = "test",
                    SecurityStamp = "stamp",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                var ungrantedAdmin = await CreateAdministratorAsync(accounts, now, adminGrant: false);
                await external.LinkAsync(ungrantedAdmin.Id, "github", "https://github.com", "ungranted", null, null);

                var policy = ReadyExternalPolicy(providerEnabled: true);
                Assert.False(await SettingsEndpoints.HasUsableAdministratorSignInAsync(
                    policy, accounts, identities, external, null!, CancellationToken.None));

                await external.LinkAsync(passwordAdmin.Id, "github", "https://github.com", "granted", null, null);
                policy.ExternalProviders.Single().Enabled = false;
                Assert.False(await SettingsEndpoints.HasUsableAdministratorSignInAsync(
                    policy, accounts, identities, external, null!, CancellationToken.None));

                policy.ExternalProviders.Single().Enabled = true;
                Assert.True(await SettingsEndpoints.HasUsableAdministratorSignInAsync(
                    policy, accounts, identities, external, null!, CancellationToken.None));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task SignInMethodRemoval_ExcludesTargetAndIgnoresPolicyDisabledFallbacks()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-auth-removal-{Guid.NewGuid():N}.db");
        try
        {
            using (var database = new DatabaseConnection(databasePath))
            {
                database.InitializeSchema();
                var accounts = new AccountRepository(database);
                var identities = new IdentityRepository(database);
                var external = new AccountExternalLoginService(new AccountExternalLoginRepository(database), accounts);
                var now = DateTimeOffset.Parse("2026-09-09T12:00:00Z");
                var account = await CreateAdministratorAsync(accounts, now, adminGrant: true);
                var login = await external.LinkAsync(account.Id, "github", "https://github.com", "subject", null, null);
                await identities.UpsertAccountCredentialAsync(new AccountCredential
                {
                    Id = Guid.NewGuid(),
                    AccountId = account.Id,
                    Kind = AccountCredentialKind.Password,
                    SecretHash = "test",
                    SecurityStamp = "stamp",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                var policy = ReadyExternalPolicy(providerEnabled: true);

                Assert.True(await AuthenticationEndpoints.HasUsableAccountSignInAsync(
                    policy, account.Id, accounts, identities, external, null!, ct: CancellationToken.None));
                Assert.False(await AuthenticationEndpoints.HasUsableAccountSignInAsync(
                    policy, account.Id, accounts, identities, external, null!,
                    excludedExternalLoginId: login.Id, ct: CancellationToken.None));

                policy.PasswordSignInEnabled = true;
                Assert.True(await AuthenticationEndpoints.HasUsableAccountSignInAsync(
                    policy, account.Id, accounts, identities, external, null!,
                    excludedExternalLoginId: login.Id, ct: CancellationToken.None));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private static async Task<Account> CreateAdministratorAsync(
        AccountRepository accounts,
        DateTimeOffset now,
        bool adminGrant)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@example.com",
            IsEnabled = true,
            IsAdministrator = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        account.NormalizedEmail = account.Email.ToUpperInvariant();
        var profileId = Guid.NewGuid();
        await accounts.CreateAccountAsync(account, new AccountProfileGrant
        {
            AccountId = account.Id,
            ProfileId = profileId,
            IsDefault = true,
            IsEnabled = true,
            AdminEnabled = adminGrant,
            GrantedAt = now,
        }, new HashSet<AccountFeatureId>(), new HashSet<Guid>(), CancellationToken.None, new Profile
        {
            Id = profileId,
            DisplayName = "Administrator",
            CreatedAt = now,
        });
        return account;
    }

    private static AuthSettings ReadyExternalPolicy(bool providerEnabled) => new()
    {
        Mode = "Required",
        PasswordSignInEnabled = false,
        PasskeySignInEnabled = false,
        ExternalSignInEnabled = true,
        PasswordReset = new PasswordResetDeliverySettings { PublicBaseUrl = "https://library.example" },
        ExternalProviders =
        [
            new ExternalAuthProviderSettings
            {
                Id = "github", Kind = ExternalAuthProviderKinds.OAuth, Enabled = providerEnabled,
                Issuer = "https://github.com", ClientId = "client", ClientSecret = "secret",
                AuthorizationEndpoint = "https://github.com/login/oauth/authorize",
                TokenEndpoint = "https://github.com/login/oauth/access_token",
                UserInformationEndpoint = "https://api.github.com/user",
            },
        ],
    };

    private static AuthSession Session(DateTimeOffset expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        ActiveProfileId = Guid.NewGuid(),
        DeviceId = "browser",
        DeviceName = "Browser",
        Client = "Dashboard",
        AuthenticationMethod = "Password",
        SecurityStamp = "stamp",
        CreatedAt = expiresAt.AddHours(-2),
        LastSeenAt = expiresAt.AddHours(-1),
        ExpiresAt = expiresAt,
    };

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
