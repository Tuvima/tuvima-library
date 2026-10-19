---
paths:
  - "src/**"
  - "tests/**"
---

# Architecture boundaries

- Domain stays independent of Web, API, Storage, Providers, Ingestion, Processors, AI, and UI packages. UI consumes view models, Contracts, and typed clients — never storage models.
- Domain aggregates expose children, property bags, and lifecycle state read-only; mutate through explicit aggregate methods (`Work.LinkToWikidata`, `Collection.SetVisibility`, `Collection.ChangeResolution`, …). Persisted aggregate enums convert only through `AggregateStateSerializer`; unknown values fail fast.
- **Wire ownership:** `src/MediaEngine.Contracts/` solely owns types serialized over HTTP/SignalR. Engine endpoints map explicitly into Contracts; the Dashboard deserializes Contracts directly and never keeps a second JSON DTO. Preserve `[JsonPropertyName]`, casing, defaults, nullability, and collection shape. Only frozen exception: `UniverseEnrichmentProgressEvent` in `Services/Integration/IntercomEvents.cs` — never extend it. Enforced by `BoundaryContractGuardrailTests` and `WireContractSnapshotTests`; when a wire snapshot changes intentionally, refresh fixtures with `pwsh -File tools/Update-ContractFixtures.ps1`.
- No silent `catch { }`: best-effort failures need a justification comment or guardrail allowlist entry; user-visible failures need logging and degraded/error UI.
- Dashboard service credentials resolve at request send time, never at typed-client construction; missing/invalid/rotated bundles fail closed with recoverable errors. No anonymous requests or stale credentials; the service header and any View signature share one credential snapshot.
- Before changing C# for behaviour that looks configurable, check `config/` first (provider behaviour is JSON-driven in `config/providers/`; secrets in gitignored `config/secrets/`).
- Guardrail tests (`*GuardrailTests.cs`, allowlists in `*GuardrailAllowlist.txt`) encode these rules. Fix the code, not the guardrail; extend an allowlist only with a justification.

## Where new non-Dashboard code goes

| New code | Location |
|---|---|
| Engine↔Dashboard type | `Contracts/<Concern>/` (+ boundary tests) |
| Read-model DTO | `Application/ReadModels/` |
| Query contract | `Application/Services/IReadServices.cs` |
| Config shape / inward port | `Domain/Configuration/` or `Domain/Contracts/` |
| SQLite repository | `Storage/` (`Repository` suffix) |
| API service without persistence | `Api/Services/` (`Service` suffix, separate interface) |
| Registration | a focused `Api/DependencyInjection/Tuvima*ServiceCollectionExtensions.cs` |
| Plugin | new `src/MediaEngine.Plugin.<Name>/` implementing `ITuvimaPlugin` |
