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
status: current
---

# Configure External Authentication

Add another way to sign in to Tuvima Library with an account you already use. Allow 15–30 minutes after registering the provider app and setting up HTTPS. Keep password and recovery access enabled while you test.

Each self-hosted server needs an application registration at the provider and a
stable HTTPS Dashboard origin for remote callbacks. Register the callback shown
below, replacing the origin with the public Dashboard origin:

```text
https://library.example.com/signin-tuvima-{provider-id}
```

## Connect the provider

1. Configure a stable HTTPS Dashboard address using [secure remote access](remote-access.md).
2. Register an application with the provider and copy the callback above exactly.
3. Open **Settings → Users & Access → Authentication**.
4. Choose **Optional** or **Required** authentication mode to allow external sign-in.
5. Enter the provider ID, protocol, client ID, secret, scopes, and authority or OAuth addresses.
6. Save, then link the provider from your own **Settings → Account → Security** page.
7. Test sign-in before relying on it for other accounts.

Keep a password, passkey, or recovery route available while testing. A configured provider does not automatically link existing accounts by email.

<details>
<summary>Technical details: configure files directly</summary>

Provider IDs contain lowercase letters, numbers, and hyphens. They begin with a letter and are 2–40 characters long. Callback paths are fixed. Linked identity keys contain the provider ID, canonical issuer, and immutable subject.

## Public configuration

Add providers to `auth.external_providers` in `config/core.json`. This file may
contain public client IDs but must not contain client secrets.

```json
{
  "auth": {
    "mode": "Required",
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
        "authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
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

Microsoft requires your tenant ID in the authority; `common`, `organizations`
and `consumers` are rejected because their tokens carry a tenant-specific issuer.
Provider changes apply at startup; Settings shows "Restart Tuvima Library to apply sign-in provider changes" while the saved providers differ from the running ones. If you set `issuer`, it must match the token's `iss` value exactly, including any trailing slash. When only `authority` is set, one trailing-slash difference is tolerated. Google and Microsoft use OIDC discovery. GitHub uses its OAuth
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

</details>

## Identity linking

Link an external login to a Tuvima account before using it to sign in. Tuvima checks the provider identity; it never links accounts just because their email addresses match. This helps prevent an old or unverified email address from taking over your account.

Sign in to your existing account and open **Settings → Account → Security**. Choose the provider's **Link** action and complete its sign-in check. You can disconnect it from the same page, but must keep one working sign-in method. Administrators cannot link a person by typing their provider identity.

To add a family member, invite them from **Settings → Users & Access → Users**. After accepting, they can link their own provider from Account Security.

## Reverse proxies

The provider must see your public HTTPS host and scheme. Trust only the actual proxy addresses before using forwarded headers. The registered callback address must match exactly.

## Provider references

- [Google OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect)
- [Microsoft identity platform protocols](https://learn.microsoft.com/en-us/entra/identity-platform/v2-protocols)
- [GitHub OAuth web application flow](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps)
- [Facebook Login for the web](https://developers.facebook.com/docs/facebook-login/web/)

## Next steps

- [Manage account recovery](account-security.md).
- [Check remote access](remote-access.md).
- [Read the security architecture](../architecture/security.md).
