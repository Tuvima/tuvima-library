# P01 access schema handoff

Status: design contract only. P01 does not change the database, bootstrap, repositories, or runtime authorization. P02 owns the pre-beta schema cutover and must keep the identifiers and invariants below aligned with the shared Domain and HTTP contracts.

## Identity and grants

- Extend `accounts` with `is_administrator` and monotonic `authorization_version`. An email remains nullable for a truthful local-only account.
- Keep `account_profile_grants` identified by the existing `(account_id, profile_id)` compound key. Add `is_enabled`, `admin_enabled`, monotonic `authorization_version`, and the current default/granted timestamps. Do not introduce a grant GUID solely for authorization context.
- Add `account_feature_grants` keyed by `(account_id, feature_id)` and `account_library_grants` keyed by `(account_id, library_id)`. Only `read`, `watch`, `listen`, and `view` are valid feature IDs. Absence denies access.
- Store grant PIN material in a separate row keyed by `(account_id, profile_id)`: hash and hash parameters, unlock mode, optional fixed duration, failed-attempt count, lockout time, and monotonic `protection_version`. Hashes and attempt state never enter response DTOs.
- Persist grant unlocks against session, account, profile, and protection version with a server-enforced expiry or bounded lock-on-leave lease. Profile switch, sign-out, grant/protection mutation, or stale version invalidates them.

P02 transactions must enforce at most eight distinct granted or invited profiles per account, exactly one valid default among usable grants, `admin_enabled` only for administrator accounts, and preservation of the last usable administrator account/grant. Revocation must not delete personal originals or reassign Personal Space ownership; its reserved storage label remains administratively discoverable as orphaned state.

## Applications and delegated clients

- `applications`: identity, type, enabled/administrator flags, timestamps, last use, and monotonic `authorization_version`.
- `application_credentials`: credential identity and lifecycle only, keyed to Application; store only a verifier/hash plus created, expiry, last-used, and revoked timestamps. Plaintext is returned once by `ApplicationCredentialIssuedResponse`.
- `application_permission_grants`: compound key `(application_id, permission_id)`. Credential rotation and revocation do not modify these rows. Administrator Applications resolve all currently available registry definitions dynamically rather than copying a permanent wildcard grant.
- Native devices, pairing requests, access-token families, and refresh-token families gain `application_id`, `account_id`, and approved `profile_id`. Delegated consent remains a narrowing set and never replaces live Application permission grants or current account/profile authority.

Permission IDs are ordinal, case-sensitive strings. Built-ins cannot be replaced by plugin definitions. Plugin service IDs use `plugin.{lowercase-plugin-id}.{service}.{action}` and retained unloaded definitions are unavailable with a reason.

## Revocation and audit

Increment the narrowest authorization version in the same transaction as any authority mutation, then invoke `IAuthorizationInvalidationService` after commit for the affected account, compound grant, Application, or session. Cache entries and live token/unlock validation compare stored versions; invalidation is an optimization, not the source of truth.

Write `AuthorizationAuditEvent` for account status/administrator changes, profile grant/default/admin/PIN changes, feature/library changes, Application lifecycle and administrator changes, permission changes, and credential issue/rotate/revoke. Audit changes contain identifiers and before/after non-secret facts only. Never record PINs, hashes, plaintext credentials, session tokens, or provider secrets.
