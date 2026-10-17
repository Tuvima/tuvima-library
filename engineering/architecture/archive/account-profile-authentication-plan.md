---
title: "Account, Profile, and Authentication Plan"
summary: "Target architecture and implementation sequence for household accounts, profiles, recovery, external providers, passkeys, and administrator elevation."
audience: "developer"
category: "architecture"
product_area: "security"
tags:
  - "accounts"
  - "profiles"
  - "authentication"
  - "passkeys"
  - "oidc"
---

# Account, Profile, and Authentication Plan

## Product model

An **account** proves who may enter a Tuvima Library server. An account owns an
email address, authenticators, recovery methods, external sign-in identities,
device sessions, and an enabled/disabled state.

A **profile** is the identity used inside the library. It owns a display name,
avatar, role, content restrictions, preferences, history, progress, and personal
View space. One account may use multiple profiles, and a profile may be made
available to more than one account when an administrator explicitly grants it.

This separation provides the intended household behavior: one email/account can
open Dad, Mom, and Kids profiles. A remote family member receives their own
account and then sees only the profiles granted to that account. Administrators
may also create local-only accounts without a password; those accounts cannot
start a remote session and only expose their profiles from an already trusted
household/device session.

Email is a sign-in and notification address, never a display name or immutable
identity key. Profiles always choose their display names separately.

## Authentication methods

An enabled remote-capable account must have at least one usable authenticator:

- a local password;
- a passkey/WebAuthn credential; or
- a linked external identity.

Local-only accounts are the explicit exception. They have no remote sign-in
credential and must be created or assigned by an administrator.

Passwords use the ASP.NET Core Identity password hasher, throttled attempts,
security-stamp rotation, session revocation, recovery codes, and optional email
recovery. Passkeys are the preferred phishing-resistant method when client and
server origin requirements are satisfied.

## External provider implementation

Authentication configuration is an ordered `auth.external_providers` list. Each
provider has a stable local ID, protocol kind, display name, issuer, client ID,
secret supplied from the gitignored authentication secret overlay, scopes, and
protocol-specific endpoints or OIDC authority. Each provider gets its own
authentication scheme and callback path.

Initial provider support is:

| Provider | Protocol | Stable identity |
| --- | --- | --- |
| Google | OpenID Connect authorization-code flow | issuer + `sub` |
| Microsoft | OpenID Connect authorization-code flow | issuer + `sub`; retain tenant claims for policy |
| GitHub | OAuth 2.0 authorization-code flow with User API lookup | provider issuer + numeric user ID |
| Facebook | OAuth 2.0 authorization-code flow with Graph API lookup | provider issuer + app-scoped user ID |
| Generic | Discovery-based OpenID Connect | issuer + `sub` |

GitHub and Facebook must not be represented as generic OIDC providers. OAuth
handlers must fetch the authenticated user record before creating a Tuvima
session. OIDC handlers validate issuer, signature, audience, nonce, state, and
token lifetime. Authorization-code flows use PKCE where the provider supports
it. Callback URLs are fixed and must be served from the configured canonical
HTTPS origin for remote use.

External identities are stored on accounts with a unique
`(provider_id, issuer, subject)` key. Email and provider display name are cached
attributes only. Matching email addresses never merge or link accounts. A new
identity may be linked only while signed into the target account or while
redeeming a targeted, expiring invitation.

The sign-in page renders every enabled provider separately. Account Security
lists connected providers and prevents removal of the last remote authenticator
unless the account is intentionally converted to local-only. Authentication
Settings shows configuration state and callback URLs without returning secrets.

### Self-hosted provider registration

Local passwords, passkeys, and generic OIDC work without a Tuvima cloud account.
For direct Google, Microsoft, GitHub, or Facebook sign-in, a self-hosted server
administrator supplies a provider application registration and exact HTTPS
callback URL. A future optional Tuvima-hosted identity broker could remove that
setup burden, but it is a separate service, privacy, availability, and incident-
response commitment and is not required by the local-first design.

## Password recovery and email

Recovery codes and the host administrator command remain available even when
email is not configured. Email reset tokens are single-use, short-lived, stored
only as hashes, invalidate on credential/security-stamp changes, and return the
same public response whether an email exists or not.

Email delivery is an optional server adapter:

- SMTP with STARTTLS/TLS and credentials stored in the secret store;
- a transactional provider adapter added later; or
- no email, with recovery codes and host recovery clearly shown instead.

Tuvima does not run an unauthenticated local mail relay. Account Security
includes a send-test action and Authentication Settings reports delivery
readiness. A deployment is not required to use email-shaped usernames merely
to support local accounts; email is required only for accounts that choose
email/password sign-in or email recovery.

## Administrator elevation

Authentication establishes an account session. Authorization uses the active
profile. Administrative actions additionally require a recent elevation grant,
similar to `sudo`.

An administrator profile may configure a distinct administrative PIN and one or
more passkeys. Elevation can be satisfied by that PIN, a passkey/platform
authenticator such as Windows Hello, or the account password. External-provider
sign-in establishes the account session but does not elevate administrator
rights, because provider assurance and recent-authentication semantics vary. A
normal profile-selection PIN is not automatically an administrator PIN.

Elevation grants are bound to session, device, account, and active profile. They
expire after 30 minutes by default, are never extended merely by browsing, are
cleared on sign-out/profile switch/security change, and are audited. Sensitive
operations such as changing authentication providers, recovery methods, roles,
network exposure, backups, or deleting data may require immediate reauthentication
even within the normal elevation window.

## Settings information architecture

- **Profile**: display name, avatar, preferences, restrictions, history, and
  profile-selection PIN.
- **Account & Security**: email, password, passkeys, connected providers,
  recovery codes, and sessions.
- **Users & Access**: accounts, profile grants, roles, invitations, local-only
  accounts, and disabling access.
- **Authentication**: server sign-in policy, external provider registrations,
  callback readiness, email delivery, passkey origin, and elevation policy.

## Implementation sequence

1. **Provider-capable boundary (implemented)** — replace the single `auth.oidc` object with
   `auth.external_providers`, load secrets from `.secrets`, register one handler
   per provider, expose provider-safe settings, and bind identities by provider,
   issuer, and subject.
2. **Account cutover (implemented)** — introduce accounts and account/profile grants; move
   passwords, external identities, recovery codes, and sessions from profiles to
   accounts. This is a destructive pre-beta schema cutover with no legacy shim.
3. **Household and remote flows (implemented)** — make setup collect account email separately
   from the first profile name; add invitation acceptance, profile picker, and
   administrator-created local-only accounts.
4. **Account/Profile settings split (implemented)** — replace manual subject-ID linking with
   authorization-driven Connect/Disconnect actions and add account/session UI.
5. **Recovery delivery (implemented)** — add hashed email reset challenges, SMTP adapter,
   readiness/test UI, generic responses, throttling, and audit events.
6. **Passkeys (implemented)** — add WebAuthn registration/authentication, credential inventory,
   recovery handling, and platform-authenticator UI.
7. **Administrator elevation (implemented)** — add administrator PIN/passkey enrollment,
   elevation grants, protected policies/endpoints, prompts, timeout display, and
   audit coverage.
8. **Hardening (implemented)** — test duplicate emails, provider/tenant collisions, invitation
   theft, callback state/nonce/PKCE, unlinking the final authenticator, session
   invalidation, reverse-proxy origin handling, and elevation expiry.

## Acceptance criteria

- One email/password or passkey account can select multiple granted profiles.
- A remote account cannot discover or select an ungranted profile.
- A local-only account cannot authenticate from a fresh remote session.
- Display names are profile data and never derived as the account identifier.
- External identity lookup never uses email and cannot collide across issuers.
- Google/Microsoft OIDC and GitHub/Facebook OAuth callbacks produce the same
  internal verified-identity result.
- The server remains usable without SMTP or any social provider.
- Administrator endpoints require both an administrator profile and a current
  elevation grant where policy marks the operation as privileged.
- Secrets never appear in settings responses, logs, backups, or checked-in
  configuration.
