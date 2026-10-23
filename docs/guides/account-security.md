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

1. Open **Settings → Users and Sign-in → Users** (server administrators only).
2. Choose **Invite someone outside your household**, enter the person's email, then pick how they will sign in for the first time: **Send an invitation** (the default) or **Set a temporary password**. They start a household of their own and look after it as its household administrator. Use **Invite user** to invite someone into profiles that already exist.
3. Set the account's permitted Read, Watch, Listen, View, and catalogue libraries.
4. Grant only the profiles that person should use. An account can have up to eight profile grants.
5. Hand over what Tuvima shows you, through a trusted private channel. It is shown once.

Only administrators create accounts. There is no public sign-up, and Tuvima never sends email for this.

On a phone, **Settings → Users and Sign-in** shows people and applications as read-only cards (name, role or kind, status and last active). Make changes on a computer or tablet.

### Invitation code

An invitation is a short code such as `KQ7M4-XH2TA` (ten letters and digits; the confusing ones, 0, O, 1, I and L, are left out). Tuvima shows the code, a link (`<address>/auth/invite?code=KQ7M4-XH2TA`), a QR code, a **Copy** button for the code and the link, and the expiry. The link uses the server's public address when one is set under **Settings → Network**, and otherwise the address you are using now. Only a fingerprint of the code is stored, so it cannot be shown again.

The person opens the link (or opens `/auth/invite` and types the code), sees their email, and chooses a password. Each invitation works once. Its lifetime comes from **Users and Sign-in → Sign-in Methods**; the default is seven days. A wrong or expired code gets the same plain answer, and repeated wrong codes from outside the home are slowed down.

This page sets a password only; passkeys can be added afterwards in **Settings → Security → Extra protection**.

### Temporary password

Choose **Set a temporary password** to type one yourself or press **Generate** for a random 16-character one. It is shown once, with a **Copy** button. The first time the person signs in with it, Tuvima asks them to choose their own password and nothing else works until they do. (On a person's own **Sign-in** sheet the **Must change it at first sign-in** switch is on by default; turning it off sets an ordinary password that does not expire.) Choosing a new one signs out every other device. A temporary password stops working after 7 days; after that the person sees "Ask your administrator for a new temporary password." and you can set another from the user's actions menu (**Set temporary password**), which also signs them out everywhere.

Every sign-in that works away from home needs an email address. The administrator you create at setup with **Start without a password** has only a name until you secure the account in **Settings → Security**. Someone who does not need their own sign-in is added as a profile in an existing household instead, and is opened by switching profiles.

## Add a person, and give them their own sign-in

A person is looked after from their own page under **Settings → Profiles**, which is for administrators only; each person's row has an **Edit** button. What you see depends on who you are:

- A **household administrator** sees the people in their own household (up to 8), and, on **Security → Devices**, the paired devices and signed-in browsers of their household (every signed-in person sees that page, but a member sees only their own). Nothing from Users and Sign-in is shown, and they cannot reach another household.
- A **server administrator** sees every household under **Settings → Profiles**, with **Add household**, and a **Households** page under Profiles (**Settings → Profiles → Households**) that lists each household (name, how many people, how many sign-ins) and lets you rename or remove it. A household can only be removed once it has no people, sign-ins or shared photos.
- Anyone else does not see Profiles. They change their own name, picture and details from **Edit profile** on their Overview, and look after their sign-in under **Settings → Security**.

On a person's page:

1. **Add profile** (on the Profiles list) adds someone to the household. Enter a name, switch on **Kids profile** for a restricted profile, and set an optional PIN that is asked before anyone switches into them. The household's sign-in can open them straight away; they do not have a sign-in of their own yet.
2. **Sign-in** shows the person's sign-in email, or *No sign-in*. It is read only on the profile page: a server administrator adds, changes and removes sign-ins in **Settings → Users and Sign-in**. A sign-in opens its own profile, and the person can switch to others in their household.
3. **Roles** and **Access** are set on the profile: **Household administrator** (and, for a server administrator, **Server administrator**) switches, and **Libraries and lanes** for Read, Watch, Listen and View and the Libraries they can open. A household administrator can give only what their own profile has.
4. **Profile details** sets the PIN asked before anyone switches into that person (4 to 12 digits). A new PIN replaces the old one.
5. **Delete profile** removes the person and everything personal in it, and their sign-in too. You choose whether their photos **Move to Shared** (the default) or are deleted.

If a household administrator turned on an administrator PIN, Tuvima asks for it the first time the Engine refuses a change.

## Protect admin settings

Administrator rights belong to the profile: a profile is a server administrator or a household administrator, and the settings only open while that profile is the one in use. The optional PIN lock is a setting on the profile too.

1. Make sure the profile holds an administrator role (**Settings → Profiles**, the person's **Roles** group) and has a PIN (**Profile details**). There is one PIN per profile; admin pages and Vault mode ask for that same PIN.
2. Open the person's page under **Settings → Profiles**. An **Administrator PIN** group appears there, on your own profile or, for a server administrator, on anyone's.
3. Turn on **Ask for a PIN to open administrator settings** (it is off until you do) and choose how long an unlock lasts.

For example, Mom is a household administrator and signs in as mom@home.com into her own profile. If she also needs server settings, she switches to Shy's profile, which asks for Shy's PIN when he has turned this on.

The PIN lock protects settings after sign-in. It is separate from the sign-in password. Switching profiles clears the active admin unlock. With the switch off, admin pages open without a PIN.

## Manage your sign-in methods

Open **Settings → Security**. **Password** changes your password. Once you have a password, **Extra protection** opens one sheet for passkeys, two-step codes, linked sign-ins and recovery codes. Everyone sees **Sign out everywhere else** under *Access*, which signs out your other sessions (for example a lost phone) and keeps the one you are using; administrators also see **Devices** there. Under *Your photos*, **Who viewed your photos** lists, read-only and newest first, each time a server administrator opened your photos (the household's own administrator also sees views of the household's Shared Library); the times are shown in UTC and nothing there can be deleted.

Password resets revoke existing sessions. Tuvima prevents removal of the final usable authenticator.

Passkeys need a secure browser origin. Development on `localhost` is allowed; other hostnames need HTTPS and the server's public address (`remote.public_hostname`, set under **Settings → Network**). **Users and Sign-in → Sign-in Methods** reports readiness and explains unavailable methods.

## Secure a this-computer account

If you started Tuvima on your desktop without a password (**Use Tuvima on this computer**), that account has no password and no passkey. **Any device that can reach the server can use it, including a guest on your home Wi-Fi, and it has administrator rights.** Tuvima is open from the start and closed router ports are what keep outsiders away, so add a password or passkey as soon as other people share your network, and always before you give Tuvima an address on the internet.

To close it, open **Settings → Security → Password**, which shows **Secure your account**. Enter an email (only if the account has none; you sign in with it from other devices), then a password (at least 12 characters), and confirm it. You get a new set of recovery codes, and the account becomes a normal one that always asks for its password or passkey. You can add a passkey afterwards. Installs that start with a password are normal accounts from the first sign-in.

## Confirm it's you

Changing your password, adding or removing a passkey, linking or unlinking a provider, replacing recovery codes, signing out other sessions, and setting or removing a PIN need a sign-in from the last 10 minutes. If yours is older, a **Confirm it's you** window asks for your password or passkey, then finishes what you were doing. Accounts that work only on this computer have no password to ask for, so they are not asked. Tuvima still refuses to remove your last sign-in method, and there is no action to remove a password.

## Two-step codes (optional)

Two-step codes add a second check to password sign-in. You turn them on yourself in **Settings → Security → Extra protection → Two-step codes**: after confirming it's you, scan the picture with an authenticator app (or type the key it shows), enter the 6-digit code the app gives you, and save the new recovery codes that appear. From then on, signing in with your password also asks for **the 6-digit code from your authenticator app**. If your phone is lost, choose **Use a recovery code instead**; each recovery code works once.

- Passkey and **Sign in with…** sign-ins never ask for a code. They are already a second kind of proof.
- **Confirm it's you** also asks for the code while two-step is on.
- To turn it off, confirm it's you, then enter a fresh code (wait for the next one if you just used one) or a recovery code.
- If someone loses both their phone and their recovery codes, an administrator can turn their codes off from that person's page under **Settings → Profiles**, in **Household access** → **Two-step codes** (for a person with their own sign-in): a server administrator for anyone, a household administrator for their own household. This is recorded in the audit log. You can also run `tuvima-admin auth reset-two-step --email someone@example.com` on the computer that hosts Tuvima.
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
5. In **Users and Sign-in → Sign-in Methods**, use **Send test email to my account** when delivery reports ready.

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
