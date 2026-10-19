using System.Collections.Frozen;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Plugins.ApplicationServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ApplicationEntity = MediaEngine.Domain.Entities.Application;
using DomainAuthorizationEvaluator = MediaEngine.Domain.Contracts.IAuthorizationEvaluator;

namespace MediaEngine.Api.Tests;

public sealed class ApplicationAdministrationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CredentialIssuance_ReturnsPlaintextOnceAndNeverExposesStoredVerifier()
    {
        var fixture = new Fixture();
        var application = await fixture.Service.CreateApplicationAsync(
            Fixture.HumanAdministrator,
            new CreateApplicationRequest(
                "Living room",
                "Television client",
                ApplicationTypeDto.UserClient,
                false,
                [ApplicationPermissionIds.LibraryRead.Value]));

        var first = await fixture.Service.IssueCredentialAsync(
            Fixture.HumanAdministrator,
            application.Id,
            new CreateApplicationCredentialRequest("Television", Now.AddDays(30)));
        var second = await fixture.Service.IssueCredentialAsync(
            Fixture.HumanAdministrator,
            application.Id,
            new CreateApplicationCredentialRequest("Tablet", null));

        Assert.StartsWith("tuvima_app_", first.PlaintextCredential, StringComparison.Ordinal);
        Assert.True(first.PlaintextCredential.Length >= 75);
        Assert.NotEqual(first.PlaintextCredential, second.PlaintextCredential);
        var stored = await fixture.Repository.GetApplicationCredentialsAsync(application.Id);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, credential =>
        {
            Assert.Equal("sha256", credential.HashScheme);
            Assert.Equal(64, credential.CredentialHash.Length);
            Assert.DoesNotContain("tuvima_app_", credential.CredentialHash, StringComparison.Ordinal);
        });

        var issueJson = JsonSerializer.Serialize(first);
        Assert.Contains("\"plaintext_credential\"", issueJson, StringComparison.Ordinal);
        Assert.DoesNotContain("credential_hash", issueJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(stored[0].CredentialHash, issueJson, StringComparison.Ordinal);

        var readJson = JsonSerializer.Serialize(
            await fixture.Service.GetApplicationAsync(Fixture.HumanAdministrator, application.Id));
        Assert.DoesNotContain("plaintext", readJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential_hash", readJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(first.PlaintextCredential, readJson, StringComparison.Ordinal);

        Assert.All(fixture.Audits.Events, audit =>
        {
            Assert.DoesNotContain(audit.Changes.Values, value =>
                value is not null &&
                (value.Contains(first.PlaintextCredential, StringComparison.Ordinal) ||
                 stored.Any(credential => value.Contains(credential.CredentialHash, StringComparison.Ordinal))));
        });
        Assert.Contains(application.Id, fixture.Invalidation.ApplicationIds);
    }

    [Fact]
    public async Task RegistryAndMutations_RejectUnavailableOrWrongTypePermissions()
    {
        var fixture = new Fixture();
        var definitions = await fixture.Service.GetPermissionDefinitionsAsync(Fixture.HumanAdministrator);
        var unavailable = Assert.Single(
            definitions,
            value => value.Id == ApplicationPermissionIds.EventsSubscribe.Value);
        Assert.False(unavailable.IsAvailable);
        Assert.NotNull(unavailable.UnavailableReason);

        var unavailableError = await Assert.ThrowsAsync<ApplicationAdministrationException>(() =>
            fixture.Service.CreateApplicationAsync(
                Fixture.HumanAdministrator,
                new CreateApplicationRequest(
                    "Events",
                    null,
                    ApplicationTypeDto.Automation,
                    false,
                    [ApplicationPermissionIds.EventsSubscribe.Value])));
        Assert.Equal(ApplicationAdministrationError.Validation, unavailableError.Error);

        var wrongTypeError = await Assert.ThrowsAsync<ApplicationAdministrationException>(() =>
            fixture.Service.CreateApplicationAsync(
                Fixture.HumanAdministrator,
                new CreateApplicationRequest(
                    "Metadata client",
                    null,
                    ApplicationTypeDto.UserClient,
                    false,
                    [ApplicationPermissionIds.MetadataWrite.Value])));
        Assert.Equal(ApplicationAdministrationError.Validation, wrongTypeError.Error);

        var presets = await fixture.Service.GetPermissionPresetsAsync(Fixture.HumanAdministrator);
        Assert.DoesNotContain(
            presets.SelectMany(value => value.PermissionIds),
            value => value == ApplicationPermissionIds.EventsSubscribe.Value);
    }

    [Fact]
    public async Task PermissionDiscovery_ProjectsLivePluginAvailability()
    {
        var availability = new StubPluginAvailability(false, "Fandom Lore is disabled.");
        var fixture = new Fixture(pluginAvailability: availability);

        var definitions = await fixture.Service.GetPermissionDefinitionsAsync(Fixture.HumanAdministrator);
        var plugin = Assert.Single(definitions, value => value.Id == TestPermissionRegistry.PluginPermissionId.Value);

        Assert.False(plugin.IsAvailable);
        Assert.Equal("Fandom Lore is disabled.", plugin.UnavailableReason);
        Assert.Equal("tuvima.fandom-lore", plugin.PluginId);
    }

    [Theory]
    [InlineData(PrincipalKind.Human, AuthorizationDenialReason.DisabledPrincipal)]
    [InlineData(PrincipalKind.DelegatedUserClient, AuthorizationDenialReason.DisabledPrincipal)]
    [InlineData(PrincipalKind.ServiceApplication, AuthorizationDenialReason.PermissionUnavailable)]
    public async Task Administration_DeniesInvalidOrUnavailableAuthority(
        PrincipalKind principalKind,
        AuthorizationDenialReason denialReason)
    {
        var fixture = new Fixture(
            applicationDecision: AuthorizationDecision.Deny(denialReason),
            administratorDecision: AuthorizationDecision.Deny(denialReason));
        var authority = principalKind switch
        {
            PrincipalKind.Human => Fixture.HumanAdministrator with { AccountEnabled = false },
            PrincipalKind.DelegatedUserClient => Fixture.DelegatedAdministrator with { GrantEnabled = false },
            PrincipalKind.ServiceApplication => Fixture.AdministratorApplication,
            _ => throw new ArgumentOutOfRangeException(nameof(principalKind)),
        };

        var exception = await Assert.ThrowsAsync<ApplicationAdministrationException>(() =>
            fixture.Service.GetApplicationsAsync(authority));

        Assert.Equal(ApplicationAdministrationError.Forbidden, exception.Error);
        if (principalKind == PrincipalKind.ServiceApplication)
        {
            Assert.Contains(ApplicationPermissionIds.IdentityApplicationsWrite, fixture.Evaluator.Requirements);
        }
    }

    [Fact]
    public async Task RotationAndRevocation_PreservePermissionsAndEmitSecretFreeAuditFacts()
    {
        var fixture = new Fixture();
        var created = await fixture.Service.CreateApplicationAsync(
            Fixture.HumanAdministrator,
            new CreateApplicationRequest(
                "Automation",
                null,
                ApplicationTypeDto.Automation,
                false,
                [ApplicationPermissionIds.LibraryRead.Value]));
        var issued = await fixture.Service.IssueCredentialAsync(
            Fixture.HumanAdministrator,
            created.Id,
            new CreateApplicationCredentialRequest("Primary", null));
        var before = (await fixture.Repository.GetApplicationPermissionsAsync(created.Id)).ToArray();

        var rotated = await fixture.Service.RotateCredentialAsync(
            Fixture.HumanAdministrator,
            created.Id,
            issued.Credential.Id,
            new RotateApplicationCredentialRequest(null, Now.AddDays(90)));

        Assert.NotEqual(issued.PlaintextCredential, rotated.PlaintextCredential);
        Assert.Equal(before, await fixture.Repository.GetApplicationPermissionsAsync(created.Id));
        await fixture.Service.RevokeCredentialAsync(
            Fixture.HumanAdministrator,
            created.Id,
            rotated.Credential.Id);
        var response = await fixture.Service.GetApplicationAsync(Fixture.HumanAdministrator, created.Id);
        Assert.Equal(2, response.Credentials.Count);
        Assert.All(response.Credentials, value => Assert.NotNull(value.RevokedAt));
        Assert.All(fixture.Audits.Events, audit =>
            Assert.DoesNotContain(audit.Changes.Keys, key =>
                key.Contains("hash", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("plaintext", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("secret", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ClientBindings_AreUserClientOnlyAndAppearInApplicationResponses()
    {
        var fixture = new Fixture();
        var userClient = await fixture.Service.CreateApplicationAsync(
            Fixture.HumanAdministrator,
            new CreateApplicationRequest(
                "Native client",
                null,
                ApplicationTypeDto.UserClient,
                false,
                [ApplicationPermissionIds.LibraryRead.Value]));
        var bound = await fixture.Service.ReplaceClientBindingsAsync(
            Fixture.HumanAdministrator,
            userClient.Id,
            new SetApplicationClientBindingsRequest(["tuvima-custom", "tuvima-tablet"]));

        Assert.Equal(["tuvima-custom", "tuvima-tablet"], bound.ClientIds);
        Assert.Equal(
            userClient.Id,
            (await fixture.Repository.GetApplicationByClientIdAsync("tuvima-custom"))!.Id);
        Assert.Contains(
            fixture.Audits.Events,
            value => value.EventType == "application.client_bindings_changed");

        var typeChange = await Assert.ThrowsAsync<ApplicationAdministrationException>(() =>
            fixture.Service.UpdateApplicationAsync(
                Fixture.HumanAdministrator,
                userClient.Id,
                new UpdateApplicationRequest(
                    "Native client",
                    null,
                    ApplicationTypeDto.ServerIntegration,
                    true,
                    false)));
        Assert.Equal(ApplicationAdministrationError.Validation, typeChange.Error);

        var integration = await fixture.Service.CreateApplicationAsync(
            Fixture.HumanAdministrator,
            new CreateApplicationRequest(
                "Integration",
                null,
                ApplicationTypeDto.ServerIntegration,
                false,
                [ApplicationPermissionIds.LibraryRead.Value]));
        var wrongType = await Assert.ThrowsAsync<ApplicationAdministrationException>(() =>
            fixture.Service.ReplaceClientBindingsAsync(
                Fixture.HumanAdministrator,
                integration.Id,
                new SetApplicationClientBindingsRequest(["submitted-client-id"])));
        Assert.Equal(ApplicationAdministrationError.Validation, wrongType.Error);
    }

    [Fact]
    public async Task EndpointRegistration_MapsEveryApplicationRouteWithTheCombinedAuthorityRequirement()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        var fixture = new Fixture();
        builder.Services.AddSingleton(fixture.Service);
        builder.Services.AddSingleton<IRequestAuthorityResolver, StubAuthorityResolver>();
        await using var app = builder.Build();
        app.MapApplicationEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/access/applications", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(12, endpoints.Length);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/access/applications/permissions");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/access/applications/{applicationId:guid}/client-bindings");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/access/applications/{applicationId:guid}/credentials/{credentialId:guid}/rotate");
        Assert.All(endpoints, endpoint =>
        {
            var policy = Assert.IsType<AuthorizationPolicy>(
                endpoint.Metadata.Single(metadata => metadata is AuthorizationPolicy));
            var requirement = Assert.Single(
                policy.Requirements.OfType<AdministratorOrApplicationRequirement>());
            Assert.Equal(ApplicationPermissionIds.IdentityApplicationsWrite, requirement.Permission);
        });
    }

    [Fact]
    public async Task EndpointErrors_UseProblemDetailsStatusAndBodyContracts()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        var cases = new[]
        {
            (ApplicationAdministrationException.Unauthorized("Sign in."), 401),
            (ApplicationAdministrationException.Forbidden("Denied."), 403),
            (ApplicationAdministrationException.NotFound("Missing."), 404),
            (ApplicationAdministrationException.Validation("Invalid."), 400),
            (ApplicationAdministrationException.Conflict("Changed."), 409),
        };

        foreach (var (exception, expectedStatus) in cases)
        {
            var context = new DefaultHttpContext
            {
                RequestServices = app.Services,
                Response = { Body = new MemoryStream() },
            };
            await ApplicationEndpoints.MapError(exception).ExecuteAsync(context);

            Assert.Equal(expectedStatus, context.Response.StatusCode);
            Assert.Equal("application/problem+json", context.Response.ContentType);
            context.Response.Body.Position = 0;
            using var body = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.Equal(expectedStatus, body.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(exception.Message, body.RootElement.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task IssuingAKey_WithoutTheApprovalHash_IsRefusedWithThePreview_AndNoKeyIsIssued()
    {
        using var approvals = new ApprovalTestKit();
        var fixture = new Fixture();
        var application = await CreateIntegrationAsync(fixture);

        var result = await ApplicationEndpoints.IssueCredentialAsync(
            application.Id, new CreateApplicationCredentialRequest("Key", null), approvals.Http(), approvals.Resolver, fixture.Service, approvals.Service, default);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(409, status);
        var required = JsonSerializer.Deserialize<MediaEngine.Contracts.Approvals.ApprovalRequiredResponse>(body)!;
        var preview = Assert.Single(required.Approvals);
        Assert.Equal("application_credential", preview.Kind);
        Assert.Equal("Key for Integration", preview.Title);
        Assert.Contains("Never expires", preview.CanDo);
        Assert.Equal("the software that holds this key", preview.SendsDataTo);
        Assert.Empty((await fixture.Repository.GetApplicationCredentialsAsync(application.Id)));
    }

    [Fact]
    public async Task IssuingAKey_WithTheHash_IssuesItAndRecordsTheApprovalUnderTheNewKeyId_WithoutTheKeyItself()
    {
        using var approvals = new ApprovalTestKit();
        var fixture = new Fixture();
        var application = await CreateIntegrationAsync(fixture);
        var expiry = Now.AddDays(30);
        var preview = approvals.Service.PreviewCredential(application, expiry);
        var libraryRead = new PermissionRegistry().GetAll().Single(d => d.Id == ApplicationPermissionIds.LibraryRead).DisplayName;

        var result = await ApplicationEndpoints.IssueCredentialAsync(
            application.Id, new CreateApplicationCredentialRequest("Key", expiry, preview.StatementHash), approvals.Http(), approvals.Resolver, fixture.Service, approvals.Service, default);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(200, status);
        using var json = JsonDocument.Parse(body);
        var plaintext = json.RootElement.GetProperty("plaintext_credential").GetString()!;
        var credentialId = json.RootElement.GetProperty("credential").GetProperty("id").GetGuid();
        var record = Assert.Single(await approvals.Records.ListAsync("application_credential", credentialId.ToString("D")));
        Assert.Equal(ApprovalTestKit.ApproverName, record.ApprovedByName);
        Assert.Equal(approvals.Clock.GetUtcNow(), record.ApprovedAt);
        Assert.Contains(libraryRead, record.StatementJson, StringComparison.Ordinal);
        Assert.Contains("Expires on", record.StatementJson, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, record.StatementJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryKeyIsAskedAbout_EvenForAnApplicationThatAlreadyHasAnApprovedKey()
    {
        using var approvals = new ApprovalTestKit();
        var fixture = new Fixture();
        var application = await CreateIntegrationAsync(fixture);
        var hash = approvals.Service.PreviewCredential(application, null).StatementHash;
        await ApplicationEndpoints.IssueCredentialAsync(
            application.Id, new CreateApplicationCredentialRequest("First", null, hash), approvals.Http(), approvals.Resolver, fixture.Service, approvals.Service, default);

        var second = await ApplicationEndpoints.IssueCredentialAsync(
            application.Id, new CreateApplicationCredentialRequest("Second", null), approvals.Http(), approvals.Resolver, fixture.Service, approvals.Service, default);

        Assert.Equal(409, (await ExecuteAsync(second)).Status);
        Assert.Single(await fixture.Repository.GetApplicationCredentialsAsync(application.Id));
    }

    [Fact]
    public async Task IssuingAKey_WithAHashFromBeforeThePermissionsGrew_IsRefused()
    {
        using var approvals = new ApprovalTestKit();
        var fixture = new Fixture();
        var application = await CreateIntegrationAsync(fixture);
        var staleHash = approvals.Service.PreviewCredential(application, null).StatementHash;
        await fixture.Service.ReplacePermissionsAsync(
            Fixture.HumanAdministrator,
            application.Id,
            new SetApplicationPermissionsRequest([ApplicationPermissionIds.LibraryRead.Value, ApplicationPermissionIds.MetadataWrite.Value]));

        var result = await ApplicationEndpoints.IssueCredentialAsync(
            application.Id, new CreateApplicationCredentialRequest("Key", null, staleHash), approvals.Http(), approvals.Resolver, fixture.Service, approvals.Service, default);

        Assert.Equal(409, (await ExecuteAsync(result)).Status);
        Assert.Empty(await fixture.Repository.GetApplicationCredentialsAsync(application.Id));
    }

    private static Task<ApplicationResponse> CreateIntegrationAsync(Fixture fixture) => fixture.Service.CreateApplicationAsync(
        Fixture.HumanAdministrator,
        new CreateApplicationRequest(
            "Integration",
            null,
            ApplicationTypeDto.ServerIntegration,
            false,
            [ApplicationPermissionIds.LibraryRead.Value]));

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class Fixture
    {
        public static RequestAuthority HumanAdministrator { get; } = new(
            PrincipalKind.Human,
            true,
            AccountId: Guid.Parse("10000000-0000-0000-0000-000000000001"),
            ActiveProfileId: Guid.Parse("20000000-0000-0000-0000-000000000001"),
            SessionId: Guid.Parse("30000000-0000-0000-0000-000000000001"),
            AccountEnabled: true,
            GrantEnabled: true,
            AccountIsAdministrator: true,
            GrantAdminEnabled: true);

        public static RequestAuthority DelegatedAdministrator { get; } = HumanAdministrator with
        {
            PrincipalKind = PrincipalKind.DelegatedUserClient,
            ApplicationId = Guid.Parse("40000000-0000-0000-0000-000000000001"),
            ApplicationEnabled = true,
        };

        public static RequestAuthority AdministratorApplication { get; } = new(
            PrincipalKind.ServiceApplication,
            true,
            ApplicationId: Guid.Parse("50000000-0000-0000-0000-000000000001"),
            ApplicationEnabled: true,
            ApplicationIsAdministrator: true);

        public Fixture(
            AuthorizationDecision? applicationDecision = null,
            AuthorizationDecision? administratorDecision = null,
            IPluginApplicationPermissionAvailability? pluginAvailability = null)
        {
            Repository = new MemoryApplicationRepository();
            Registry = new TestPermissionRegistry();
            Evaluator = new StubEvaluator(applicationDecision ?? AuthorizationDecision.Allow());
            Administrator = new StubAccountAccess(administratorDecision ?? AuthorizationDecision.Allow());
            Invalidation = new CapturingInvalidation();
            Audits = new CapturingAuditWriter();
            Service = new(
                Repository,
                Registry,
                Evaluator,
                Administrator,
                Invalidation,
                Audits,
                new FixedTimeProvider(Now),
                pluginAvailability);
        }

        public MemoryApplicationRepository Repository { get; }
        public TestPermissionRegistry Registry { get; }
        public StubEvaluator Evaluator { get; }
        public StubAccountAccess Administrator { get; }
        public CapturingInvalidation Invalidation { get; }
        public CapturingAuditWriter Audits { get; }
        public ApplicationAdministrationService Service { get; }
    }

    private sealed class TestPermissionRegistry : IPermissionRegistry
    {
        public static readonly ApplicationPermissionId PluginPermissionId = new("plugin.tuvima.fandom-lore.universe-lore.discover");
        private static readonly PermissionDefinition[] Definitions =
        [
            Definition(ApplicationPermissionIds.IdentityApplicationsWrite, [ApplicationType.ServerIntegration, ApplicationType.Automation]),
            Definition(ApplicationPermissionIds.LibraryRead, Enum.GetValues<ApplicationType>()),
            Definition(ApplicationPermissionIds.MetadataWrite, [ApplicationType.ServerIntegration, ApplicationType.Automation]),
            Definition(ApplicationPermissionIds.EventsSubscribe, Enum.GetValues<ApplicationType>(), false),
            new(PluginPermissionId, "Plugins", "Discover Fandom lore sources", "Discovers sources.",
                PermissionRiskLevel.Read, [ApplicationType.Automation], false, PermissionProvenance.Plugin,
                "tuvima.fandom-lore", 1_700, PermissionAvailability.Available, null),
        ];

        public IReadOnlyList<PermissionDefinition> GetAll() => Definitions;

        public IReadOnlyList<PermissionDefinition> GetAvailableFor(ApplicationType applicationType) =>
            Definitions.Where(value => value.IsAvailable && value.ApplicationTypes.Contains(applicationType)).ToArray();

        public bool TryGet(ApplicationPermissionId id, out PermissionDefinition definition)
        {
            definition = Definitions.SingleOrDefault(value => value.Id == id)!;
            return definition is not null;
        }

        private static PermissionDefinition Definition(
            ApplicationPermissionId id,
            IEnumerable<ApplicationType> types,
            bool available = true) => new(
            id,
            "Test",
            id.Value,
            $"Description for {id.Value}",
            PermissionRiskLevel.Administrative,
            types,
            false,
            PermissionProvenance.BuiltIn,
            null,
            1,
            available ? PermissionAvailability.Available : PermissionAvailability.Unavailable,
            available ? null : "The backing service is not available.");
    }

    private sealed class StubPluginAvailability(bool available, string? reason) : IPluginApplicationPermissionAvailability
    {
        public bool TryGetAvailability(string permissionId, out bool isAvailable, out string? unavailableReason)
        {
            isAvailable = available;
            unavailableReason = reason;
            return permissionId == TestPermissionRegistry.PluginPermissionId.Value;
        }
    }

    private sealed class StubEvaluator(AuthorizationDecision decision) : DomainAuthorizationEvaluator
    {
        public List<ApplicationPermissionId> Requirements { get; } = [];

        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default)
        {
            if (requirement.ApplicationPermission is { } permission)
            {
                Requirements.Add(permission);
            }

            return ValueTask.FromResult(decision);
        }
    }

    private sealed class StubAccountAccess(AuthorizationDecision decision) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(
            RequestAuthority authority,
            AccountFeatureId feature,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(decision);

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(
            RequestAuthority authority,
            Guid libraryId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(decision);

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(
            RequestAuthority authority,
            bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(decision);
    }

    private sealed class CapturingInvalidation : IAuthorizationInvalidationService
    {
        public List<Guid> ApplicationIds { get; } = [];

        public ValueTask InvalidateAccountAsync(Guid accountId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask InvalidateGrantAsync(
            Guid accountId,
            Guid profileId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask InvalidateApplicationAsync(
            Guid applicationId,
            CancellationToken cancellationToken = default)
        {
            ApplicationIds.Add(applicationId);
            return ValueTask.CompletedTask;
        }

        public ValueTask InvalidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class CapturingAuditWriter : IAuthorizationAuditWriter
    {
        public List<AuthorizationAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(
            AuthorizationAuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubAuthorityResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(
            HttpContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(Fixture.HumanAdministrator);
    }

    private sealed class MemoryApplicationRepository : IApplicationRepository
    {
        private readonly Dictionary<Guid, ApplicationEntity> _applications = [];
        private readonly Dictionary<Guid, HashSet<ApplicationPermissionId>> _permissions = [];
        private readonly Dictionary<Guid, List<ApplicationCredential>> _credentials = [];
        private readonly Dictionary<Guid, HashSet<string>> _clientIds = [];

        public Task<ApplicationEntity?> GetApplicationAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_applications.TryGetValue(id, out var value) ? Clone(value) : null);

        public Task<ApplicationEntity?> GetApplicationByClientIdAsync(
            string clientId,
            CancellationToken ct = default)
        {
            var match = _clientIds.SingleOrDefault(value => value.Value.Contains(clientId));
            if (match.Key == Guid.Empty ||
                !_applications.TryGetValue(match.Key, out var application) ||
                !application.IsEnabled ||
                application.ApplicationType != ApplicationType.UserClient)
            {
                return Task.FromResult<ApplicationEntity?>(null);
            }
            return Task.FromResult<ApplicationEntity?>(Clone(application));
        }

        public Task<IReadOnlyList<ApplicationEntity>> GetApplicationsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ApplicationEntity>>(_applications.Values.Select(Clone).ToArray());

        public Task<IReadOnlySet<ApplicationPermissionId>> GetApplicationPermissionsAsync(
            Guid applicationId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<ApplicationPermissionId>>(
                _permissions.GetValueOrDefault(applicationId, []).ToFrozenSet());

        public Task<IReadOnlySet<string>> GetApplicationClientBindingsAsync(
            Guid applicationId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(
                _clientIds.GetValueOrDefault(applicationId, []).ToFrozenSet(StringComparer.Ordinal));

        public Task<ApplicationCredentialIdentity?> FindApplicationCredentialAsync(
            string hash,
            DateTimeOffset now,
            CancellationToken ct = default) => Task.FromResult<ApplicationCredentialIdentity?>(null);

        public Task<IReadOnlyList<ApplicationCredential>> GetApplicationCredentialsAsync(
            Guid applicationId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ApplicationCredential>>(
                _credentials.GetValueOrDefault(applicationId, []).Select(Clone).ToArray());

        public Task InsertApplicationAsync(
            ApplicationEntity application,
            IReadOnlySet<ApplicationPermissionId> permissions,
            CancellationToken ct = default)
        {
            _applications.Add(application.Id, Clone(application));
            _permissions.Add(application.Id, permissions.ToHashSet());
            _credentials.Add(application.Id, []);
            _clientIds.Add(application.Id, new HashSet<string>(StringComparer.Ordinal));
            return Task.CompletedTask;
        }

        public Task UpdateApplicationAsync(ApplicationEntity application, CancellationToken ct = default)
        {
            var stored = Clone(application);
            stored.AuthorizationVersion = _applications[application.Id].AuthorizationVersion + 1;
            _applications[application.Id] = stored;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteApplicationAsync(
            Guid applicationId,
            DateTimeOffset deletedAt,
            CancellationToken ct = default) => Task.FromResult(_applications.Remove(applicationId));

        public Task ReplacePermissionsAsync(
            Guid applicationId,
            IReadOnlySet<ApplicationPermissionId> permissions,
            DateTimeOffset changedAt,
            CancellationToken ct = default)
        {
            _permissions[applicationId] = permissions.ToHashSet();
            _applications[applicationId].AuthorizationVersion++;
            return Task.CompletedTask;
        }

        public Task ReplaceClientBindingsAsync(
            Guid applicationId,
            IReadOnlySet<string> clientIds,
            DateTimeOffset changedAt,
            CancellationToken ct = default)
        {
            if (_applications[applicationId].ApplicationType != ApplicationType.UserClient)
            {
                throw new InvalidOperationException("Client bindings require a UserClient application.");
            }

            if (_clientIds.Any(pair =>
                    pair.Key != applicationId &&
                    pair.Value.Overlaps(clientIds)))
            {
                throw new InvalidOperationException(
                    "A client identifier is already bound to another application.");
            }
            _clientIds[applicationId] = new HashSet<string>(clientIds, StringComparer.Ordinal);
            _applications[applicationId].AuthorizationVersion++;
            return Task.CompletedTask;
        }

        public Task InsertCredentialAsync(ApplicationCredential credential, CancellationToken ct = default)
        {
            _credentials[credential.ApplicationId].Add(Clone(credential));
            _applications[credential.ApplicationId].AuthorizationVersion++;
            return Task.CompletedTask;
        }

        public Task<bool> RevokeCredentialAsync(
            Guid applicationId,
            Guid credentialId,
            DateTimeOffset revokedAt,
            CancellationToken ct = default)
        {
            var credential = _credentials[applicationId].SingleOrDefault(value => value.Id == credentialId);
            if (credential is null || credential.RevokedAt is not null)
            {
                return Task.FromResult(false);
            }

            credential.RevokedAt = revokedAt;
            _applications[applicationId].AuthorizationVersion++;
            return Task.FromResult(true);
        }

        public Task<bool> RotateCredentialAsync(
            Guid applicationId,
            Guid priorCredentialId,
            ApplicationCredential replacement,
            DateTimeOffset rotatedAt,
            CancellationToken ct = default)
        {
            var prior = _credentials[applicationId].SingleOrDefault(value => value.Id == priorCredentialId);
            if (prior is null || prior.RevokedAt is not null)
            {
                return Task.FromResult(false);
            }

            prior.RevokedAt = rotatedAt;
            _credentials[applicationId].Add(Clone(replacement));
            _applications[applicationId].AuthorizationVersion++;
            return Task.FromResult(true);
        }

        public Task TouchCredentialUsageAsync(
            Guid applicationId,
            Guid credentialId,
            DateTimeOffset usedAt,
            CancellationToken ct = default) => Task.CompletedTask;

        private static ApplicationEntity Clone(ApplicationEntity value) => new()
        {
            Id = value.Id,
            Name = value.Name,
            Description = value.Description,
            ApplicationType = value.ApplicationType,
            IsEnabled = value.IsEnabled,
            IsAdministrator = value.IsAdministrator,
            AuthorizationVersion = value.AuthorizationVersion,
            CreatedAt = value.CreatedAt,
            UpdatedAt = value.UpdatedAt,
            LastUsedAt = value.LastUsedAt,
        };

        private static ApplicationCredential Clone(ApplicationCredential value) => new()
        {
            Id = value.Id,
            ApplicationId = value.ApplicationId,
            Name = value.Name,
            CredentialHash = value.CredentialHash,
            HashScheme = value.HashScheme,
            CreatedAt = value.CreatedAt,
            ExpiresAt = value.ExpiresAt,
            LastUsedAt = value.LastUsedAt,
            RevokedAt = value.RevokedAt,
        };
    }
}
