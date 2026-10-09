---
title: "Accounts, profiles, and recovery"
description: "Manage household accounts, profile access, optional administrator locks, and reliable account recovery."
audience: "administrator"
category: "guide"
product_area: "security"
status: current
---

# Accounts, profiles, and recovery

Give each person the right access to Tuvima Library and keep a way to recover the administrator account. Basic account setup takes a few minutes.

An account signs in. A profile holds personal history, preferences, and View Personal Space. Sharing a profile deliberately shares that experience and private space.

## Give someone access

1. Open **Settings → Users & Access → Users**.
2. Choose **New user** for a local account or **Invite user** for an invitation.
3. Set the account's permitted Read, Watch, Listen, View, and catalogue libraries.
4. Grant only the profiles that person should use. An account can have up to eight profile grants.
5. Send an invitation through a trusted private channel. Use the expiry shown when it is created.

Invitations work once. Their lifetime comes from **Users & Access → Authentication**; the default is seven days, but administrators can change it.

Every account needs an email address to sign in. Someone who does not need their own sign-in is added as a profile in an existing household instead, and is opened by switching profiles.

## Protect admin settings

Admin access requires both an eligible account and an active profile grant with admin access.

1. In **Users**, open the account's **Manage profiles** action.
2. Enable admin settings only for the intended grant.
3. Turn on its optional admin protection and set a separate PIN.
4. Choose the unlock duration offered by the control and save.

The PIN lock protects settings after sign-in. It is separate from the account password and profile-selection PIN. Switching profiles clears the active admin unlock.

## Manage your sign-in methods

Open **Settings → Account → Security** to change your password, replace recovery codes, manage passkeys, connect external providers, or revoke device sessions.

Password resets revoke existing sessions. Tuvima prevents removal of the final usable authenticator.

Passkeys need a secure browser origin. Development on `localhost` is allowed; other hostnames need HTTPS and the server's public address (`remote.public_hostname`, set under **Settings → Network**). **Users & Access → Authentication** reports readiness and explains unavailable methods.

## Secure a this-computer account

If you started Tuvima on your desktop without a password, **Settings → Account → Security** shows a **Secure your account** card. Enter a password (at least 12 characters) and confirm it. You get a new set of recovery codes, and the account becomes a normal one: it can sign in from other devices once **Who can connect** allows it. You can add a passkey afterwards. Until you do this, Tuvima will not let you open the door wider than *This computer*.

## Confirm it's you

Changing your password, adding or removing a passkey, linking or unlinking a provider, replacing recovery codes, signing out other sessions, and setting or removing a PIN need a sign-in from the last 10 minutes. If yours is older, a **Confirm it's you** window asks for your password or passkey, then finishes what you were doing. Accounts that work only on this computer have no password to ask for, so they are not asked. Tuvima still refuses to remove your last sign-in method, and there is no action to remove a password.

## Password rules

A password needs at least 12 characters, up to 128. It cannot be a common password (such as `iloveyou1234`) or match your email address or display name, and capitals do not get around the list. These rules apply when a password is set or changed. Passwords created earlier keep working until they are changed.

## Recover a lost password

Use a saved one-time recovery code, or email recovery if an administrator configured it. Keep recovery codes outside the server and replace your saved set after regeneration.

If neither works, an operator with administrator control of the host can run the interactive recovery console.

From a source checkout:

```powershell
dotnet run --project src/MediaEngine.Admin -- auth reset-password --email administrator@example.com --config-dir config
```

From the container:

```bash
docker exec -it --user 0 tuvima-library /app/admin/tuvima-admin auth reset-password --email administrator@example.com
```

Where a host installation includes the console:

```text
tuvima-admin auth reset-password --email administrator@example.com
```

Use an elevated Windows terminal or effective user ID 0 on Linux/macOS. Enter the password at the hidden prompt. Do not put it in a command argument.

### Start first-run setup from another device

Before the first administrator exists, `tuvima-admin setup code` prints an eight-character one-time code (valid 30 minutes) that the setup page asks for when you open it from another device on your home network. In the container the command is on the path: `docker exec -it <container> tuvima-admin setup code`. It refuses to run once an administrator exists (exit code 5).

A successful reset revokes sessions and replaces recovery codes. The command requires an existing data store; it does not create a missing one. It has no Dashboard or Engine HTTP equivalent.

## Set up optional email recovery

Email is optional. Use an authenticated SMTP relay with a verified sender and provider-issued credential. Keep recovery codes available if delivery fails.

1. Set the public connection values in `config/core.json`.
2. Copy `config/examples/email.secrets.example.json` to `config/.secrets/email.json`.
3. Enter `smtp_password` and restrict file access to the Dashboard's operating-system account.
4. Restart the Dashboard.
5. In **Users & Access → Authentication**, use **Send test email to my account** when delivery reports ready.

Password-reset links are built from the server's public address, the same one passkeys and linked sign-in use. Set it under **Settings → Network** (`remote.public_hostname` in `config/network.json`) before enabling email recovery.

Example non-secret configuration:

```json
{
  "auth": {
    "password_reset": {
      "mode": "Smtp",
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

The private `.secrets` folder is ignored by Git and excluded from recovery archives. Preserve it separately through your host backup policy.

Password-reset requests show the same public response for unknown accounts or delivery failures. Valid links expire after 30 minutes and work once.

## Next steps

- [Connect an external sign-in provider](external-authentication.md).
- [Configure secure remote access](remote-access.md).
- [Back up and test recovery](operations-and-recovery.md).
