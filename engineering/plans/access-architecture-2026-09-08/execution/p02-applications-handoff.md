# P02 Applications handoff

## Implemented slice

- `ApplicationRepository` uses GUID BLOB parameters for every Application and credential key.
- Application and permission writes are transactional. Permission changes, status changes,
  credential issue, credential rotation, and credential revocation advance the Application
  authorization version.
- Credential lookup returns one consistent SQLite snapshot and denies revoked, expired, and
  disabled Application credentials. Usage timestamps advance only for an active credential on
  an enabled Application.
- Rotation revokes the prior credential and inserts its replacement in one serialized write
  transaction. Concurrent rotations have one winner and do not alter permission grants.
- `ApplicationAdministrationService` accepts a server-resolved `RequestAuthority`, enforces the
  administrator-or-Application decision again at the service boundary, validates permission
  registration, availability, and Application type, invalidates authority after mutations, and
  writes secret-free audit events.
- Issuance creates 48 random bytes, returns the URL-safe plaintext once, and persists only its
  SHA-256 verifier. Normal Application and credential responses contain no plaintext or verifier.
- `ApplicationEndpoints` maps list/read/create/update/delete, registry/presets, permission
  replacement, native client bindings, and credential issue/rotate/revoke under `/applications`.
  Every route uses
  `RequireAdministratorOrApplication(identity.applications.write)`.
- Native client identifiers are exact, server-registered bindings available only to enabled
  UserClient Applications. Replacing bindings is atomic, collision-safe, audited, and advances
  the Application authorization version.

## Integration changes required in shared files

The P02 integrator should register `IApplicationRepository` with `ApplicationRepository`, register
`ApplicationAdministrationService`, and add `app.MapApplicationEndpoints();` to
`MapEngineEndpoints`.

The accepted request-authority services, evaluator, invalidation service, audit writer,
permission registry, and authorization handlers must also be registered once by their shared
owner.

Now that the route and backed service exist, change the built-in
`identity.applications.write` definition from unavailable to available. Keep the shared-policy
acceptance fixes identified by the P02 owner: an administrator Application must not override an
unavailable permission, and delegated authority must validate the current account, grant,
Application, consent, type, and user-context requirements.

The inbound credential authentication owner should hash the supplied one-time credential and use
`IApplicationRepository.FindApplicationCredentialAsync`; no legacy API-key role reader should be
used as a fallback.

## Verification

- `dotnet build src/MediaEngine.Storage/MediaEngine.Storage.csproj --no-restore`: passed.
- `dotnet build src/MediaEngine.Api/MediaEngine.Api.csproj --no-restore`: passed.
- Focused `ApplicationRepositoryTests`: 5 passed.
- Focused `ApplicationAdministrationServiceTests`: 9 passed.
- Existing success-response guardrail: its fixed route count expected 514 and observed 525,
  exactly the original 11 new Applications routes. The binding route brings this slice to 12;
  the shared guardrail count should be updated after all P02 endpoint additions are integrated.
- The already-reported SchemaMigrator rebuilt-index omission belongs to the schema integrator;
  this slice does not alter the schema, migrator, or that assertion.

## Product-owner summary

Administrators can manage external Applications, choose only permissions backed by real
services, and keep several independently revocable credentials per Application. A replacement
credential immediately invalidates the old one without changing what the Application may do,
and the secret is shown only once. Native clients must also be registered to the correct
Application instead of being trusted from a client-submitted name.
