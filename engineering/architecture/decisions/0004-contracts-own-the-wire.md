# 0004 — `MediaEngine.Contracts` alone owns Engine↔Dashboard wire types

**Date:** 2026-04-24 (`f599121e`, display contracts centralised) · **Status:** Accepted

## What
Every type sent between the Engine and the Dashboard (HTTP and SignalR) lives in `src/MediaEngine.Contracts/`. The Engine maps its internal models into Contracts explicitly; the Dashboard reads Contracts directly and never keeps its own copy. One frozen exception: two events in the Dashboard's `IntercomEvents.cs`.

## Why
- **Reliability:** a renamed field can't silently break the Dashboard; `BoundaryContractGuardrailTests` and `WireContractSnapshotTests` catch it.
- **Extensibility:** native clients (`clients/`) and third-party apps can rely on one documented shape.

## Trade-offs accepted
Extra mapping code at the Engine boundary. Intentional wire changes need a fixture refresh (`tools/Update-ContractFixtures.ps1`) and are treated as high-risk work.
