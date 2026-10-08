# P00 ownership and interface manifest (P01-P05)

Status: frozen for dispatch from tentative source SHA `1b75af4e76ad4b4afc9ce23769877566f6ec10e2`. Workers use the clean integration worktree SHA supplied by the coordinator. Paths below are exclusive during each parallel wave; an unlisted required shared-file edit is sent to the P02 integrator as a minimal patch request.

## P01 — shared contracts and definitions (sequential)

Existing owned files:

```text
src/MediaEngine.Contracts/Authentication/ClientAuthorizationContracts.cs
tests/MediaEngine.Contracts.Tests/Fixtures/contracts-shape.approved.txt
tests/MediaEngine.Contracts.Tests/Fixtures/wire-type-inventory.approved.txt
```

New owned paths:

```text
src/MediaEngine.Domain/Authorization/AuthorizationPrimitives.cs
src/MediaEngine.Domain/Authorization/AuthorizationModels.cs
src/MediaEngine.Domain/Authorization/AuthorizationAuditEvent.cs
src/MediaEngine.Domain/Authorization/ApplicationPermissionIds.cs
src/MediaEngine.Domain/Authorization/PermissionDefinition.cs
src/MediaEngine.Domain/Authorization/PermissionRegistry.cs
src/MediaEngine.Domain/Authorization/ApplicationPermissionPresets.cs
src/MediaEngine.Domain/Contracts/IAuthorizationServices.cs
src/MediaEngine.Contracts/Authentication/AccessContracts.cs
src/MediaEngine.Contracts/Authentication/ApplicationContracts.cs
src/MediaEngine.Contracts/Authentication/ApplicationEventContracts.cs
tests/MediaEngine.Domain.Tests/AuthorizationFoundationTests.cs
tests/MediaEngine.Contracts.Tests/AccessContractTests.cs
```

P01 was delivered in worker commit `df0a9a2e`, integrated as `7060a837`. Paths above reflect its actual organization. The authoritative complete interfaces are in `IAuthorizationServices.cs`; the excerpt below highlights the common evaluator boundary. Account/profile grant identity is the compound pair, not a synthetic grant GUID. See [schema handoff](./p01-schema-design.md).

Frozen interface excerpt:

```csharp
interface IPermissionRegistry {
  IReadOnlyList<PermissionDefinition> GetAll();
  bool TryGet(ApplicationPermissionId id, out PermissionDefinition definition);
}
interface IAuthorizationEvaluator {
  ValueTask<AuthorizationDecision> EvaluateAsync(
    RequestAuthority authority, AuthorizationRequirement requirement,
    ResourceAuthorizationContext? resource, CancellationToken cancellationToken);
}
interface IAuthorizationInvalidationService {
  ValueTask InvalidateAccountAsync(Guid accountId, CancellationToken cancellationToken);
  ValueTask InvalidateApplicationAsync(Guid applicationId, CancellationToken cancellationToken);
}
interface IAuthorizationAuditWriter {
  ValueTask WriteAsync(AuthorizationAuditEvent auditEvent, CancellationToken cancellationToken);
}
```

`RequestAuthority` contains principal kind, Account/Profile/grant/Application/session/device IDs, authority versions, and effective-admin state. It contains no `ClaimsPrincipal`, secret, repository, or HTTP type. `AuthorizationRequirement` carries typed application permission, account feature, library/resource needs, human/self-service/admin-surface flags. `PermissionDefinition` includes availability/unavailable reason and provenance. DTOs include one dedicated one-time credential creation response.

## P02 — identity, schema, credentials, and shared integration files

```text
src/MediaEngine.Domain/Entities/Account.cs
src/MediaEngine.Domain/Entities/AccountCredential.cs
src/MediaEngine.Domain/Entities/AccountExternalLogin.cs
src/MediaEngine.Domain/Entities/AccountInvitation.cs
src/MediaEngine.Domain/Entities/ApiKey.cs
src/MediaEngine.Domain/Aggregates/Profile.cs
src/MediaEngine.Domain/Roles.cs
src/MediaEngine.Domain/Contracts/IAccountRepository.cs
src/MediaEngine.Domain/Contracts/IAccountExternalLoginRepository.cs
src/MediaEngine.Domain/Contracts/IClientAuthorizationRepository.cs
src/MediaEngine.Domain/Contracts/IIdentityRepository.cs
src/MediaEngine.Domain/Contracts/IApiKeyRepository.cs
src/MediaEngine.Contracts/Admin/AdminContracts.cs
src/MediaEngine.Contracts/Authentication/AuthContracts.cs
src/MediaEngine.Contracts/Profiles/ProfileContracts.cs
src/MediaEngine.Identity/FirstPartyIdentityService.cs
src/MediaEngine.Identity/ProfileService.cs
src/MediaEngine.Identity/AccountExternalLoginService.cs
src/MediaEngine.Identity/Contracts/IAccountExternalLoginService.cs
src/MediaEngine.Storage/Schema/schema.sql
src/MediaEngine.Storage/SchemaInitializer.cs
src/MediaEngine.Storage/SchemaMigrator.cs
src/MediaEngine.Storage/DatabaseConnection.cs
src/MediaEngine.Storage/AccountRepository.cs
src/MediaEngine.Storage/AccountExternalLoginRepository.cs
src/MediaEngine.Storage/IdentityRepository.cs
src/MediaEngine.Storage/ProfileRepository.cs
src/MediaEngine.Storage/ClientAuthorizationRepository.cs
src/MediaEngine.Storage/ApiKeyRepository.cs
src/MediaEngine.Api/Program.cs
src/MediaEngine.Api/DependencyInjection/ApiEndpointRouteBuilderExtensions.cs
src/MediaEngine.Api/Security/TuvimaAuthentication.cs
src/MediaEngine.Api/Security/AdministratorElevationAuthorization.cs
src/MediaEngine.Api/Security/ClientAuthorizationService.cs
src/MediaEngine.Api/Security/ClientScopeFilter.cs
src/MediaEngine.Api/Security/DashboardServiceCredentialBootstrapper.cs
src/MediaEngine.Api/Security/IntercomTokenService.cs
src/MediaEngine.Api/Security/IntercomTokenAuthenticationMiddleware.cs
src/MediaEngine.Api/Security/IntercomAuthFilter.cs
src/MediaEngine.Api/Security/RoleAuthorizationFilter.cs
src/MediaEngine.Api/Security/ViewProfileAssertion.cs
src/MediaEngine.Api/Services/ApiKeyService.cs
src/MediaEngine.Api/Services/ApiKeyLookupCache.cs
src/MediaEngine.Api/Services/IApiKeyLookupCache.cs
src/MediaEngine.Api/Endpoints/AccountEndpoints.cs
src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs
src/MediaEngine.Api/Endpoints/ClientAuthorizationEndpoints.cs
src/MediaEngine.Api/Endpoints/AdminEndpoints.cs
src/MediaEngine.Api/Endpoints/SetupEndpoints.cs
src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs
tests/MediaEngine.Identity.Tests/FirstPartyIdentityServiceTests.cs
tests/MediaEngine.Identity.Tests/ProfileServiceTests.cs
tests/MediaEngine.Api.Tests/ClientAuthorizationServiceFlowTests.cs
tests/MediaEngine.Api.Tests/ApiKeyServiceTests.cs
tests/MediaEngine.Api.Tests/ApiKeyLookupCacheTests.cs
```

P02 implements `IRequestAuthorityResolver.ResolveAsync(HttpContext, CancellationToken)`, `IAdministratorSurfaceAuthorizationService`, `IGrantAdminUnlockService`, and transactional `IAccountAccessMutationService`/`IApplicationAdministrationService` in API/Identity boundaries. P02 alone changes schema, root API DI, authentication handlers, account/profile wire contracts, and P01 definitions after freeze.

## P03 — catalogue/service endpoint enforcement

Exclusive endpoint files:

```text
src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs
src/MediaEngine.Api/Endpoints/AiEndpoints.cs
src/MediaEngine.Api/Endpoints/CanonEndpoints.cs
src/MediaEngine.Api/Endpoints/CapabilityEndpoints.cs
src/MediaEngine.Api/Endpoints/CharacterEndpoints.cs
src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs
src/MediaEngine.Api/Endpoints/DebugEndpoints.cs
src/MediaEngine.Api/Endpoints/DeferredEnrichmentEndpoints.cs
src/MediaEngine.Api/Endpoints/DetailEndpoints.cs
src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs
src/MediaEngine.Api/Endpoints/EnrichmentRefreshEndpoints.cs
src/MediaEngine.Api/Endpoints/HlsStreamEndpoints.cs
src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs
src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs
src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs
src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs
src/MediaEngine.Api/Endpoints/LibraryMutationEndpoints.cs
src/MediaEngine.Api/Endpoints/LibraryReorganizationEndpoints.cs
src/MediaEngine.Api/Endpoints/MaintenanceEndpoints.cs
src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs
src/MediaEngine.Api/Endpoints/MetadataEndpoints.MediaEditorNavigator.cs
src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs
src/MediaEngine.Api/Endpoints/OperationsEndpoints.cs
src/MediaEngine.Api/Endpoints/PersonEndpoints.cs
src/MediaEngine.Api/Endpoints/PlaybackEndpoints.cs
src/MediaEngine.Api/Endpoints/PlaybackSegmentEndpoints.cs
src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs
src/MediaEngine.Api/Endpoints/PluginEndpoints.cs
src/MediaEngine.Api/Endpoints/ProgressEndpoints.cs
src/MediaEngine.Api/Endpoints/ProviderCatalogueEndpoints.cs
src/MediaEngine.Api/Endpoints/ReadEndpoints.cs
src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs
src/MediaEngine.Api/Endpoints/ReportEndpoints.cs
src/MediaEngine.Api/Endpoints/ReviewEndpoints.cs
src/MediaEngine.Api/Endpoints/SearchEndpoints.cs
src/MediaEngine.Api/Endpoints/ServerFolderEndpoints.cs
src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs
src/MediaEngine.Api/Endpoints/StreamEndpoints.cs
src/MediaEngine.Api/Endpoints/SystemEndpoints.cs
src/MediaEngine.Api/Endpoints/TimelineEndpoints.cs
src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs
src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs
src/MediaEngine.Api/Endpoints/UniverseLoreEndpoints.cs
src/MediaEngine.Api/Endpoints/WorkEndpoints.cs
tests/MediaEngine.Api.Tests/RouteAuthorizationGuardrailTests.cs
tests/MediaEngine.Api.Tests/ClientApiV1GuardrailTests.cs
```

Read/query services touched by P03 must be claimed by exact path in its dispatch before edits. P03 cannot edit P02 shared files or P04 files. DI/contract needs go to the integrator.

## P04 — View privacy

```text
src/MediaEngine.Api/Endpoints/ViewEndpoints.cs
src/MediaEngine.Api/Endpoints/ViewDiscoveryEndpoints.cs
src/MediaEngine.Api/Endpoints/CollectionPersonalMediaEndpoints.cs
src/MediaEngine.Api/Services/View/IViewAssetQueryBackend.cs
src/MediaEngine.Api/Services/View/IViewQueryOrchestrator.cs
src/MediaEngine.Api/Services/View/IViewRequestProfileContext.cs
src/MediaEngine.Api/Services/View/IViewResourceAuthorizationService.cs
src/MediaEngine.Api/Services/View/IViewResourceStore.cs
src/MediaEngine.Api/Services/View/IViewScopeResolver.cs
src/MediaEngine.Api/Services/View/IViewScopeStore.cs
src/MediaEngine.Api/Services/View/IViewSmartGalleryQueryService.cs
src/MediaEngine.Api/Services/View/ViewDiscoveryService.cs
src/MediaEngine.Api/Services/View/ViewFolderService.cs
src/MediaEngine.Api/Services/View/ViewPersistenceAdapters.cs
src/MediaEngine.Api/Services/View/ViewQueryOrchestrator.cs
src/MediaEngine.Api/Services/View/ViewResourceAuthorization.cs
src/MediaEngine.Api/Services/View/ViewScopeModels.cs
src/MediaEngine.Api/Services/View/ViewScopeResolver.cs
src/MediaEngine.Api/Services/View/ViewSharedContributionService.cs
src/MediaEngine.Api/Services/View/ViewSharedTransferService.cs
src/MediaEngine.Api/Services/View/ViewSmartGalleryQueryService.cs
src/MediaEngine.Api/Services/View/ViewThumbnailService.cs
src/MediaEngine.Api/Services/Collections/CollectionPersonalMediaService.cs
src/MediaEngine.Domain/Services/LibraryAccessEvaluator.cs
src/MediaEngine.Domain/Contracts/IViewProfileRepository.cs
src/MediaEngine.Contracts/LocalAssets/ViewDiscoveryContracts.cs
src/MediaEngine.Contracts/LocalAssets/ViewFolderContracts.cs
src/MediaEngine.Storage/ViewDiscoveryRepository.cs
src/MediaEngine.Storage/ViewGalleryRepository.cs
src/MediaEngine.Storage/ViewPersonalSpaceRepository.cs
src/MediaEngine.Storage/ViewProfileRepository.cs
src/MediaEngine.Storage/LocalAssetRepository.cs
src/MediaEngine.Storage/Contracts/ILocalAssetRepository.cs
src/MediaEngine.Storage/CollectionViewSourceRepository.cs
src/MediaEngine.Storage/Contracts/IViewDiscoveryRepository.cs
tests/MediaEngine.Api.Tests/ViewRequestProfileContextTests.cs
tests/MediaEngine.Api.Tests/ViewResourceAuthorizationTests.cs
tests/MediaEngine.Api.Tests/ViewScopeResolverTests.cs
tests/MediaEngine.Api.Tests/ViewProfilePolicyContractTests.cs
```

P04 consumes P02 `RequestAuthority`; it does not change its shape. Its resource decision interface returns allowed/denied without substituting Mine for an unauthorized explicit ID. Original-file mutations continue through existing filesystem gates.

The September 9 ownership clarification assigns the View asset writer and its registration/content-location records to P04. It must persist the published explicit personal/shared scope and Personal Space fields and represent server-owned Shared assets without a profile owner. This expands View implementation ownership only; shared authorization contracts and schema remain P02-owned.

## P05 — Dashboard identity and functional wiring

```text
src/MediaEngine.Web/Program.cs
src/MediaEngine.Web/Shared/MainLayout.razor
src/MediaEngine.Web/Shared/MainLayout.razor.css
src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs
src/MediaEngine.Web/Components/Navigation/TopNavAccountMenu.razor
src/MediaEngine.Web/Components/Navigation/TopNavAccountMenu.razor.css
src/MediaEngine.Web/Components/Settings/UsersAccessSettingsTab.razor
src/MediaEngine.Web/Components/Settings/UsersAccessSettingsTab.razor.css
src/MediaEngine.Web/Components/Settings/AccountsAccessTab.razor
src/MediaEngine.Web/Components/Settings/ApiKeysTab.razor
src/MediaEngine.Web/Components/Settings/ApiKeysTab.razor.css
src/MediaEngine.Web/Components/Settings/AccountSettingsTab.razor
src/MediaEngine.Web/Services/Integration/ActiveProfileAccessor.cs
src/MediaEngine.Web/Services/Integration/ActiveProfileSessionService.cs
src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs
src/MediaEngine.Web/Services/Integration/DashboardEngineAuthenticationHandler.cs
src/MediaEngine.Web/Services/Integration/DashboardSessionAccessor.cs
src/MediaEngine.Web/Services/Integration/ViewMediaEngineClient.cs
src/MediaEngine.Web/Services/Integration/ViewMediaGrantService.cs
src/MediaEngine.Web/Services/Integration/ViewMediaProxyEndpoint.cs
src/MediaEngine.Web/Services/Integration/ViewProfileAssertionHandler.cs
src/MediaEngine.Web/Services/Integration/EngineApiClient.Settings.cs
src/MediaEngine.Web/Services/Integration/IEngineApiClient.Settings.cs
src/MediaEngine.Web/Services/Integration/EngineApiClient.View.cs
src/MediaEngine.Web/Services/Integration/IEngineApiClient.View.cs
tests/MediaEngine.Web.Tests/SettingsNavTests.cs
tests/MediaEngine.Web.Tests/TrustedViewProfileAssertionTests.cs
tests/MediaEngine.Web.Tests/ViewMediaProxyTests.cs
tests/MediaEngine.Web.Tests/ViewProfileSettingsUiTests.cs
```

P05 receives Access DTOs from P02 and must not duplicate them in Web models. P06 later owns visual refinement; P05 must already deliver working routes and denial behavior.

For execution, P05 is authorized to update additional Web-only files/client partials required to remove old Role/elevation authority, including the Settings route shell and editor launchers. It must report those files at handoff. Engine/Domain/Contracts changes still require the shared owner; P04 supplies the View scope/proxy contract.

## Parallel-wave collision rules

P02 owns all shared authorization definitions after the P01 commit, schema/bootstrap, API composition, and common Contracts. P03 owns non-View Engine endpoint enforcement. P04 owns View Engine paths. P05 owns Dashboard wiring. `IngestionEndpoints.cs` currently has an unrelated dirty change in the original checkout; the coordinator must integrate that work before P03 edits the clean Access branch or explicitly rebase/reconcile it.
