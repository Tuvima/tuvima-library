# Spec: Encrypted plugin and integration keys

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-10-21 · **Related docs:** [Developer tools spec](developer-tools.md) (WP14 secrets store, WP19 install, WP25 backup encryption, D-K, D-P) · [Security architecture](../../docs/architecture/security.md#secrets-limits-and-lifecycle)

## 1. Goal

The keys and logins you give the Library (metadata provider keys, plugin keys, later notification and backup-destination logins) stop sitting as readable text files in the config folder. They stay usable after a data store wipe, and a backup can carry them safely, so moving to a new machine does not mean re-entering every key. It matters now because one-button plugin install (WP19) means third-party code arrives on the server and the keys become worth protecting.

## 2. Scope

**In scope**
- Encrypting the key files in `config/secrets/` (providers) and `config/secrets/plugins/` (plugins) on disk.
- One-time, automatic conversion of existing plain key files on first start, with no re-entry (this is the Engine's own file format change, not a pre-release data migration; see D6).
- An optional "Include keys" choice on a backup, sealed under the backup passphrase, and restoring them with that passphrase.
- Updating the security docs and the plugin guide so they stop saying keys are plain text.

**Out of scope**
- The backup passphrase feature itself (WP25 builds scheduled, passphrase-encrypted archives). This spec uses it and does not duplicate it.
- Sign-in secrets that are already protected (two-step secrets, webhook signing secrets, cast links).
- Keeping keys safe from an administrator who is signed in on the server, or from a plugin the administrator approved to read its own key. Plugins still run inside the server (developer tools D-C); encryption protects files at rest and backups, not a running process.
- A cloud or shared key vault. Nothing leaves the machine (Privacy).

## 3. Decisions made

Shy agreed on 2026-10-21 that keys should stop being plain text but must stay usable after backup and restore. D1 to D5 turn that into a design; the open points are in section 8.

- **D1. Encrypt at rest, behind the existing key store.** `IConfigSecretsStore` keeps its shape (owners, names, files that survive a data store wipe, one owner cannot read another's). Only the file contents change, so no provider or plugin code changes. Reason: Maintenance and Reliability.
- **D2 (changed 2026-10-21 by Shy: one method on every platform).** **The encryption key must not sit next to the files it protects, and works the same everywhere.** The root key is a random key file stored outside the config folder (in the Library's per-user data area; on Docker, supplied through a mounted secret or environment value), readable only by the account that runs the Library (owner-only permissions on Linux and macOS, owner-only access list on Windows). Windows' built-in user-bound protection is not used. Reason: Maintenance (one code path, fully testable in the cloud) and Reliability; the practical protection is the same, because anyone with administrator rights on the machine can read the file either way.
- **D3. Wiping the data store never loses keys, and moving machines never silently breaks them.** The root key is separate from the data store. If the root key is missing (new machine, new Windows sign-in, restored config folder), keys show as "Needs re-entering" on their plugin or provider card with Needs attention, never an unexplained failure and never a crash. Reason: Reliability.
- **D4. Backups carry keys only when asked, and only sealed.** A backup has an "Include keys" choice, off by default, available only when a backup passphrase is set. Keys are re-encrypted under a key derived from that passphrase (never the machine's root key), inside the archive. Without a passphrase the choice is hidden and the backup behaves as today (no keys). The Library never stores the passphrase. Reason: Privacy; a sealed bundle is useless to anyone who finds the file.
- **D5. Restore asks for the passphrase and puts keys back sealed to the new machine.** Restoring a backup that includes keys prompts for the passphrase, checks it, re-encrypts the keys under the new machine's root key, and lists which keys were restored. A wrong passphrase restores everything else and leaves keys for re-entry, with a clear message. The first-run "restore during setup" path accepts sealed keys the same way; today it refuses any backup that holds keys, and that refusal stays for unsealed ones. Reason: Reliability.
- **D6. Existing plain key files convert on first start of the new version, once, with the original removed only after the encrypted copy is verified.** No user action. This is the Engine converting its own live files, which is not the forbidden pre-release data migration; if a file cannot be converted it stays untouched, shows Needs attention, and is retried next start.
- **D7. Keys are still never shown, logged or returned.** The key list keeps returning names only. Same rule as today.
- **D8. Nothing changes for the person entering a key.** Same screens, same Save button. A small "Keys are encrypted on this server" line on Integrations and on plugin cards replaces any wording that implies plain text.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | Keys behave exactly as now. A line under the key fields says they are stored encrypted on this server. |
| Backup, no passphrase set | No "Include keys" option. A one-line hint: set a backup passphrase to include keys. |
| Backup, passphrase set | "Include keys" switch, off by default, with: "Keys are sealed with your passphrase. Without it they cannot be read." |
| Restore, backup holds keys | Passphrase prompt. Success lists restored keys by name. Wrong passphrase: "That passphrase didn't open the keys. Everything else was restored; re-enter keys when you're ready." |
| Root key missing | Affected cards show Needs attention: "This key can't be read on this computer. Enter it again." Other plugins and providers carry on. |
| Conversion fails for one file | That owner shows Needs attention; the rest are converted. |

Phones, tablets and TV: no new screens. Backup and key pages are server-administrator settings, desktop-reachable as today.

## 5. Data/model changes

- Key files stay at the same paths, but hold an encrypted envelope (version, algorithm, nonce, ciphertext, check value) instead of readable JSON.
- Root key stored outside the config folder per D2.
- Backup archive gains one optional sealed entry holding the keys; the manifest records "includes sealed keys" and the key-derivation settings, never the passphrase. The existing refusal of plain secret entries stays.
- No data store (SQLite) change.

## 6. Work packets

### EK1 — Encrypted key files
- **What it does:** Encrypts and decrypts the owner files inside the config-folder store, with the root key per D2, an envelope with a version marker, and a clear "cannot be read here" result that cards surface as Needs attention (D3). Includes the first-start conversion with verify-then-remove (D6) and tests that no log line or error holds a value.
- **Files likely touched:** `ConfigFolderSecretsStore.cs`, a new root-key provider, `ProviderCredentialService.cs`, Program setup (to be confirmed by Claude Code)
- **Depends on:** WP14 (built)
- **Size:** Complex
- **Done when:** a key saved, then the file opened in a text editor, shows no readable value; keys still work after the data store is wiped; moving the config folder to a machine without the root key shows Needs attention for each key and nothing crashes; a plain file from the old version converts on start and the original is gone only after the encrypted copy reads back correctly.

### EK2 — Sealed keys in backups and restore
- **What it does:** "Include keys" on backup (D4), passphrase-derived sealing, restore with passphrase prompt (D5), including the setup-time restore path, and the manifest flag. Plain secret entries are still refused.
- **Files likely touched:** `DatabaseBackupService.cs`, `BackupManifestWriter.cs`, backup Contracts and tests, Dashboard Backup and recovery page (to be confirmed by Claude Code)
- **Depends on:** EK1, and the backup passphrase from WP25
- **Size:** Complex
- **Done when:** a backup with keys, restored on a clean test install with the right passphrase, brings every key back working; with a wrong passphrase everything else restores and keys are listed for re-entry; the archive holds no readable key value; a backup made without a passphrase cannot include keys.

### EK3 — Wording and docs
- **What it does:** Replaces every "plain text" or "encryption at rest is not part of this store yet" statement (security architecture page, plugin guide, provider onboarding, Developer tools spec D-K note), adds the install-guide note for Docker root keys, and adds the line in D8.
- **Files likely touched:** `docs/architecture/security.md`, `docs/architecture/provider-onboarding.md`, Dashboard key screens (to be confirmed by Claude Code)
- **Depends on:** EK1
- **Size:** Mechanical
- **Done when:** a search of the docs finds no claim that keys are plain text.

**Order and coordination with the Developer tools build.** EK1 and EK3 must merge before WP19 turns the Install button on (the Install button is already hidden until WP19). EK2 must not block WP19: it ships with WP25, because the backup passphrase does not exist until then. Until EK2, restore behaves as today (keys are not in backups; re-enter them). The Developer tools build thread should add "EK1 merged" as a condition on WP19 and pick up EK2 beside WP25; if it agrees, this spec can be built as its own small thread rather than inside that one.

## 7. Local checks

These go on `engineering/validation/batch.md` when built.
- Windows: save a key, confirm the file is unreadable in Notepad, restart the Engine, confirm the key still works.
- Windows: copy the config folder alone to another machine and confirm keys show Needs attention, not an error screen.
- Real library: take a backup with Include keys, wipe the data store, restore, and confirm TMDB and one real plugin still fetch data.
- Docker/Linux: root key supplied as documented, and a missing key behaves as in D3.

## 8. Risks and open questions (all answered; none open)

- **Q1 (answered by Shy 2026-10-21: use the same method everywhere).** Protection is the same on Windows, Linux and Docker: it stops a copied config folder, a backup of it or a support bundle from exposing keys, but not someone with administrator rights on the machine. Stated plainly in the docs.
- **Q2 (answered by Shy 2026-10-21: no recovery code; keys are for convenience).** Lose the root key and the passphrase, lose the keys.** That is the point, and the fix is re-entering keys (provider keys are free to re-create). Recommendation: keep it simple, no recovery key. Alternative: print a one-time recovery code at first start, which is one more thing for people to lose.
- **Q3 (answered by Shy 2026-10-21: wait for WP25).** Can EK2 be done before WP25?** Only by building a small standalone passphrase just for key sealing, which WP25 would then replace. Recommendation: no, wait for WP25's passphrase and keep the interim "re-enter after restore" behaviour.
- **Q4. Plugins reading their own keys.** An approved plugin can still read its own key while running (D-C says plugins run unsandboxed). Encryption does not change that; the later separate-program plugin host (WP17) narrows it. Recommendation: note it in the docs, nothing more here.
- **Risk:** a bug in conversion could lose keys. Mitigated by D6 (keep the original until the encrypted copy reads back).

## Plain English summary

Today your provider and plugin keys sit in ordinary files that anyone with access to the config folder can read. This spec locks them: the files become unreadable, the Library still opens them for you automatically, and wiping the library's data does not lose them. Backups can optionally carry the keys, locked with your backup passphrase, so moving to a new computer means typing one passphrase instead of hunting down every key. The locking and the auto-conversion come first, before plugin install ships. The backup part waits for the backup passphrase feature that is already planned. If you move to a new computer without a backup that includes keys, you re-enter your keys once and nothing else is lost.
