---
title: "API Boundary Debt"
summary: "Known API boundary cleanup items and migration guidance for moving SQL-heavy endpoint behavior into services."
audience: "developer"
category: "architecture"
product_area: "api"
status: "internal"
---

# API Boundary Debt

Wave 7 removed direct database access from `SystemEndpoints.cs` by moving orphan-image reference queries into `OrphanImageReferenceReadService`.

Sprint 2 removed profile overview SQL from `ProfileEndpoints.cs`, moved
person and character credit projections into `PersonCreditReadService`, and
carved the small operational aggregate portion of `GET /library/overview` into
`LibraryOverviewReadService`.

Sprint 4 moved the first collection read routes behind focused API read
services: `CollectionBrowseReadService` for `GET /collections/`,
`CollectionSearchReadService` for `GET /collections/search`, and
`CollectionMediaLookupReadService` for `GET /collections/media-lookup` plus
curated item metadata projection. It also moved media editor navigator,
membership suggestion, preview, and apply logic into
`MediaEditorNavigationReadService`, and moved `GET /metadata/claims/{entityId}`
into `MetadataClaimHistoryReadService`.

## Compliant Endpoint Pattern

Sprint 1 reinforced the guardrails around endpoint boundaries but did not pay
down the full legacy SQL allowlist below. New and touched endpoint code should
look like these existing patterns:

- `ProgressEndpoints.cs` validates route/query input and delegates journey
  projection work to `IJourneyReadService`.
- `IngestionEndpoints.cs` keeps batch item projection behind
  `IIngestionBatchReadService` instead of building SQL in the endpoint.
- `SystemEndpoints.cs` delegates orphan-image reference checks to
  `IOrphanImageReferenceReadService`.
- `PersonEndpoints.cs` uses focused person read services for aliases,
  presence, works, scoped person summaries, and person credit projections.
- `ProfileEndpoints.cs` delegates account overview projection work to
  `IProfileOverviewReadService`.
- `LibraryEndpoints.cs` delegates overview operational aggregates to
  `ILibraryOverviewReadService`; broader library browse and management SQL
  remains legacy debt.

The endpoint should stay an HTTP adapter: validate input, call a repository or
read service, preserve cancellation flow, and return the established DTO shape.
SQL belongs in Storage repositories or focused API read services that use
`IDatabaseConnection.CreateConnection()` with a short-lived disposed connection.

## Remaining Allowlist

The endpoint direct-database-access and direct-SQL allowlists are empty.
`ArchitectureBoundaryTests` now require endpoint handlers to remain HTTP
adapters. Further decomposition can still make broad endpoint files easier to
navigate, but it must not restore SQL or `IDatabaseConnection` access there.
