---
title: "Troubleshooting"
description: "Check startup, connections, folder access, providers, and optional AI when Tuvima Library cannot complete a task."
audience: "user"
category: "guide"
product_area: "support"
status: current
---

# Troubleshooting

Use these checks to find why Tuvima Library cannot start, find files, or complete an import. Most checks take a few minutes. Keep any error text and the time it occurred.

## A Docker container does not become healthy

1. Confirm all seven persistent mounts exist and have the intended permissions.
2. Validate the edited configuration.
3. Inspect startup and health:

   ```bash
   docker compose config --quiet
   docker compose ps
   docker compose logs --tail=100 tuvima
   docker inspect --format '{{.State.Health.Status}}' tuvima-library
   ```

4. Repair the host folder ACL or configured UID/GID if logs report denied access.
5. Confirm Dashboard port `5016` is not already used.

Do not expose Engine port `61495` or enable privileged mode to work around startup failures. See [Docker installation](../install/docker.md).

## The Engine does not start from source

Run from the repository root and check `dotnet --version`. `global.json` requests stable SDK `10.0.100` with `latestFeature` roll-forward.

If restore cannot find `Tuvima.Wikidata*`, inspect `nuget.config`. This checkout uses nuget.org, while some local setups map those packages to the sibling `tuvima-wikidata/artifacts` feed. Check that feed before changing package references.

Review `config/core.json` and `config/libraries.json` for machine-specific paths. Stop an older Engine or Dashboard instance before starting another.

<span id="dashboard-cannot-reach-the-engine"></span>

## The Dashboard cannot reach the Engine

For source runs, start the Engine first:

```powershell
dotnet run --project src/MediaEngine.Api
```

Wait for `http://localhost:61495`, then start the Dashboard in another root terminal:

```powershell
dotnet run --project src/MediaEngine.Web
```

Set `TUVIMA_ENGINE_URL` before launching the Dashboard when the Engine address differs.

Both apps must resolve the same `TUVIMA_CONFIG_DIR` and data-protection keys. The Engine writes a protected Dashboard credential there. Check the resolved credential path in Dashboard logs if requests report temporary unavailability.

The Dashboard retries credentials on later requests and detects replacement. Do not copy a credential from another Engine data store or disable authentication to recover.

## Sign-in or administrator settings are unavailable

Check the active account, profile grant, and **Users & Access → Authentication** settings.

Remote account access needs remote sign-in enabled and a secure path. Administrator settings require account eligibility and an administrator-enabled active grant; optional grant PIN protection may also need unlocking.

Use [account recovery](account-security.md) if your password and authenticators are unavailable. A request from localhost alone never grants administration.

## Home is empty

Home shows real library results. Check:

1. **Settings → Libraries** for sources, media types, and path access.
2. **Settings → Providers** for enabled providers and connection results.
3. Operations at `/settings/ingestion` for active or waiting work.
4. **Settings → Review Queue** for uncertain items.

A catalogue item needs a real title, resolved type, and settled artwork state before browsing. View assets use their own local index and permissions.

## Files do not import

1. Confirm the Engine can read the server-side source path.
2. In Docker, use a container path and check its mount.
3. Confirm the extension and library media type match [supported media types](../reference/media-types.md).
4. Wait for copying to finish and release locks held by other programs.
5. Use **Scan now** in Operations after changing source folders.

For View, confirm the owning profile, source attachment, and upload/import policy. Routine catalogue scans do not replace View's personal-source workflow.

## An import waits or needs review

Operations reports durable queued jobs, retries, and provider waits. A settled file count can reach its target while required identity or organization work remains.

Restarted imports resume recoverable jobs. Use **Settings → Review Queue** for unclear matches, conflicting metadata, unreadable files, or ambiguous media types. Read the specific reason before changing anything.

Optional lyrics, subtitles, or commercial detection returning no result usually do not require identity review.

## Provider lookups fail

1. Confirm the provider is enabled.
2. Use its saved **Test connection** action.
3. Check required credentials, server network access, and provider rate limits.
4. Inspect the provider's safe status message.

Long-lived keys belong in `config/secrets/`. A blank key in the public provider definition may have an effective secret overlay. Do not include keys in logs or support reports.

## Local AI is unavailable

Open **Settings → Local AI → Models & Runtime**. Check whether a role is missing, downloading, ready, loaded, or failed.

Confirm model storage, native runtime availability, and capability gates. Saved feature flags do not make missing dependencies ready. Local AI is optional for initial catalogue setup.

## The documentation site looks stale

The site is generated from `docs/` by the documentation workflow. Check the latest eligible `main` run before assuming local edits are published.

Preview from the repository root:

```powershell
./scripts/docs/build-docs.ps1
./scripts/docs/serve-docs.ps1
```

<details>
<summary>Technical details: inspect durable operation and capability state</summary>

Authorized diagnostics can inspect:

- `GET /operations?queueName=ingestion`
- `GET /ingestion/batches/{batchId}/items`
- `GET /assets/{id}/capabilities`

These are internal Engine actions, subject to authentication and operation/resource permissions. Do not publish the Engine to reach them.

Useful operation states include `discovered`, `settling`, `waiting_for_lock`, `queued`, `hashing`, `parsing`, `scoring`, `registered`, `queued_identity`, and `completed`. Interrupted work remains visible after restart.

Capability rows distinguish `pending`, `queued`, `running`, `no_result`, `blocked`, `failed_retryable`, `failed_terminal`, `dead_lettered`, and `stale`. Missing output alone does not prove a provider ran or found nothing.

</details>

## Next steps

- [Check installation](../tutorials/getting-started.md).
- [Resolve review items](resolving-reviews.md).
- [Back up and recover](operations-and-recovery.md).
- [Check product status](../product/status.md).
