---
title: "Configuration Safety"
description: "Architecture rules for Tuvima configuration ownership, validation, and runtime safety."
audience: "developer"
category: "architecture"
product_area: "configuration"
status: current
---

# Configuration Safety

## In this page

Architecture rules for Tuvima configuration ownership, validation, and runtime safety.

## Where this lives in the code

- `src/MediaEngine.Storage/ConfigurationDirectoryLoader.cs`
- `src/MediaEngine.Domain/Configuration`
- `config/schemas`

Tuvima Library keeps runtime configuration under `config/`. Wave 5 adds defensive loading rules for the highest-risk files: `core.json`, provider configs, `hydration.json`, `scoring.json`, `maintenance.json`, `media_types.json`, `pipelines.json`, and `ui/palette.json`.

The JSON schemas in `config/schemas/` document the expected shape. Runtime enforcement is intentionally typed validation in `ConfigurationDirectoryLoader`, so first-run defaults and legacy migration keep working without adding a large schema dependency.

If an existing primary config file is malformed or fails validation, the loader tries the matching `.bak`. If the backup is valid, it is restored and used. If both are invalid, startup or load fails with `ConfigValidationException`, including the file path, schema name, and sanitized validation messages. Secret field values are not included in exception text.

Hot reload is bounded. The Engine watches non-secret JSON config and reloads only after validation; invalid live edits are rejected and the last-known-good value stays active. The Dashboard watches `config/ui/palette.json` and applies valid palette changes without restart. Startup-only settings, including database path, auth wiring, rate limiter construction, and service registration, still require restart.

Editors that can stage user changes now include Blazor navigation guards. Users are warned with: "You have unsaved changes. Leave without saving?"

Accessibility guardrails cover the shell skip link, main landmark, drawer/dialog roles, modal labels, and accessible close buttons on the touched drawer/dialog/editor surfaces.

### Retail matrix follow-up

Retail identity confidence uses typed per-media `scoring.scopes` in `config/pipelines.json` and synchronized settings defaults. Eight scopes replace the global retail weights: books, audiobooks, movies, TV series, TV episodes, music tracks, music albums and comic issues. Missing optional values redistribute their weight and show Not provided; required creator evidence fails closed. Genre contributes nothing. Wrong-kind and structural gates apply after bonuses. Additive nullable `field_scores` exposes the complete durable breakdown. Accept/review thresholds remain 0.90/0.65. See `docs/architecture/scoring-and-cascade.md` for the normative matrices and the distinction from canonical trust scoring. No legacy fallback or data conversion is supported.
