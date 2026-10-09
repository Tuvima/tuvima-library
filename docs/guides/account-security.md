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
2. Choose **New user**, enter the person's email, then pick how they will sign in for the first time: **Send an invitation** (the default) or **Set a temporary password**. Use **Invite user** to invite someone into profiles that already exist.
3. Set the account's permitted Read, Watch, Listen, View, and catalogue libraries.
4. Grant only the profiles that person should use. An account can have up to eight profile grants.
5. Hand over what Tuvima shows you, through a trusted private channel. It is shown once.

Only administrators create accounts. There is no public sign-up, and Tuvima never sends email for this.

### Invitation code

An invitation is a short code such as `KQ7M4-XH2TA` (ten letters and digits; the confusing ones, 0, O, 1, I and L, are left out). Tuvima shows the code, a link (`<address>/auth/invite?code=KQ7M4-XH2TA`), a QR code, a **Copy** button for the code and the link, and the expiry. The link uses the server's public address when one is set under **Settings → Network**, and otherwise the address you are using now. Only a fingerprint of the code is stored, so it cannot be shown again.

The person opens the link (or opens `/auth/invite` and types the code), sees their email, and chooses a password. Each invitation works once. Its lifetime comes from **Users & Access → Authentication**; the default is seven days. A wrong or expired code gets the same plain answer, and repeated wrong codes from outside the home are slowed down.

This page sets a password only; passkeys can be added afterwards in **Settings → Account → Security**.

### Temporary password

Choose **Set a temporary password** to type one yourself or press **Generate** for a random 16-character one. It is shown once, with a **Copy** button. The first time the person signs in with it, Tuvima asks them to choose their own password and nothing else works until they do. Choosing a new one signs out every other device. A temporary password stops working after 7 days; after that the person sees "Ask your administrator for a new temporary password." and you can set another from the user's actions menu (**Set temporary password**), which also signs them out everywhere.

Every account needs an email address to sign in. Someone who does not need their own sign-in is added as a profile in an existing household instead, and is opened by switching profiles.

## Add a person, and give them their own sign-in

Under **Settings → Users & Access → People** every household is listed with the people in it (up to 8).

1. **Add person** adds someone to the household. Enter a name, switch on **Child profile** for a restricted profile, and set an optional PIN that is asked before anyone switches into them. The household's sign-in can open them straight away; they do not have a sign-in of their own yet.
2. **Give (name) their own sign-in** lets a person sign in with their own email. Choose **Send an invitation** or **Set a temporary password** (the same two ways as for a new user) and enter their email. The new sign-in opens straight to that person without the profile picker, stays in the same household, and is never an administrator.
3. **Remove sign-in** deletes the person's own sign-in, signs it out everywhere and unpairs its devices. The person stays in the household with everything they have saved, and the household's sign-in can still open them.

Library and lane access is not set per person. A person's own sign-in follows the household's main sign-in, so when you change the household's access it reaches them as well. Only administrators do this for now; a household administrator will be able to later.

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

## Two-step codes (optional)

Two-step codes add a second check to password sign-in. You turn them on yourself in **Settings → Account → Security → Two-step codes**: after confirming it's you, scan the picture with an authenticator app (or type the key it shows), enter the 6-digit code the app gives you, and save the new recovery codes that appear. From then on, signing in with your password also asks for **the 6-digit code from your authenticator app**. If your phone is lost, choose **Use a recovery code instead**; each recovery code works once.

- Passkey and **Sign in with…** sign-ins never ask for a code. They are already a second kind of proof.
- **Confirm it's you** also asks for the code while two-step is on.
- To turn it off, confirm it's you, then enter a fresh code (wait for the next one if you just used one) or a recovery code.
- If someone loses both their phone and their recovery codes, a server administrator can use **Turn off two-step codes** on their account in Settings → Access. This is recorded in the audit log. You can also run `tuvima-admin auth reset-two-step --email someone@example.com` on the computer that hosts Tuvima.
- Codes are checked offline against a secret stored encrypted on your server. Nothing is sent to any service, and there are no text or email codes.

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
