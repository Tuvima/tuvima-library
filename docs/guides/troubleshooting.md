---
title: "Troubleshooting"
description: "Resolve common setup, ingestion, provider, AI, and Dashboard problems in Tuvima Library."
audience: "user"
category: "guide"
product_area: "support"
tags:
  - "troubleshooting"
  - "setup"
  - "ingestion"
---
This guide covers the checks that usually explain first-run, ingestion, provider, and Dashboard problems.

## Engine Does Not Start

Check the .NET SDK:

```powershell
dotnet --version
```

The repository requires the SDK version in `global.json`. If restore fails for `Tuvima.Wikidata*`, confirm the local NuGet feed path in `nuget.config` exists.

Run from the repository root so the Engine can resolve `config/`.

## Dashboard Cannot Reach The Engine

Start the Engine first:

```powershell
dotnet run --project src/MediaEngine.Api
```

Wait for:

```text
Now listening on: http://localhost:61495
```

Then start the Dashboard:

```powershell
dotnet run --project src/MediaEngine.Web
```

If the Engine uses a different address, set `TUVIMA_ENGINE_URL` before starting the Dashboard.

Both processes must use the same `TUVIMA_CONFIG_DIR` and data-protection keys. The Engine creates the protected Dashboard credential in that directory. If the credential is missing, invalid, or unreadable, the Dashboard blocks Engine requests and reports temporary unavailability instead of terminating the browser session. It retries the credential on subsequent requests and detects replacement without requiring a Dashboard restart. Check the Dashboard log for the resolved credential path; do not copy a credential from a different Engine database or disable authentication to recover.

## Home Is Empty

Home only shows real data returned by the Engine. It does not invent sample media.

Check:

- **Settings > Libraries** for incoming locations, catalogue source roles, primary destinations, the View root, and path access; use **Settings > Users** for profile View sources.
- **Settings > Providers** for provider availability and credentials.
- **Settings > Ingestion** for active scans and recent batches.
- **Settings > Review Queue** for items that need confirmation before they can appear in browse surfaces.

## Files Do Not Ingest

Confirm:

- The source path exists and is readable by the Engine process.
- The configured media types match the file extensions.
- The file finished copying before ingestion started.
- The file is not locked by another process.
- The extension is supported in [Media Types](../reference/media-types.md).

Use **Scan now** from Ingestion after changing library folder paths.

To inspect the durable queue directly:

- Open **Settings > Ingestion**.
- Call `GET /operations?queueName=ingestion` to see queued/running/retry rows.
- Call `GET /ingestion/batches/{batchId}/items` to see each file in a batch.

Useful operation stages are `discovered`, `settling`, `waiting_for_lock`,
`queued`, `hashing`, `parsing`, `scoring`, `registered`, `queued_identity`, and
`completed`. A file stuck in `waiting_for_lock` is still locked or actively
copying. A file in `interrupted` was running when the Engine stopped and will be
visible after restart.

## Capabilities Are Missing Or Stale

Use `GET /assets/{id}/capabilities` to inspect explicit readiness for one media
asset. Missing rows should not be treated as proof that lyrics, subtitles,
commercial markers, or provider output do not exist. The capability row is the
truth.

Common statuses:

- `pending`, `queued`, or `running`: automation is still working.
- `no_result`: the provider or plugin ran and found nothing.
- `blocked`: configuration, credentials, or a tool are missing.
- `failed_retryable`: the system will retry later.
- `failed_terminal` or `dead_lettered`: manual/admin action may be needed.
- `stale`: a provider, plugin, model, or capability version changed and output
  needs a rerun.

Optional capabilities such as lyrics, subtitles, and commercial skip detection
normally do not create Review Queue entries when they end as `no_result`.

## Items Need Review

Review Queue is expected when Tuvima cannot safely identify an item.

Common causes:

- Missing or conflicting embedded metadata.
- Ambiguous title/provider results.
- Low retail match score.
- Missing bridge identifiers for Wikidata resolution.
- Corrupt or unreadable files.
- Ambiguous MP3, M4A, MP4, MKV, AVI, or WEBM classification.

Open **Settings > Review Queue**, review the reason, and launch the shared editor from the item.

## Provider Lookups Fail

Check:

- The provider is enabled.
- Required credentials are present.
- The provider health/test action succeeds.
- Network access to the provider is available.
- Rate limits have not been exceeded.

Provider secrets belong in `config/secrets/` and should not be committed.

## Local AI Is Unavailable

Local AI is optional for first ingestion. If the AI status is unavailable:

- Confirm `config/ai.json` exists.
- Check whether the model role is missing, downloading, ready, loaded, or failed.
- Use **Settings > Local AI** for model lifecycle actions exposed by the Engine.
- Remember that saved feature flags do not guarantee active behavior if dependencies are missing.

## Docs Look Stale On GitHub Pages

The public site is generated from `docs/` by the `Docs` GitHub Actions workflow. If local docs look correct but Pages still shows old navigation, verify the workflow completed on `main`.

Local preview:

```powershell
./scripts/docs/build-docs.ps1
./scripts/docs/serve-docs.ps1
```

## Related

- [Getting Started](../tutorials/getting-started.md)
- [Your First Library](../tutorials/first-library.md)
- [Product Status](../product/status.md)
