---
title: "API Boundaries"
description: "Rules for keeping Engine HTTP endpoints thin, tested, and separated from storage implementation details."
audience: "developer"
category: "architecture"
product_area: "api"
---
Engine endpoint files are HTTP adapters. They should validate route/query/body input, call a service or repository, and return the existing response shape.

## Wire-Contract Ownership

`MediaEngine.Contracts` is the sole owner of product-defined types serialized
over non-frozen HTTP or SignalR boundaries. This includes request bodies,
success responses, nested payload types, and server-push event payloads.
Endpoint-local projection rows and Application read models may support a
boundary, but they must not themselves become the public wire shape.

When a Domain, Application, provider, or persistence model is not already the
wire contract, the API must use an explicit boundary mapper. The mapper is where
field selection, naming, defaults, and intentional completeness differences are
made visible. Do not serialize an internal model directly or preserve two
field-for-field public DTO definitions in different projects.

The Dashboard deserializes Contracts types directly. A Dashboard-only type may
wrap or compose a contract for presentation state, formatting, selection, or
component behavior, but it must use an explicit mapper and must not duplicate
the JSON contract. Types under `MediaEngine.Web/Models/ViewDTOs` are
presentation types, not a second wire-contract owner.

The only frozen exception is the exact Universe/Chronicle SignalR pair
`LoreDeltaDiscoveredEvent` and `UniverseEnrichmentProgressEvent`, currently
defined in `MediaEngine.Web/Services/Integration/IntercomEvents.cs`. This
exception does not cover Universe HTTP payloads, does not authorize additional
fields or types, and must not grow. All other Universe and Chronicle HTTP and
SignalR boundary types follow normal Contracts ownership.

## Rules

- New endpoint files must not inject or use `IDatabaseConnection` directly.
- New endpoint files must not contain SQL statements or command construction.
- SQL belongs in Storage repositories or focused API read services when the projection is API-specific.
- Repository and read-service data access should use `IDatabaseConnection.CreateConnection()` and dispose the connection with `using`.
- Web/Dashboard code must not reference `MediaEngine.Storage` implementation types and must not contain SQL.
- Domain remains independent of API, Web, Storage, Providers, Ingestion, Processors, and AI.
- Preserve each contract's established `[JsonPropertyName]`, JSON casing,
  defaults, nullability, collection shape, and editor-required mutability.
- Adding a public HTTP or SignalR DTO outside `MediaEngine.Contracts` is a
  boundary regression, even when its fields currently match a Contracts type.

## Adding A New Endpoint

1. Define or reuse the request and response types in
   `MediaEngine.Contracts/<Concern>/`.
2. Put persistence in a repository or read service.
3. Add an explicit boundary mapper when the service returns an internal model.
4. Inject that service into the endpoint handler.
5. Preserve cancellation token flow.
6. Add endpoint behavior tests, JSON contract tests, and service/repository
   tests for non-trivial queries.

Current focused API read-service examples include `ProfileOverviewReadService`
for account overview projections, `PersonCreditReadService` for person and
character credit projections, `LibraryOverviewReadService` for small
operational overview aggregates, `CollectionSearchReadService` and
`CollectionMediaLookupReadService` for collection browse/search projections,
`MediaEditorNavigationReadService` for editor navigator projections, and
`MetadataClaimHistoryReadService` for claim history aggregation.

The current direct database endpoint allowlist is legacy debt. It should shrink over time and must not be treated as a pattern for new code.

## Wire Guardrails

The Contracts test project enforces this boundary in complementary ways:

- `BoundaryContractGuardrailTests` scans endpoint `Accepts`/`Produces`, Web JSON
  generic targets, and SignalR subscriptions. Non-Contracts product types fail
  unless they are one of the two exact frozen events above.
- `WireContractSnapshotTests` protects property names, casing, nesting, and
  representative serialized shapes.
- Focused contract round-trip and shape tests protect defaults, mutability, and
  fields whose old API and Dashboard mirrors were incomplete.

Contract consolidation intentionally retained the complete union of fields used
by either side of the old boundary. Examples include ingestion `count_unit`,
person and person-backed collection `roles`, the full AI settings dictionaries
and resource `cpu_pressure`, and complete collection, playback/text-track, and
plugin manifest/capability/permission/job/catalog fields. These are
completeness corrections to the canonical contract, not permission to introduce
parallel compatibility DTOs.
