---
title: "Configure External Authentication"
description: "Configure Google, Microsoft, GitHub, Facebook, or another OIDC/OAuth provider for a self-hosted Tuvima Library server."
audience: "administrator"
category: "guide"
product_area: "security"
tags:
  - "oidc"
  - "oauth"
  - "self-hosting"
---
External authentication is optional. Local password and recovery-code access
continues to work when no provider is configured.

Each self-hosted server needs an application registration at the provider and a
stable HTTPS Dashboard origin for remote callbacks. Register the callback shown
below, replacing the origin with the public Dashboard origin:

```text
https://library.example.com/signin-tuvima-{provider-id}
```

Provider IDs contain lowercase letters, numbers, and hyphens, begin with a
letter, and are 2–40 characters long. Callback paths are intentionally fixed so
an administrator can copy them into a provider console without ambiguity.

## Public configuration

Add providers to `auth.external_providers` in `config/core.json`. This file may
contain public client IDs but must not contain client secrets.

```json
{
  "auth": {
    "mode": "Hybrid",
    "localhost_bypass": false,
    "require_https_remote": true,
    "external_providers": [
      {
        "id": "google",
        "kind": "oidc",
        "enabled": true,
        "display_name": "Google",
        "authority": "https://accounts.google.com",
        "client_id": "YOUR_GOOGLE_CLIENT_ID",
        "scopes": ["openid", "profile", "email"]
      },
      {
        "id": "microsoft",
        "kind": "oidc",
        "enabled": true,
        "display_name": "Microsoft",
        "authority": "https://login.microsoftonline.com/common/v2.0",
        "client_id": "YOUR_MICROSOFT_CLIENT_ID",
        "scopes": ["openid", "profile", "email"]
      },
      {
        "id": "github",
        "kind": "oauth",
        "enabled": true,
        "display_name": "GitHub",
        "issuer": "https://github.com",
        "client_id": "YOUR_GITHUB_CLIENT_ID",
        "use_pkce": true,
        "scopes": ["read:user", "user:email"],
        "authorization_endpoint": "https://github.com/login/oauth/authorize",
        "token_endpoint": "https://github.com/login/oauth/access_token",
        "user_information_endpoint": "https://api.github.com/user",
        "id_claim": "id",
        "name_claim": "name",
        "email_claim": "email"
      }
    ]
  }
}
```

Use a tenant ID instead of `common` when Microsoft sign-in must be restricted to
one organization. Google and Microsoft use OIDC discovery. GitHub uses its OAuth
web flow and User API; it is not configured as OIDC.

Facebook uses the same `oauth` shape. Supply the current Facebook Login
authorization, token, and Graph `/me?fields=id,name,email` endpoints from the
Meta application dashboard, use `https://www.facebook.com` as the issuer, and
map `id`, `name`, and `email`. Keeping the Graph API version explicit in server
configuration avoids silently changing behavior when Meta retires a version.
PKCE defaults to enabled; disable `use_pkce` only when the chosen provider flow
explicitly does not support it.

## Private secrets

Copy `config/examples/auth-providers.secrets.example.json` to
`config/.secrets/auth-providers.json` on the server, then replace only the
secrets for configured providers:

```json
{
  "providers": {
    "google": { "client_secret": "YOUR_GOOGLE_CLIENT_SECRET" },
    "microsoft": { "client_secret": "YOUR_MICROSOFT_CLIENT_SECRET" },
    "github": { "client_secret": "YOUR_GITHUB_CLIENT_SECRET" }
  }
}
```

The provider key must match the public provider ID. The `.secrets` directory is
gitignored and excluded from Tuvima backups. Restrict this file to the operating-
system account that runs the Dashboard.

An enabled OAuth provider fails startup when its secret is missing. An OIDC
provider may omit a secret only when its provider registration explicitly
supports a public authorization-code client with PKCE.

## Identity linking

An external login succeeds only after the verified provider identity has been
linked to a Tuvima account. Tuvima keys that link by provider ID, issuer,
and immutable subject. It does not use an email-address match to link accounts.
This prevents an email reassignment or an unverified provider email from taking
over an existing library account.

Sign in with an existing account, open **Account & Security**, and choose the
configured provider's **Link** action. Tuvima links the immutable identity only
after that provider completes its own validated callback. Administrators never
type provider subject identifiers manually. The same page can disconnect a
provider, but Tuvima refuses to remove the account's final usable authenticator.

For a new remote family member, first create a targeted invitation under
**Users & Access → Accounts & Profile Grants**. After the invitation is accepted,
the family member can connect their preferred external provider from Account
Security.

## Reverse proxies

The provider callback must observe the public HTTPS scheme and host. Configure
Tuvima's trusted proxy networks before relying on forwarded headers; do not trust
forwarded headers from arbitrary clients. The exact redirect URI registered at
the provider must match the public callback URI.

## Provider references

- [Google OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect)
- [Microsoft identity platform protocols](https://learn.microsoft.com/en-us/entra/identity-platform/v2-protocols)
- [GitHub OAuth web application flow](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps)
- [Facebook Login for the web](https://developers.facebook.com/docs/facebook-login/web/)
