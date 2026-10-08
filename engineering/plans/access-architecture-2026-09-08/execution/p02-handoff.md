# P02 authority and identity handoff

## Frozen shared contracts

- `IRequestAuthorityResolver.ResolveAsync(HttpContext, CancellationToken)` returns the exact P01 `RequestAuthority` for Engine requests.
- `IAuthorizationEvaluator.EvaluateAsync(RequestAuthority, AuthorizationRequirement, ResourceAuthorizationContext?, CancellationToken)` is the common fail-closed decision point. `RequireAdministratorOrApplication(permission)` admits a valid effective human administrator or a valid Application with that available permission.
- `IAccountAccessMutationService` owns account, invitation, profile/grant/access, and administrator-protection changes. Account creation selects exactly one existing profile or creates one new default profile atomically. Managed profile creation always names its target account.
- `IApplicationRepository` owns all Application reads/writes, permissions, client bindings, credentials, usage touch, and delete. `ClientAccessIdentity` carries live token, device, account, grant, and Application state.
- `IAccountSignInMethodRepository` performs transactional final-sign-in-method removal for passkeys and external identities while counting password credentials in the same invariant.

## Routes and DI

- Accounts: `/access/accounts`, `/access/accounts/{accountId}`, `/access/accounts/{accountId}/access`, `/access/accounts/{accountId}/grants/{profileId}`, `/access/accounts/{accountId}/grants/{profileId}/admin-protection`.
- Profiles/libraries: `/access/profiles`, `/access/profiles/{profileId}`, `/access/libraries`.
- Self service/protection: `/access/self-service`, GET/DELETE `/access/self-service/external-logins[/{loginId}]`, GET/POST/DELETE `/access/admin-unlock`.
- Invitations: POST `/access/invitations` creates or reuses the same pending remote account and returns the one-time plaintext token.
- Applications: `/access/applications`, `/access/applications/permissions`, client bindings, permission replacement, and credential issue/rotate/revoke.
- Storage DI registers singleton account, identity, Application, client-authorization, and sign-in-method repositories. Authority resolver/evaluator, typed handlers, mutation services, and unlock services are scoped; invalidation and audit writers are singleton services over the uncached singleton repositories.

## Verified behavior

- Authority/API tests cover invalid human and delegated bindings, administrator overrides, registry availability/type/context, the bounded View administrator read exception, typed Access routes, and absence of arbitrary external-login POST.
- Native tests cover exact known-client binding at begin/exchange/validate/refresh, pending binding removal/reassignment/type change, live account/grant/Application versions, and administrator Application dynamic permissions.
- Storage tests cover fresh-only native seed/restart preservation, max eight/default/final usable administrator invariants, pending administrators without a sign-in method, atomic own-default profile creation, transactional sign-in-method removal, and View parent/Shared constraints.
- Identity tests cover hash-only credentials, invitation/recovery/session lifecycle, unlock invalidation on switch/revoke, and profile PIN without implicit account creation.

## Remaining dependent work

- P03 must replace remaining catalogue/resource Role decisions, including all retained Profile, Setup, collection, detail, playback, and settings paths, and close the mapped-endpoint inventory ratchet.
- P04 supplies View repository/services against the published scope schema and must retain scoped evaluator lifetimes.
- P05 consumes authenticated authority/capability projections; the old Web `Role` readers currently prevent the Contracts test project from compiling on this branch.
- P07 must supply callback-proven, short-lived, replay-safe external-link assertions and trusted local-entry eligibility. No caller-supplied external identity binding route is exposed.
