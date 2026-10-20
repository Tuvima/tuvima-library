# Endpoint registration ledger

Generated from source by `generate-endpoint-ledger.ps1`. It is a syntactic inventory, not proof that a guard authorizes the correct resource. Re-run after route changes and reconcile against `endpoint-matrix.md`. Routes containing interpolated helper fragments are preserved as written.

| Method | Registered route | Declared guard | Environment | Source |
| --- | --- | --- | --- | --- |
| GET | `/dev/check-keys` | fallback policy / middleware | Development only | `src/MediaEngine.Api/DevSupport/DevSeedEndpoints.cs:1019` |
| POST | `/dev/seed-library` | fallback policy / middleware | Development only | `src/MediaEngine.Api/DevSupport/DevSeedEndpoints.cs:1022` |
| POST | `/dev/wipe` | fallback policy / middleware | Development only | `src/MediaEngine.Api/DevSupport/DevSeedEndpoints.cs:1025` |
| POST | `/dev/full-test` | fallback policy / middleware | Development only | `src/MediaEngine.Api/DevSupport/DevSeedEndpoints.cs:1028` |
| POST | `/dev/reingest-library` | fallback policy / middleware | Development only | `src/MediaEngine.Api/DevSupport/DevSeedEndpoints.cs:1031` |
| GET | `/dev/pipeline-status` | fallback policy / middleware | Development only | `src/MediaEngine.Api/DevSupport/DevSeedEndpoints.cs:1034` |
| POST | `/dev/view-photo-harness` | fallback policy / middleware | Development only | `src/MediaEngine.Api/DevSupport/DevSeedEndpoints.cs:1037` |
| POST | `/dev/generate-ingestion-edge-fixtures` | fallback policy / middleware | Development only | `src/MediaEngine.Api/DevSupport/DevSeedEndpoints.cs:1045` |
| POST | `/dev/integration-test` | fallback policy / middleware | Development only | `src/MediaEngine.Api/DevSupport/IntegrationTestEndpoints.cs:720` |
| GET | `/access/self-service/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:28` |
| GET | `/access/self-service/external-logins` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:43` |
| DELETE | `/access/self-service/external-logins/{loginId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:52` |
| GET | `/access/admin-unlock` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:96` |
| POST | `/access/admin-unlock` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:103` |
| DELETE | `/access/admin-unlock` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:116` |
| GET | `/access/accounts/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:129` |
| GET | `/access/accounts/{accountId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:142` |
| POST | `/access/accounts/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:152` |
| PUT | `/access/accounts/{accountId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:170` |
| DELETE | `/access/accounts/{accountId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:183` |
| PUT | `/access/accounts/{accountId:guid}/access` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:192` |
| PUT | `/access/accounts/{accountId:guid}/grants/{profileId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:203` |
| DELETE | `/access/accounts/{accountId:guid}/grants/{profileId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:221` |
| PUT | `/access/accounts/{accountId:guid}/grants/{profileId:guid}/admin-protection` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:230` |
| POST | `/access/invitations` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:247` |
| GET | `/access/libraries` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:262` |
| GET | `/access/profiles/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:275` |
| POST | `/access/profiles/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:280` |
| PUT | `/access/profiles/{profileId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:291` |
| DELETE | `/access/profiles/{profileId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AccountEndpoints.cs:301` |
| GET | `/activity/summary` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:24` |
| GET | `/activity/batches` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:32` |
| GET | `/activity/batches/{batchId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:69` |
| GET | `/activity/batches/{batchId:guid}/presentation` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:85` |
| GET | `/activity/batches/{batchId:guid}/media-groups` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:101` |
| GET | `/activity/batches/{batchId:guid}/groups` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:119` |
| GET | `/activity/batches/{batchId:guid}/insights` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:132` |
| GET | `/activity/batches/{batchId:guid}/items` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:144` |
| GET | `/activity/batches/{batchId:guid}/events` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:165` |
| GET | `/activity/batches/{batchId:guid}/items/{assetId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:183` |
| GET | `/activity/people` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:200` |
| GET | `/activity/recent` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:237` |
| POST | `/activity/prune` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:256` |
| GET | `/activity/stats` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:276` |
| PUT | `/activity/retention` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:295` |
| GET | `/activity/by-types` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:322` |
| GET | `/activity/run/{runId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs:344` |
| GET | `/admin/provider-configs/{providerId}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AdminEndpoints.cs:14` |
| METHODS | `/admin/provider-configs/{providerId}/{configKey}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AdminEndpoints.cs:15` |
| DELETE | `/admin/provider-configs/{providerId}/{configKey}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AdminEndpoints.cs:17` |
| GET | `/ai/status` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:27` |
| GET | `/ai/models` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:47` |
| POST | `/ai/models/{role}/download` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:67` |
| DELETE | `/ai/models/{role}/download` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:96` |
| POST | `/ai/models/{role}/load` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:125` |
| POST | `/ai/models/{role}/unload` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:154` |
| GET | `/ai/config` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:188` |
| PUT | `/ai/config` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:199` |
| GET | `/ai/profile` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:220` |
| POST | `/ai/benchmark` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:230` |
| DELETE | `/ai/benchmark` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:242` |
| GET | `/ai/resources` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:256` |
| GET | `/ai/enrichment/progress` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/AiEndpoints.cs:274` |
| GET | `/access/applications/permissions` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:17` |
| GET | `/access/applications/presets` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:27` |
| GET | `/access/applications/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:37` |
| GET | `/access/applications/{applicationId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:47` |
| POST | `/access/applications/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:63` |
| PUT | `/access/applications/{applicationId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:79` |
| DELETE | `/access/applications/{applicationId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:97` |
| PUT | `/access/applications/{applicationId:guid}/permissions` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:113` |
| PUT | `/access/applications/{applicationId:guid}/client-bindings` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:131` |
| POST | `/access/applications/{applicationId:guid}/credentials` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:154` |
| POST | `/access/applications/{applicationId:guid}/credentials/{credentialId:guid}/rotate` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:173` |
| DELETE | `/access/applications/{applicationId:guid}/credentials/{credentialId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationEndpoints.cs:198` |
| GET | `/access/applications/{applicationId:guid}/webhooks/event-types` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationWebhookEndpoints.cs:17` |
| GET | `/access/applications/{applicationId:guid}/webhooks/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationWebhookEndpoints.cs:19` |
| POST | `/access/applications/{applicationId:guid}/webhooks/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationWebhookEndpoints.cs:22` |
| PUT | `/access/applications/{applicationId:guid}/webhooks/{webhookId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationWebhookEndpoints.cs:28` |
| POST | `/access/applications/{applicationId:guid}/webhooks/{webhookId:guid}/rotate` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationWebhookEndpoints.cs:32` |
| DELETE | `/access/applications/{applicationId:guid}/webhooks/{webhookId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ApplicationWebhookEndpoints.cs:38` |
| GET | `/auth/bootstrap/status` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:22` |
| POST | `/auth/login` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:27` |
| POST | `/auth/external-transactions` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:63` |
| POST | `/auth/external-session` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:97` |
| POST | `/auth/external-link` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:138` |
| POST | `/auth/invitations/accept` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:191` |
| POST | `/auth/session/validate` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:206` |
| GET | `/auth/sessions` | RequireAuthorization(AuthPolicies.HumanSelfService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:212` |
| DELETE | `/auth/sessions/{sessionId:guid}` | RequireAuthorization(AuthPolicies.HumanSelfService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:232` |
| POST | `/auth/password/change` | RequireAuthorization(AuthPolicies.HumanSelfService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:244` |
| POST | `/auth/password/recovery-codes` | RequireAuthorization(AuthPolicies.HumanSelfService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:256` |
| POST | `/auth/password/recover` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:267` |
| POST | `/auth/password/reset/begin` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:286` |
| POST | `/auth/password/reset/complete` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:300` |
| POST | `/auth/passkeys/login/options` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:315` |
| POST | `/auth/passkeys/login/complete` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:334` |
| POST | `/auth/passkeys/registration/options` | RequireAuthorization(AuthPolicies.HumanSelfService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:354` |
| POST | `/auth/passkeys/registration/complete` | RequireAuthorization(AuthPolicies.HumanSelfService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:362` |
| GET | `/auth/passkeys` | RequireAuthorization(AuthPolicies.HumanSelfService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:376` |
| DELETE | `/auth/passkeys/{credentialId}` | RequireAuthorization(AuthPolicies.HumanSelfService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:382` |
| PUT | `/auth/profiles/{profileId:guid}/pin` | RequireAuthorization(AuthPolicies.Administrator) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:418` |
| POST | `/auth/session/switch-profile` | RequireAuthorization(AuthPolicies.HumanSelfService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:425` |
| POST | `/auth/intercom-token` | RequireAuthorization(AuthPolicies.HumanSelfService) | Production | `src/MediaEngine.Api/Endpoints/AuthenticationEndpoints.cs:446` |
| GET | `//metadata/{entityId:guid}/canon-discrepancies` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CanonEndpoints.cs:21` |
| GET | `/assets/{id:guid}/capabilities` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CapabilityEndpoints.cs:13` |
| GET | `/capabilities/summary` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CapabilityEndpoints.cs:27` |
| GET | `/library/portraits/{portraitId:guid}` | RequireClientScope(ApplicationPermissionIds.ArtworkRead.Value) | Production | `src/MediaEngine.Api/Endpoints/CharacterEndpoints.cs:36` |
| GET | `/library/characters/{fictionalEntityId:guid}/portraits` | RequireClientScope(ApplicationPermissionIds.ArtworkRead.Value) | Production | `src/MediaEngine.Api/Endpoints/CharacterEndpoints.cs:99` |
| PUT | `/library/characters/{fictionalEntityId:guid}/portraits/{portraitId:guid}/default` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CharacterEndpoints.cs:132` |
| GET | `/library/persons/{personId:guid}/character-roles` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/CharacterEndpoints.cs:156` |
| GET | `/library/universes/{universeQid}/characters` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/CharacterEndpoints.cs:184` |
| GET | `/library/assets/{entityId}` | RequireClientScope(ApplicationPermissionIds.MetadataRead.Value) | Production | `src/MediaEngine.Api/Endpoints/CharacterEndpoints.cs:248` |
| POST | `/library/enrichment/universe/trigger` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CharacterEndpoints.cs:269` |
| POST | `/api/v1/oauth/device_authorization` | AllowAnonymous | Production | `src/MediaEngine.Api/Endpoints/ClientAuthorizationEndpoints.cs:18` |
| POST | `/api/v1/oauth/token` | AllowAnonymous, RequireAuthorization(AuthPolicies.DashboardInteractive) | Production | `src/MediaEngine.Api/Endpoints/ClientAuthorizationEndpoints.cs:45` |
| GET | `/api/v1/pairing/review/{userCode}` | RequireAuthorization(AuthPolicies.DashboardInteractive) | Production | `src/MediaEngine.Api/Endpoints/ClientAuthorizationEndpoints.cs:78` |
| POST | `/api/v1/pairing/decision` | RequireAuthorization(AuthPolicies.DashboardInteractive), RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ClientAuthorizationEndpoints.cs:90` |
| GET | `/api/v1/devices/` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ClientAuthorizationEndpoints.cs:117` |
| GET | `/api/v1/devices/current` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ClientAuthorizationEndpoints.cs:126` |
| PUT | `/api/v1/devices/current/capabilities` | RequireAuthorization(AuthPolicies.Authenticated), RequireClientScope(ClientApiScopes.PlaybackWrite) | Production | `src/MediaEngine.Api/Endpoints/ClientAuthorizationEndpoints.cs:135` |
| DELETE | `/api/v1/devices/{deviceId:guid}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ClientAuthorizationEndpoints.cs:150` |
| GET | `/collections/{collectionId:guid}/series-manifest` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:55` |
| GET | `/collections/` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:72` |
| GET | `/collections/search` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:90` |
| GET | `/collections/parents` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:110` |
| GET | `/collections/{id:guid}/children` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:169` |
| GET | `/collections/{id:guid}/parent` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:188` |
| GET | `/collections/{id:guid}/related` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:220` |
| GET | `/collections/{collectionId:guid}/group-detail` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:327` |
| GET | `/collections/artist-group-detail` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:629` |
| GET | `/collections/artist-detail-by-name` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:823` |
| GET | `/collections/system-view-detail` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:978` |
| GET | `/collections/content-groups` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1289` |
| GET | `/collections/system-views` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1473` |
| GET | `/collections/managed` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1498` |
| GET | `/collections/catalog` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1516` |
| POST | `/collections/reconcile` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1535` |
| GET | `/collections/managed/counts` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1559` |
| GET | `/collections/media-lookup` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1580` |
| GET | `/collections/{id:guid}/summary` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1629` |
| GET | `/collections/{id:guid}/items` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1652` |
| POST | `/collections/{id:guid}/items` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1685` |
| DELETE | `/collections/{id:guid}/items/{itemId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1752` |
| PUT | `/collections/{id:guid}/items/reorder` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1794` |
| GET | `/collections/{id:guid}/artwork/{slot}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1839` |
| POST | `/collections/{id:guid}/artwork/{slot}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1883` |
| DELETE | `/collections/{id:guid}/artwork/{slot}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:1974` |
| PUT | `/collections/{id:guid}/enabled` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2020` |
| PUT | `/collections/{id:guid}/featured` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2051` |
| GET | `/collections/resolve/{id:guid}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2084` |
| GET | `/collections/resolve/by-name` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2169` |
| GET | `/collections/by-location/{location}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2219` |
| POST | `/collections/preview` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2270` |
| POST | `/collections/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2316` |
| PUT | `/collections/{id:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2469` |
| DELETE | `/collections/{id:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2593` |
| GET | `/collections/field-values/{field}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2633` |
| GET | `/collections/entity-field-values/{field}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2649` |
| GET | `/collections/{id:guid}/placements` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2672` |
| PUT | `/collections/{id:guid}/placements` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs:2693` |
| GET | `/personal-media/galleries` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionPersonalMediaEndpoints.cs:14` |
| GET | `/{id:guid}/personal-media` | RequireClientScope(ApplicationPermissionIds.CollectionsRead.Value) | Production | `src/MediaEngine.Api/Endpoints/CollectionPersonalMediaEndpoints.cs:33` |
| POST | `/{id:guid}/personal-media` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionPersonalMediaEndpoints.cs:67` |
| PUT | `/{id:guid}/personal-media/{sourceId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionPersonalMediaEndpoints.cs:92` |
| DELETE | `/{id:guid}/personal-media/{sourceId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/CollectionPersonalMediaEndpoints.cs:118` |
| POST | `/debug/lookup` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/DebugEndpoints.cs:32` |
| POST | `/debug/search` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/DebugEndpoints.cs:200` |
| POST | `/debug/enrich` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/DebugEndpoints.cs:264` |
| POST | `/debug/enrich-universe` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/DebugEndpoints.cs:418` |
| POST | `/metadata/pass2/trigger` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/DeferredEnrichmentEndpoints.cs:22` |
| GET | `/metadata/pass2/status` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/DeferredEnrichmentEndpoints.cs:37` |
| GET | `/api/v1/details/{entityType}/{id:guid}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/DetailEndpoints.cs:22` |
| PUT | `/api/v1/details/{entityType}/{id:guid}/sequence-default` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/DetailEndpoints.cs:77` |
| GET | `/api/v1/display/home` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs:20` |
| GET | `/api/v1/display/browse` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs:27` |
| GET | `/api/v1/display/continue` | RequireClientScope(ClientApiScopes.ProgressRead) | Production | `src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs:66` |
| GET | `/api/v1/display/contributor-shelves` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs:82` |
| GET | `/api/v1/display/search` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs:89` |
| GET | `/api/v1/display/shelves/{shelfKey}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs:103` |
| GET | `/api/v1/display/groups/{groupId:guid}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs:136` |
| GET | `/ingestion/refresh-schedule/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/EnrichmentRefreshEndpoints.cs:17` |
| GET | `/ingestion/refresh-schedule/{entityType}/{entityId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/EnrichmentRefreshEndpoints.cs:32` |
| POST | `/ingestion/refresh-schedule/{entityType}/{entityId:guid}/run-now` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/EnrichmentRefreshEndpoints.cs:49` |
| GET/HEAD | `/stream/hls/{grant}/{packageId:guid}/{**resourcePath}` | AllowAnonymous | Production | `src/MediaEngine.Api/Endpoints/HlsStreamEndpoints.cs:12` |
| GET | `/ingestion/operations` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:27` |
| GET | `/ingestion/presentation` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:39` |
| GET | `/ingestion/media-groups` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:54` |
| GET | `/ingestion/recent-additions` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:68` |
| GET | `/ingestion/batches/{batchId:guid}/media-groups/{groupId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:86` |
| GET | `/ingestion/batches/{batchId:guid}/media-groups/{groupId:guid}/children` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:101` |
| POST | `/ingestion/assets/{assetId:guid}/reread-metadata` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:117` |
| POST | `/ingestion/scan` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:141` |
| POST | `/ingestion/library-scan` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:185` |
| GET | `/ingestion/watch-folder` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:230` |
| POST | `/ingestion/rescan` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:284` |
| POST | `/ingestion/reconcile` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:340` |
| GET | `/ingestion/batches` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:362` |
| GET | `/ingestion/batches/attention-count` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:376` |
| GET | `/ingestion/batches/{id:guid}/items` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:388` |
| GET | `/ingestion/batches/{id:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:431` |
| POST | `/ingestion/upload` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/IngestionEndpoints.cs:447` |
| GET | `/library/items/editor-suggestions/{field}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs:43` |
| PUT | `/library/items/{entityId:guid}/preferences` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs:63` |
| PUT | `/library/items/{entityId:guid}/display-overrides` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs:180` |
| POST | `/library/items/{entityId:guid}/canonical-search` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs:269` |
| POST | `/library/items/{entityId:guid}/canonical-apply` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs:385` |
| GET | `/library/items/{entityId:guid}/editor-preferences/{profileId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs:616` |
| PUT | `/library/items/{entityId:guid}/editor-preferences/{profileId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs:637` |
| POST | `/library/items/{entityId:guid}/retail-match` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs:718` |
| POST | `/library/items/{entityId:guid}/wikidata-match` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs:1139` |
| GET | `/library/overview` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs:30` |
| GET | `/library/works` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs:87` |
| POST | `/library/batch-edit/preview` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs:136` |
| POST | `/library/batch-edit` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs:194` |
| GET | `/library/universe-candidates` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs:295` |
| POST | `/library/universe-candidates/{workId:guid}/accept` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs:317` |
| POST | `/library/universe-candidates/{workId:guid}/reject` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs:354` |
| POST | `/library/universe-candidates/batch-accept` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs:387` |
| GET | `/library/universe-unlinked` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs:458` |
| POST | `/library/universe-assign` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs:470` |
| GET | `/library/items` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:33` |
| GET | `/library/items/{entityId}/detail` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:67` |
| GET | `/library/items/counts` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:84` |
| GET | `/library/items/state-counts` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:91` |
| GET | `/library/items/type-counts` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:101` |
| POST | `/library/items/{entityId}/apply-match` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:108` |
| POST | `/library/items/{entityId}/create-manual` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:228` |
| DELETE | `/library/items/{entityId}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:268` |
| POST | `/library/items/{entityId}/reject` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:298` |
| POST | `/library/items/batch/approve` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:345` |
| POST | `/library/items/batch/delete` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:369` |
| POST | `/library/items/batch/reject` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:421` |
| POST | `/library/items/{entityId:guid}/recover` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:480` |
| POST | `/library/items/{entityId:guid}/provisional` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:524` |
| GET | `/library/items/{entityId:guid}/history` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/LibraryItemEndpoints.cs:593` |
| GET | `/libraries/view-summary` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/LibraryMutationEndpoints.cs:19` |
| POST | `/libraries/mutations` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/LibraryMutationEndpoints.cs:25` |
| POST | `/settings/libraries/{libraryId:guid}/reorganization/plan` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/LibraryReorganizationEndpoints.cs:16` |
| POST | `/settings/libraries/{libraryId:guid}/reorganization/execute` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/LibraryReorganizationEndpoints.cs:40` |
| GET | `/maintenance/retag-sweep/state` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MaintenanceEndpoints.cs:25` |
| POST | `/maintenance/retag-sweep/apply` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MaintenanceEndpoints.cs:45` |
| POST | `/maintenance/retag-sweep/run-now` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MaintenanceEndpoints.cs:60` |
| POST | `/maintenance/retag-sweep/retry/{assetId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MaintenanceEndpoints.cs:75` |
| POST | `/maintenance/initial-sweep/run` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MaintenanceEndpoints.cs:99` |
| POST | `/maintenance/storage/run` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MaintenanceEndpoints.cs:113` |
| GET | `/metadata/claims/{entityId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:69` |
| GET | `/metadata/conflicts` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:84` |
| METHODS | `/metadata/lock-claim` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:104` |
| POST | `/metadata/hydrate/{entityId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:197` |
| POST | `/metadata/search` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:256` |
| PUT | `/metadata/{entityId:guid}/override` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:342` |
| POST | `/metadata/{entityId:guid}/reclassify` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:464` |
| GET | `/metadata/{entityId:guid}/editor-context` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:597` |
| GET | `/metadata/{entityId:guid}/artwork/{scopeId}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:660` |
| POST | `/metadata/{entityId:guid}/artwork/{scopeId}/refresh-provider` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:701` |
| GET | `/metadata/{entityId:guid}/artwork` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:801` |
| POST | `/metadata/{entityId:guid}/cover` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:883` |
| POST | `/metadata/{entityId:guid}/artwork/{scopeId}/{assetType}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:990` |
| POST | `/metadata/{entityId:guid}/artwork/{scopeId}/{assetType}/from-url` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1119` |
| POST | `/metadata/{entityId:guid}/artwork/{assetType}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1256` |
| PUT | `/metadata/artwork/{variantId:guid}/preferred` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1357` |
| DELETE | `/metadata/artwork/{variantId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1400` |
| GET | `/metadata/wikidata-test` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1484` |
| POST | `/metadata/search-all` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1572` |
| GET | `/metadata/{entityId:guid}/search-cache` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1692` |
| PUT | `/metadata/{entityId:guid}/search-cache` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1709` |
| GET | `/metadata/canonical/{entityId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1734` |
| POST | `/metadata/{entityId:guid}/cover-from-url` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1821` |
| POST | `/metadata/labels/resolve` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1926` |
| GET | `/metadata/{qid}/aliases` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.cs:1959` |
| GET | `/{entityId:guid}/navigator` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.MediaEditorNavigator.cs:16` |
| GET | `/{entityId:guid}/membership-suggestions` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.MediaEditorNavigator.cs:32` |
| POST | `/{entityId:guid}/membership-preview` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.MediaEditorNavigator.cs:50` |
| POST | `/{entityId:guid}/membership-apply` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/MetadataEndpoints.MediaEditorNavigator.cs:67` |
| GET | `/settings/network` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:19` |
| PUT | `/settings/network` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:26` |
| GET | `/network/status` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:70` |
| GET | `/network/readiness` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:76` |
| POST | `/network/tests/local` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:86` |
| POST | `/network/tests/remote` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:92` |
| POST | `/network/bandwidth-test` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:98` |
| POST | `/network/port-change/check` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:105` |
| POST | `/network/port-change/apply` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:113` |
| POST | `/network/router/renew` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:156` |
| POST | `/network/reset` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/NetworkEndpoints.cs:169` |
| GET | `/operations/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/OperationsEndpoints.cs:18` |
| GET | `/operations/{id:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/OperationsEndpoints.cs:33` |
| GET | `/operations/summary` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/OperationsEndpoints.cs:57` |
| POST | `/operations/{id:guid}/retry` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/OperationsEndpoints.cs:69` |
| POST | `/operations/{id:guid}/cancel` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/OperationsEndpoints.cs:87` |
| GET | `/persons/{id:guid}/editor` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:31` |
| PUT | `/persons/{id:guid}/editor` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:53` |
| GET | `/persons/{id:guid}/artwork` | RequireClientScope(ClientApiScopes.ArtworkRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:85` |
| POST | `/persons/{id:guid}/artwork/{assetType}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:144` |
| GET | `/persons/{id:guid}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:234` |
| GET | `/persons/{id:guid}/aliases` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:294` |
| GET | `/persons/{id:guid}/headshot` | RequireClientScope(ClientApiScopes.ArtworkRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:313` |
| GET | `/persons/by-collection/{collectionId:guid}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:407` |
| GET | `/persons/by-work/{workId:guid}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:424` |
| GET | `/persons/{id:guid}/library-credits` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:443` |
| GET | `/persons/{id:guid}/works` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:466` |
| GET | `/persons/role-counts` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:507` |
| GET | `/persons/presence` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:522` |
| GET | `/persons/` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/PersonEndpoints.cs:547` |
| GET | `/api/v1/playback/{assetId:guid}/manifest` | RequireClientScope(ClientApiScopes.PlaybackRead) | Production | `src/MediaEngine.Api/Endpoints/PlaybackEndpoints.cs:35` |
| POST | `/api/v1/playback/{assetId:guid}/encode` | RequireClientScope(ClientApiScopes.DownloadsWrite) | Production | `src/MediaEngine.Api/Endpoints/PlaybackEndpoints.cs:86` |
| GET | `/api/v1/playback/encode/jobs` | RequireClientScope(ClientApiScopes.DownloadsRead) | Production | `src/MediaEngine.Api/Endpoints/PlaybackEndpoints.cs:116` |
| POST | `/api/v1/playback/encode/jobs/{jobId:guid}/cancel` | RequireClientScope(ClientApiScopes.DownloadsWrite) | Production | `src/MediaEngine.Api/Endpoints/PlaybackEndpoints.cs:138` |
| GET | `/api/v1/playback/diagnostics` | RequireClientScope(ClientApiScopes.PlaybackRead) | Production | `src/MediaEngine.Api/Endpoints/PlaybackEndpoints.cs:165` |
| GET | `/api/v1/playback/{assetId:guid}/offline/{variantId:guid}` | RequireClientScope(ClientApiScopes.DownloadsRead) | Production | `src/MediaEngine.Api/Endpoints/PlaybackEndpoints.cs:177` |
| GET | `/playback/{assetId:guid}/segments` | RequireClientScope(ApplicationPermissionIds.PlaybackRead.Value) | Production | `src/MediaEngine.Api/Endpoints/PlaybackSegmentEndpoints.cs:17` |
| POST | `/playback/{assetId:guid}/segments/detect` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PlaybackSegmentEndpoints.cs:30` |
| PUT | `/playback/{assetId:guid}/segments/{segmentId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PlaybackSegmentEndpoints.cs:44` |
| DELETE | `/playback/{assetId:guid}/segments/{segmentId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PlaybackSegmentEndpoints.cs:72` |
| GET | `/api/v1/playback/sessions` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PlaybackTelemetryEndpoints.cs:17` |
| GET | `/api/v1/playback/history` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PlaybackTelemetryEndpoints.cs:28` |
| GET | `/api/v1/analytics/playback` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PlaybackTelemetryEndpoints.cs:43` |
| GET | `/api/v1/player/capabilities` | RequireClientScope(ClientApiScopes.PlaybackRead) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:24` |
| GET | `/api/v1/player/state` | RequireClientScope(ClientApiScopes.QueueRead) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:31` |
| POST | `/api/v1/player/queue/replace` | RequireClientScope(ClientApiScopes.QueueWrite) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:48` |
| POST | `/api/v1/player/queue/items` | RequireClientScope(ClientApiScopes.QueueWrite) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:73` |
| METHODS | `/api/v1/player/queue/order` | RequireClientScope(ClientApiScopes.QueueWrite) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:99` |
| DELETE | `/api/v1/player/queue/items/{queueItemId:guid}` | RequireClientScope(ClientApiScopes.QueueWrite) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:120` |
| DELETE | `/api/v1/player/queue` | RequireClientScope(ClientApiScopes.QueueWrite) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:154` |
| POST | `/api/v1/player/command` | RequireClientScope(ClientApiScopes.PlaybackWrite) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:187` |
| POST | `/api/v1/player/heartbeat` | RequireClientScope(ClientApiScopes.ProgressWrite) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:201` |
| POST | `/api/v1/player/session/takeover` | RequireClientScope(ClientApiScopes.PlaybackWrite) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:221` |
| GET | `/api/v1/player/audiobooks/{workId:guid}/history` | RequireClientScope(ClientApiScopes.ProgressRead) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:242` |
| GET | `/api/v1/player/audiobooks/{workId:guid}/bookmarks` | RequireClientScope(ClientApiScopes.ProgressRead) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:263` |
| POST | `/api/v1/player/audiobooks/{workId:guid}/bookmarks` | RequireClientScope(ClientApiScopes.ProgressWrite) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:280` |
| DELETE | `/api/v1/player/audiobooks/bookmarks/{bookmarkId:guid}` | RequireClientScope(ClientApiScopes.ProgressWrite) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:303` |
| GET | `/api/v1/player/audiobooks/{workId:guid}/chapter-overrides` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:320` |
| POST | `/api/v1/player/audiobooks/{workId:guid}/chapter-overrides` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:342` |
| DELETE | `/api/v1/player/audiobooks/{workId:guid}/chapter-overrides/{assetId:guid}/{chapterIndex:int}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PlayerEndpoints.cs:369` |
| GET | `/plugin-services/operations` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/PluginApplicationServiceEndpoints.cs:19` |
| POST | `/plugin-services/{operationId}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/PluginApplicationServiceEndpoints.cs:25` |
| GET | `/plugins` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:21` |
| GET | `/plugins/approved` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:27` |
| GET | `/plugins/{pluginId}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:33` |
| POST | `/plugins/{pluginId}/enable` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:42` |
| POST | `/plugins/{pluginId}/disable` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:51` |
| PUT | `/plugins/{pluginId}/settings` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:60` |
| GET | `/plugins/{pluginId}/manifest` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:72` |
| PUT | `/plugins/{pluginId}/manifest` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:87` |
| DELETE | `/plugins/{pluginId}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:110` |
| POST | `/plugins/{pluginId}/health` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:126` |
| GET | `/plugins/{pluginId}/jobs` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:166` |
| POST | `/plugins/jobs/segment-detection/run` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/PluginEndpoints.cs:178` |
| PUT | `/profiles/{id:guid}/experience` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:42` |
| GET | `/profiles/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:49` |
| GET | `/profiles/{id:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:68` |
| GET | `/profiles/{id:guid}/taste` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:84` |
| GET | `/profiles/{id:guid}/overview` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:105` |
| GET | `/profiles/{id:guid}/settings/playback` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:124` |
| GET | `/profiles/{id:guid}/settings/view` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:145` |
| PUT | `/profiles/{id:guid}/settings/view` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:165` |
| PUT | `/profiles/{id:guid}/settings/playback` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:198` |
| GET | `/profiles/{id:guid}/sequence-preferences/missing-items` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:225` |
| PUT | `/profiles/{id:guid}/sequence-preferences/missing-items` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:257` |
| DELETE | `/profiles/{id:guid}/sequence-preferences/missing-items` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:292` |
| GET | `/profiles/{id:guid}/avatar` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:324` |
| POST | `/profiles/{id:guid}/avatar` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:349` |
| DELETE | `/profiles/{id:guid}/avatar` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProfileEndpoints.cs:358` |
| GET | `/api/v1/progress/{assetId:guid}` | RequireAuthorization(AuthPolicies.Authenticated), RequireClientScope(ClientApiScopes.ProgressRead) | Production | `src/MediaEngine.Api/Endpoints/ProgressEndpoints.cs:28` |
| PUT | `/api/v1/progress/{assetId:guid}` | RequireAuthorization(AuthPolicies.Authenticated), RequireClientScope(ClientApiScopes.ProgressWrite) | Production | `src/MediaEngine.Api/Endpoints/ProgressEndpoints.cs:44` |
| GET | `/api/v1/progress/recent` | RequireAuthorization(AuthPolicies.Authenticated), RequireClientScope(ClientApiScopes.ProgressRead) | Production | `src/MediaEngine.Api/Endpoints/ProgressEndpoints.cs:77` |
| GET | `/api/v1/progress/journey` | RequireAuthorization(AuthPolicies.Authenticated), RequireClientScope(ClientApiScopes.ProgressRead) | Production | `src/MediaEngine.Api/Endpoints/ProgressEndpoints.cs:92` |
| GET | `/api/v1/progress/status/{entityType}/{targetId:guid}` | RequireAuthorization(AuthPolicies.Authenticated), RequireClientScope(ClientApiScopes.ProgressRead) | Production | `src/MediaEngine.Api/Endpoints/ProgressEndpoints.cs:110` |
| POST | `/api/v1/progress/status` | RequireAuthorization(AuthPolicies.Authenticated), RequireClientScope(ClientApiScopes.ProgressWrite) | Production | `src/MediaEngine.Api/Endpoints/ProgressEndpoints.cs:122` |
| POST | `/api/v1/progress/status/{commandId:guid}/undo` | RequireAuthorization(AuthPolicies.Authenticated), RequireClientScope(ClientApiScopes.ProgressWrite) | Production | `src/MediaEngine.Api/Endpoints/ProgressEndpoints.cs:137` |
| GET | `/api/v1/progress/status/{entityType}/{targetId:guid}/history` | RequireAuthorization(AuthPolicies.Authenticated), RequireClientScope(ClientApiScopes.ProgressRead) | Production | `src/MediaEngine.Api/Endpoints/ProgressEndpoints.cs:150` |
| GET | `/providers/catalogue` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ProviderCatalogueEndpoints.cs:31` |
| GET | `/read/{assetId:guid}/metadata` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/ReadEndpoints.cs:28` |
| GET | `/read/{assetId:guid}/toc` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/ReadEndpoints.cs:65` |
| GET | `/read/{assetId:guid}/chapter/{index:int}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/ReadEndpoints.cs:96` |
| GET | `/read/{assetId:guid}/resource/{**path}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/ReadEndpoints.cs:145` |
| GET | `/read/{assetId:guid}/search` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/ReadEndpoints.cs:187` |
| GET | `/read/resolve/{workId:guid}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/ReadEndpoints.cs:229` |
| GET | `/reader/{assetId:guid}/bookmarks` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs:24` |
| POST | `/reader/{assetId:guid}/bookmarks` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs:39` |
| DELETE | `/reader/bookmarks/{id:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs:66` |
| GET | `/reader/{assetId:guid}/highlights` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs:100` |
| POST | `/reader/{assetId:guid}/highlights` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs:115` |
| PUT | `/reader/highlights/{id:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs:145` |
| DELETE | `/reader/highlights/{id:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs:178` |
| GET | `/reader/{assetId:guid}/statistics` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs:212` |
| PUT | `/reader/{assetId:guid}/statistics` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReaderEndpoints.cs:237` |
| POST | `/reports/` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReportEndpoints.cs:21` |
| GET | `/reports/entity/{entityId:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReportEndpoints.cs:70` |
| POST | `/reports/{activityId:long}/resolve` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReportEndpoints.cs:98` |
| POST | `/reports/{activityId:long}/dismiss` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReportEndpoints.cs:121` |
| GET | `/review/pending` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReviewEndpoints.cs:35` |
| GET | `/review/count` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReviewEndpoints.cs:50` |
| GET | `/review/{id:guid}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReviewEndpoints.cs:63` |
| POST | `/review/{id:guid}/resolve` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReviewEndpoints.cs:78` |
| POST | `/review/{id:guid}/dismiss` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReviewEndpoints.cs:229` |
| POST | `/review/{id:guid}/skip-universe` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/ReviewEndpoints.cs:304` |
| POST | `/search/universe` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SearchEndpoints.cs:25` |
| POST | `/search/retail/detail` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SearchEndpoints.cs:44` |
| POST | `/search/retail` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SearchEndpoints.cs:111` |
| POST | `/search/resolve` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SearchEndpoints.cs:139` |
| GET | `/settings/server-folders/roots` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/ServerFolderEndpoints.cs:17` |
| POST | `/settings/server-folders/browse` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/ServerFolderEndpoints.cs:23` |
| POST | `/settings/server-folders/validate` | RequireAdmin | Production | `src/MediaEngine.Api/Endpoints/ServerFolderEndpoints.cs:41` |
| GET | `/settings/security/auth` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:108` |
| PUT | `/settings/security/auth` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:118` |
| PUT | `/settings/security/auth/providers/{providerId}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:202` |
| DELETE | `/settings/security/auth/providers/{providerId}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:249` |
| GET | `/settings/transcoding` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:285` |
| PUT | `/settings/transcoding` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:294` |
| GET | `/settings/libraries` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:345` |
| PUT | `/settings/libraries` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:354` |
| POST | `/settings/test-path` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:404` |
| GET | `/settings/providers/health` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:458` |
| PUT | `/settings/providers/{name}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:481` |
| GET | `/settings/providers` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:508` |
| GET | `/settings/organization-template` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:543` |
| POST | `/settings/organization-template/preview` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:569` |
| PUT | `/settings/organization-template` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:616` |
| POST | `/settings/providers/{name}/credentials/test` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:674` |
| PUT | `/settings/providers/{name}/credentials` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:687` |
| DELETE | `/settings/providers/{name}/credentials` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:700` |
| POST | `/settings/providers/{name}/test` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:715` |
| POST | `/settings/providers/{name}/sample` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:836` |
| PUT | `/settings/providers/{name}/config` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:918` |
| DELETE | `/settings/providers/{name}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1020` |
| GET | `/settings/hydration` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1059` |
| PUT | `/settings/hydration` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1070` |
| GET | `/settings/pipelines` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1083` |
| GET | `/settings/pipelines/defaults` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1093` |
| PUT | `/settings/pipelines` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1112` |
| GET | `/settings/media-types` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1125` |
| PUT | `/settings/media-types` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1137` |
| POST | `/settings/media-types/add` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1172` |
| DELETE | `/settings/media-types/{key}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1207` |
| POST | `/settings/providers/{name}/icon` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1239` |
| GET | `/settings/providers/{name}/icon` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1291` |
| GET | `/settings/server-general` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1329` |
| PUT | `/settings/server-general` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1352` |
| GET | `/settings/catalog` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs:1382` |
| GET | `/setup/v1/status` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:24` |
| POST | `/setup/v1/begin` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:28` |
| POST | `/setup/v1/preflight` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:37` |
| POST | `/setup/v1/administrator` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:54` |
| POST | `/setup/v1/media-locations/validate` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:84` |
| GET | `/setup/v1/libraries` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:100` |
| PUT | `/setup/v1/libraries` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:112` |
| GET | `/setup/v1/server-folders/roots` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:147` |
| POST | `/setup/v1/server-folders/browse` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:159` |
| POST | `/setup/v1/server-folders/validate` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:172` |
| PUT | `/setup/v1/providers/{name}/credentials` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:185` |
| POST | `/setup/v1/providers/{name}/credentials/test` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:198` |
| POST | `/setup/v1/steps/{stepKey}` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:211` |
| POST | `/setup/v1/restore/upload` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:237` |
| POST | `/setup/v1/restore/{operationId:guid}/confirm` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:271` |
| GET | `/setup/v1/readiness` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:299` |
| POST | `/setup/v1/complete` | RequireAuthorization(AuthPolicies.DashboardService) | Production | `src/MediaEngine.Api/Endpoints/SetupEndpoints.cs:311` |
| GET | `/stream/{assetId:guid}` | RequireClientScope(ClientApiScopes.PlaybackRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:64` |
| GET | `/stream/artwork/{variantId:guid}` | RequireClientScope(ClientApiScopes.ArtworkRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:125` |
| GET | `/stream/entity/{entityType}/{entityId:guid}/cover` | RequireClientScope(ClientApiScopes.ArtworkRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:189` |
| GET | `/stream/{assetId:guid}/cover` | RequireClientScope(ClientApiScopes.ArtworkRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:252` |
| GET | `/stream/{assetId:guid}/text-tracks` | RequireClientScope(ClientApiScopes.PlaybackRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:285` |
| GET | `/stream/{assetId:guid}/text-tracks/{trackId:guid}` | RequireClientScope(ClientApiScopes.PlaybackRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:322` |
| POST | `/stream/{assetId:guid}/text-tracks/{trackId:guid}/preferred` | RequireClientScope(ClientApiScopes.PlaybackWrite) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:352` |
| POST | `/stream/{assetId:guid}/text-tracks/import` | RequireClientScope(ClientApiScopes.PlaybackWrite) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:379` |
| GET | `/stream/{assetId:guid}/lyrics` | RequireClientScope(ClientApiScopes.PlaybackRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:439` |
| GET | `/stream/{assetId:guid}/subtitles` | RequireClientScope(ClientApiScopes.PlaybackRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:460` |
| POST | `/stream/{assetId:guid}/text-tracks/refresh` | RequireClientScope(ClientApiScopes.PlaybackWrite) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:482` |
| GET | `/stream/{assetId:guid}/cover-thumb` | RequireClientScope(ClientApiScopes.ArtworkRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:566` |
| GET | `/stream/{assetId:guid}/background` | RequireClientScope(ClientApiScopes.ArtworkRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:599` |
| GET | `/stream/{assetId:guid}/logo` | RequireClientScope(ClientApiScopes.ArtworkRead) | Production | `src/MediaEngine.Api/Endpoints/StreamEndpoints.cs:630` |
| GET | `/system/status` | AllowAnonymous | Production | `src/MediaEngine.Api/Endpoints/SystemEndpoints.cs:32` |
| GET | `/system/readiness` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SystemEndpoints.cs:48` |
| GET | `/system/activity-status` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SystemEndpoints.cs:57` |
| GET | `/system/watcher-status` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SystemEndpoints.cs:79` |
| POST | `/maintenance/sweep-orphan-assets` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SystemEndpoints.cs:96` |
| GET | `/system/backups` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SystemEndpoints.cs:109` |
| POST | `/system/backups` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SystemEndpoints.cs:117` |
| GET | `/system/backups/{fileName}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SystemEndpoints.cs:133` |
| POST | `/system/backups/validate` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SystemEndpoints.cs:157` |
| POST | `/system/backups/restore` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/SystemEndpoints.cs:188` |
| GET | `/timeline/{entityId:guid}` | RequireClientScope(ApplicationPermissionIds.MetadataEnrichmentRead.Value) | Production | `src/MediaEngine.Api/Endpoints/TimelineEndpoints.cs:29` |
| GET | `/timeline/{entityId:guid}/pipeline` | RequireClientScope(ApplicationPermissionIds.MetadataEnrichmentRead.Value) | Production | `src/MediaEngine.Api/Endpoints/TimelineEndpoints.cs:44` |
| GET | `/timeline/{entityId:guid}/event/{eventId:guid}/changes` | RequireClientScope(ApplicationPermissionIds.MetadataEnrichmentRead.Value) | Production | `src/MediaEngine.Api/Endpoints/TimelineEndpoints.cs:59` |
| POST | `/timeline/{entityId:guid}/rematch` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/TimelineEndpoints.cs:82` |
| GET | `/settings/ui/global` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs:60` |
| PUT | `/settings/ui/global` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs:73` |
| GET | `/settings/ui/device/{deviceClass}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs:90` |
| PUT | `/settings/ui/device/{deviceClass}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs:116` |
| GET | `/settings/ui/profile/{profileId}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs:143` |
| PUT | `/settings/ui/profile/{profileId}` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs:165` |
| GET | `/settings/ui/resolved` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs:188` |
| GET | `/settings/ui/library-preferences` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs:215` |
| GET | `/settings/ui/library-preferences/diagnostics` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs:226` |
| PUT | `/settings/ui/library-preferences` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UISettingsEndpoints.cs:248` |
| GET | `//universes` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:30` |
| GET | `//universe/{qid}` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:87` |
| GET | `//universe/{qid}/health` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:120` |
| GET | `//universe/{qid}/lore-delta` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:170` |
| GET | `//universe/{qid}/graph` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:183` |
| POST | `//universe/entity/{qid}/deep-enrich` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:358` |
| GET | `//universe/{qid}/paths` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:481` |
| GET | `//universe/{qid}/family-tree` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:501` |
| GET | `//universe/{qid}/cross-media` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:524` |
| GET | `//universe/{qid}/cast` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:542` |
| GET | `//universe/{qid}/adaptations` | RequireClientScope(ApplicationPermissionIds.LibraryRead.Value) | Production | `src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs:596` |
| GET | `/universe/{qid}/lore-sources` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UniverseLoreEndpoints.cs:18` |
| POST | `/universe/{qid}/lore-sources/discover` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UniverseLoreEndpoints.cs:30` |
| POST | `/universe/{qid}/lore-sources/manual` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UniverseLoreEndpoints.cs:42` |
| POST | `/universe/{qid}/lore-sources/{sourceId:guid}/approve` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UniverseLoreEndpoints.cs:72` |
| POST | `/universe/{qid}/lore-sources/{sourceId:guid}/reject` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UniverseLoreEndpoints.cs:97` |
| POST | `/universe/{qid}/lore/enrich` | fallback policy / middleware | Production | `src/MediaEngine.Api/Endpoints/UniverseLoreEndpoints.cs:122` |
| GET | `/view/places` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewDiscoveryEndpoints.cs:17` |
| GET | `/view/people` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewDiscoveryEndpoints.cs:47` |
| GET | `/view/scopes` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:24` |
| GET | `/view/preferences` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:66` |
| PUT | `/view/preferences` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:82` |
| GET | `/view/assets` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:119` |
| GET | `/view/folders` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:155` |
| PUT | `/view/folders/pin` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:196` |
| PUT | `/view/folders/timeline-policy` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:227` |
| POST | `/view/uploads` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:260` |
| GET | `/view/items/{id:guid}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:298` |
| GET | `/view/items/{id:guid}/content` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:309` |
| GET | `/view/items/{id:guid}/thumbnail` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:331` |
| POST | `/view/shared/contributions/preview` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:361` |
| POST | `/view/shared/contributions` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:377` |
| GET | `/view/shared/contributions` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:393` |
| GET | `/view/shared/contributions/{id:guid}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:412` |
| POST | `/view/shared/contributions/{id:guid}/cancel` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:426` |
| POST | `/view/shared/contributions/{id:guid}/decision` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:442` |
| POST | `/view/shared/contributions/{id:guid}/retry` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:459` |
| POST | `/view/shared/items/direct` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:475` |
| GET | `/view/share-targets` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:493` |
| GET | `/view/admin/profiles/{profileId:guid}/sources` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:517` |
| POST | `/view/admin/profiles/{profileId:guid}/sources` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:543` |
| PUT | `/view/admin/profiles/{profileId:guid}/sources/{sourceId:guid}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:607` |
| DELETE | `/view/admin/profiles/{profileId:guid}/sources/{sourceId:guid}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:631` |
| POST | `/view/admin/profiles/{profileId:guid}/reconcile` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:662` |
| GET | `/view/admin/shared/sources` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:672` |
| POST | `/view/admin/shared/sources` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:681` |
| PUT | `/view/admin/shared/sources/{sourceId:guid}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:728` |
| DELETE | `/view/admin/shared/sources/{sourceId:guid}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:751` |
| POST | `/view/admin/shared/reconcile` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:778` |
| GET | `/view/galleries` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:795` |
| POST | `/view/galleries` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:817` |
| GET | `/view/galleries/{id:guid}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:852` |
| PUT | `/view/galleries/{id:guid}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:861` |
| DELETE | `/view/galleries/{id:guid}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:881` |
| GET | `/view/galleries/{id:guid}/items` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:891` |
| POST | `/view/galleries/{id:guid}/items` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:902` |
| DELETE | `/view/galleries/{id:guid}/items` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:911` |
| PUT | `/view/galleries/{id:guid}/items/{itemId:guid}/position` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:921` |
| GET | `/view/galleries/{id:guid}/shares` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:933` |
| PUT | `/view/galleries/{id:guid}/shares` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:942` |
| PUT | `/view/items/{{id:guid}}/{name}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:1034` |
| POST | `/view/items/{{id:guid}}/{name}` | RequireAuthorization(AuthPolicies.Authenticated) | Production | `src/MediaEngine.Api/Endpoints/ViewEndpoints.cs:1045` |
| GET | `/works/{workId:guid}` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/WorkEndpoints.cs:19` |
| GET | `/works/{workId:guid}/editions` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/WorkEndpoints.cs:40` |
| GET | `/works/{workId:guid}/cast` | RequireClientScope(ClientApiScopes.LibraryRead) | Production | `src/MediaEngine.Api/Endpoints/WorkEndpoints.cs:61` |
| HEALTH | `/health` | RequireAuthorization(AuthPolicies.Administrator) | Production | `src/MediaEngine.Api/Program.cs:399` |
| HEALTH | `/health/live` | AllowAnonymous | Production | `src/MediaEngine.Api/Program.cs:400` |
| HEALTH | `/health/ready` | RequireAuthorization(AuthPolicies.Administrator) | Production | `src/MediaEngine.Api/Program.cs:404` |
| GET | `/.well-known/tuvima` | AllowAnonymous | Production | `src/MediaEngine.Web/Endpoints/ClientApiEdgeEndpoints.cs:16` |
| GET | `/pair` | fallback policy / middleware | Production | `src/MediaEngine.Web/Endpoints/ClientApiEdgeEndpoints.cs:26` |
| POST | `/pair` | fallback policy / middleware | Production | `src/MediaEngine.Web/Endpoints/ClientApiEdgeEndpoints.cs:55` |
| GET/HEAD/POST/PUT/PATCH/DELETE | `/api/v1/{**clientPath}` | AllowAnonymous | Production | `src/MediaEngine.Web/Endpoints/ClientApiEdgeEndpoints.cs:87` |
| HEALTH | `/health/live` | AllowAnonymous | Production | `src/MediaEngine.Web/Program.cs:318` |
| HEALTH | `/health/ready` | AllowAnonymous | Production | `src/MediaEngine.Web/Program.cs:322` |
| GET | `/_tuvima/remote-probe` | AllowAnonymous | Production | `src/MediaEngine.Web/Program.cs:332` |
| GET | `/culture/set` | AllowAnonymous | Production | `src/MediaEngine.Web/Program.cs:342` |
| GET/HEAD | `/engine-stream/{assetId:guid}` | RequireAuthorization | Production | `src/MediaEngine.Web/Program.cs:354` |
| GET/HEAD | `/engine-hls/{grant}/{packageId:guid}/{**resourcePath}` | RequireAuthorization | Production | `src/MediaEngine.Web/Program.cs:359` |
| GET/HEAD | `/engine-image/{**enginePath}` | RequireAuthorization | Production | `src/MediaEngine.Web/Program.cs:367` |
| GET | `/auth/external/{providerId}` | AllowAnonymous | Production | `src/MediaEngine.Web/Program.cs:379` |
| GET | `/auth/login` | AllowAnonymous | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:20` |
| POST | `/auth/login` | AllowAnonymous | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:51` |
| GET | `/auth/reset` | AllowAnonymous | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:149` |
| POST | `/auth/reset` | AllowAnonymous | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:160` |
| GET | `/auth/invite` | AllowAnonymous | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:170` |
| POST | `/auth/invite` | AllowAnonymous | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:180` |
| POST | `/auth/passkeys/login/options` | AllowAnonymous | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:192` |
| POST | `/auth/passkeys/login/complete` | AllowAnonymous | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:196` |
| POST | `/auth/passkeys/registration/options` | fallback policy / middleware | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:209` |
| POST | `/auth/passkeys/registration/complete` | fallback policy / middleware | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:212` |
| GET | `/account/security/external/{providerId}` | fallback policy / middleware | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:215` |
| POST | `/auth/logout` | fallback policy / middleware | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:230` |
| GET | `/account/security` | fallback policy / middleware | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:243` |
| POST | `/account/security` | fallback policy / middleware | Production | `src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs:254` |
| GET/HEAD | `/view-media/{grant}` | RequireAuthorization | Production | `src/MediaEngine.Web/Services/Integration/ViewMediaProxyEndpoint.cs:6` |

