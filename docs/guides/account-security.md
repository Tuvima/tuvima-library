---
title: "Accounts, Profiles, and Account Recovery"
description: "Operate household accounts, profiles, passkeys, administrator elevation, invitations, and optional SMTP recovery."
audience: "administrator"
category: "guide"
product_area: "security"
tags:
  - "accounts"
  - "profiles"
  - "passkeys"
  - "smtp"
---
Tuvima uses accounts for sign-in and profiles for the identity used inside the
library. The first setup screen therefore asks for an email and password for the
administrator account, plus a separate profile display name. One account can be
granted Dad, Mom, Kids, or other profiles without sharing those profiles with a
different remote account.

Administrators manage accounts and profile grants under **Settings → Users &
Access → Accounts & Profile Grants**. Creating an invitation produces a
single-use link that expires after seven days. Share it through a trusted private
channel. The recipient chooses their initial password and receives only the
profiles included in the invitation.

Creating a profile also creates local-only access for it. A local-only account
has no email or password and cannot begin a fresh remote session. An
administrator can grant the profile to a normal signed-in account, or set a
profile PIN for trusted household access.

## Account Security

Open **Account & Security** to:

- change the account password and regenerate one-time recovery codes;
- register or remove passkeys, including Windows Hello and compatible password
  managers;
- connect or disconnect configured Google, Microsoft, GitHub, Facebook, or OIDC
  sign-in providers;
- review and revoke device sessions; and
- configure an administrator-only PIN.

Never remove an account's final usable authenticator. Tuvima enforces this for
passkeys and external providers. Password changes and resets rotate the security
stamp and revoke existing sessions.

Passkeys use WebAuthn and require a secure browser origin. `localhost` is allowed
for development; LAN or internet hostnames must use HTTPS. The public hostname
seen by the browser must also be forwarded correctly to the Engine.

## Administrator elevation

Opening administrative settings or invoking privileged Engine APIs requires an
administrator profile plus a recent elevation. Confirm with the separate
administrator PIN, the account password, or a registered passkey/Windows Hello.
The grant expires after 30 minutes, is tied to the current session and profile,
and is cleared immediately when the active profile changes.

## Local administrator recovery

If the administrator cannot use email or a saved recovery code, run recovery
from an elevated terminal on the server itself. The command is deliberately not
available through the Dashboard or Engine HTTP API, even when remote access is
enabled.

From an installed host:

```text
tuvima-admin auth reset-password --email administrator@example.com
```

From a source checkout:

```text
dotnet run --project src/MediaEngine.Admin -- auth reset-password --email administrator@example.com
```

In the supported container image:

```text
docker exec -it --user 0 tuvima-library /app/admin/tuvima-admin auth reset-password --email administrator@example.com
```

Windows requires an elevated Administrator terminal. Linux and macOS require
effective user ID 0; the container command explicitly runs as root. The new
password is entered interactively so it does not enter shell history or the
process list. A successful reset revokes all sessions, rotates the account's
security stamp, and replaces all recovery codes.

An externally reachable Dashboard does not weaken this boundary: a remote user
cannot invoke the command unless they have separately compromised operating-
system or container-root access. At that point they already control the server
and its database. Full server/database reset remains a host operation and is
never offered anonymously on the login screen.

## Optional email delivery

Email is optional. A server without SMTP remains recoverable with saved recovery
codes or the local `tuvima-admin auth reset-password --email ...` command.
Tuvima does not install or expose a mail relay.

The simplest reliable self-hosted arrangement is an authenticated SMTP relay
from a transactional email provider or the household's existing mail provider.
Use port 587 with STARTTLS, a provider-issued SMTP credential or app password,
and a verified sender address. Do not use a personal mailbox password.

Set the public, non-secret values in `config/core.json`:

```json
{
  "auth": {
    "password_reset": {
      "mode": "Smtp",
      "public_base_url": "https://library.example.com",
      "smtp_host": "smtp.example.com",
      "smtp_port": 587,
      "use_start_tls": true,
      "from_address": "library@example.com",
      "from_name": "Tuvima Library",
      "username": "SMTP_USERNAME"
    }
  }
}
```

Copy `config/examples/email.secrets.example.json` to
`config/.secrets/email.json`, enter `smtp_password`, and restrict the file to the
operating-system account running the Dashboard. The `.secrets` directory is
gitignored and excluded from backups.

Restart the Dashboard after changing these files. Account Security exposes a
**Send test email** action when the adapter is ready. Password-reset requests
always show the same public response, even for unknown accounts or delivery
failure, and valid links expire after 30 minutes and work once.

For a server reachable only on a private LAN with no dependable HTTPS public
URL, leave SMTP reset disabled and use recovery codes/passkeys. Email-shaped
login identifiers are still required for remote password accounts because they
provide a stable, familiar account identity; local-only accounts are the
explicit no-email exception.
