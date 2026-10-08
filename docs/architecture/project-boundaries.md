---
title: "Project Boundaries"
description: "Rules for keeping Domain, Engine, Storage, Providers, Ingestion, and Dashboard dependency boundaries clean."
audience: "developer"
category: "architecture"
product_area: "system"
---
Tuvima Library keeps project dependencies pointed inward so UI and endpoint work does not accidentally couple to SQLite or other infrastructure details.

- `MediaEngine.Domain` is the core model and pure domain rules. It also owns
  shared configuration shapes and inward-facing ports used by infrastructure.
  Keep it dependency-light.
- `MediaEngine.Contracts` contains DTOs that are safe for API, Dashboard, and other clients to share.
- `MediaEngine.Application` contains use-case/read-service interfaces and their
  request/response models when those contracts cross an implementation boundary.
- `MediaEngine.Storage` owns SQLite, Dapper, schema behavior, and every concrete
  repository implementation.
- `MediaEngine.Api` is the composition root. It wires Application/Domain
  abstractions to infrastructure through focused `AddTuvima*` registration
  modules. API read-service implementations may live here, but their interfaces
  do not share the implementation file.
- `MediaEngine.Web` talks to the Engine through HTTP and SignalR. It must not reference `MediaEngine.Storage`.

`MediaEngine.Providers`, `MediaEngine.Intelligence`, `MediaEngine.Processors`,
and `MediaEngine.AI` depend on inward contracts rather than
`MediaEngine.Storage`. New persistence implementations must not leak into those
projects.

Domain aggregates expose child collections and property bags as read-only views.
Normal mutation must go through named aggregate methods, such as `AddWork`,
`AddEdition`, or `SetExternalIdentifier`. Repository materialization should use
those same methods, or an explicitly named hydration helper if bulk loading ever
requires one. Do not make Domain depend on serialization, Dapper, SQLite, HTTP,
or UI concerns to support persistence.

Aggregate lifecycle state is typed in Domain. `Work` identity uses
`WikidataLinkStatus` and `WorkMatchLevel`; collection definition state uses
`CollectionType`, `CollectionScope`, `CollectionResolution`,
`CollectionMatchMode`, `CollectionSortDirection`, and
`CollectionUniverseStatus`. Their setters are private. New identity decisions
use `Work.LinkToWikidata`, while repositories use the explicitly named restore
methods. `AggregateStateSerializer` is the only conversion point for the stable
SQLite/API string values; unknown persisted values fail fast.

Endpoint methods should stay thin: validate route/query/body inputs, call a service or query object, and return HTTP results. SQL, row mapping, and fallback query rules belong in Storage or clearly named API read services.

Concrete persistence types use the `Repository` suffix and live in Storage.
Non-persistence API implementations use a purpose-specific `Service` suffix.
Do not introduce new `Store` or `DataService` types as ambiguous persistence
facades.

Engine startup work that can fail or be cancelled runs through an awaited hosted
startup service. Do not start unobserved initialization tasks from `Program.cs`
or a service registration callback.

