# Access cutover and verification

This is the deployment procedure for the complete accepted Access change. It has **not** been applied to the normal development runtime. Consult [execution status](./status.md) before merging or starting the replacement.

## Before the squash

1. Confirm the final integration SHA and the passing behavior, browser, formatting, and coverage evidence recorded in the status. A successful compile alone is insufficient.
2. Recheck `main` and the original checkout's pending changes. The integration branch already includes the reviewed crash and ingestion work; compare overlapping files and preserve any newer edits. Do not reset or overwrite the original checkout to make a squash apply.
3. Prepare one squash from `codex/access-integration` to `main`. Review its implementation, contract fixtures, documentation, and tests. Exclude `.tmp`, local configuration/secrets, databases, generated documentation, and runtime logs.

## Starting the replacement

1. Stop the Engine and Dashboard belonging to this checkout. Confirm neither process still holds its database or binaries. Use one Engine and one Dashboard per database/configuration directory.
2. Resolve the actual configuration, database, data-protection, managed library, and View paths before changing disposable state. Set or verify `TUVIMA_DB_PATH` when selecting a specific SQLite file; the current resolver does not use the legacy `core.json` `database_path` value as its database selection input. Without the environment override, verify the database resolved beneath the configured library root. Keep original media, read-only sources, and existing Personal Space directories intact.
3. Use fresh disposable database state for the `guid-blob-v7-access-authority` epoch. Old role/key authority is not converted or retained as a fallback. The existing epoch guard deliberately rejects obsolete databases unless its explicit reset mode is selected; any reset must remain limited to the verified application database and its SQLite companions.
4. Use an empty, separate managed View root for a fresh identity database. Existing private directories must not be assigned to newly generated profile IDs. Do not automatically reconnect old Personal Spaces or infer their owners from names. Preserve those originals until an explicit ownership decision is made.
5. Keep the Engine and Dashboard pointed at the same intended configuration and data-protection directory. Start the Engine first and let it issue the Dashboard transport bundle for that database. Do not copy a bundle from another database or suppress a bundle/database mismatch. A missing bundle is recoverable; a mismatched identity is an intentional startup rejection.
6. Start the Dashboard, complete first-run administrator setup, and configure account grants, optional profile/grant PIN protection, and library access. Issue fresh application credentials and re-pair native clients. Old account sessions, native bindings, and API keys are not authorization inputs to the replacement.
7. Configure retained catalogue sources with their intended existing-file protections and reingest. Do not enable moves, renames, metadata writeback, or private View imports merely to verify authorization. Provider configuration and real external sign-in should be enabled only when their actual readiness requirements are satisfied.

## Acceptance after startup

- Both hosts remain healthy; missing or rotating Dashboard credentials produce a recoverable unavailable state rather than a circuit crash.
- Users, Applications, Authentication, profile switching, administrator lock/unlock, and View scopes work at desktop, short desktop, and mobile sizes.
- Account/grant/application/token revocation takes effect at the API, resource, playback, event, and webhook boundaries. A private resource denial remains indistinguishable from a missing resource.
- Native pairing and playback succeed with new bindings. Telemetry reports unknown delivery details when the server lacks trusted evidence, and expired/crashed sessions close durably.
- Authorized event subscribers and webhook receivers get scoped events; revoked subscribers stop receiving them. Receiver failures remain bounded and do not terminate the hosts.
- Existing original files and private directory ownership remain unchanged by setup and authorization operations.

Keep the integration branch and acceptance evidence until the product owner is satisfied. Remove only clean worktrees created for this effort after their changes are safely retained; leave unrelated worktrees alone.

Plain English: the code is combined once, then starts with fresh access records. People and applications receive new permissions and credentials while existing media stays protected.
