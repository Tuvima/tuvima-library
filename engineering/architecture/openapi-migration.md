---
title: "OpenAPI Migration Plan"
summary: "Internal migration notes for keeping Engine API documentation current as OpenAPI tooling changes."
audience: "developer"
category: "architecture"
product_area: "api"
status: "internal"
---

# OpenAPI Migration Plan

## Current State

The Engine currently uses Swashbuckle through `AddEndpointsApiExplorer`, `AddSwaggerGen`, `UseSwagger`, and `UseSwaggerUI`. The document is named `v1` with title `Tuvima Library API`. Swagger UI is exposed only in Development.

## Target State

Move to the modern .NET OpenAPI stack after endpoint metadata is stable and tests cover the generated document. The target should preserve the `/swagger/v1/swagger.json` compatibility path or provide a documented transition path for clients.

## Package Changes

- Evaluate replacing Swashbuckle package references with the .NET OpenAPI package set used by .NET 10.
- Keep Swashbuckle until route metadata, auth metadata, and generated schema compatibility are verified.

## Endpoint Metadata Requirements

- Keep `WithName`, `WithSummary`, `WithTags`, and `Produces` metadata on Minimal API endpoints.
- Add explicit `ProducesProblem` metadata when endpoints return ProblemDetails.
- Keep auth requirements visible through endpoint metadata, including admin-only and role-based endpoints.

## Rollout Steps

1. Add a generated document snapshot/smoke test against the current Swashbuckle output.
2. Enable the .NET OpenAPI generator behind a branch-local experiment.
3. Compare generated paths, operation IDs, schemas, response types, and auth metadata.
4. Preserve or intentionally document any changed schema names.
5. Switch packages only after tests prove compatibility.

## Test Plan

- API smoke test for `/swagger/v1/swagger.json` in Development.
- Snapshot or structural checks for core route groups: System, Profiles, Library, Metadata, Playback, and Settings.
- Auth metadata checks for admin-only operations.

## Rollback Plan

Keep the Swashbuckle configuration intact until the replacement is merged. If generated document compatibility regresses, revert the package/configuration change and keep the migration document as the next-pass checklist.
