# Architecture decisions

One short record per major decision: what, why, when, and when to revisit. New decisions get the next number. If a decision is replaced, add a new record and mark the old one "Superseded by NNNN".

| # | Decision | Date | Status |
|---|---|---|---|
| [0001](0001-sqlite-and-dapper.md) | SQLite with hand-written SQL (Dapper), not Entity Framework | 2026-03-17 | Accepted — revisit planned |
| [0002](0002-first-party-ui-components.md) | First-party `App*` UI components; MudBlazor removed | 2026-10-06 | Accepted |
| [0003](0003-storage-epoch-guid-blob.md) | Storage epoch `guid-blob-v1`; reset instead of migrate | 2026-05-30 | Accepted |
| [0004](0004-contracts-own-the-wire.md) | `MediaEngine.Contracts` alone owns Engine↔Dashboard wire types | 2026-04-24 | Accepted |
| [0005](0005-local-first.md) | Local-first; no Tuvima-hosted account | 2026-02-27 | Accepted |
| [0006](0006-management-workspace-retired.md) | All-in-one management workspace retired; shared editor + Needs Review | 2026-04-25 | Accepted |
| [0007](0007-accounts-and-profiles-access.md) | Accounts own access; profiles own experience; no guest keys | 2026-09-08 | Accepted |
| [0008](0008-public-docs-vs-engineering-docs.md) | Public docs site in `docs/`; internal planning in `engineering/` | 2026-10-17 | Accepted |

Also see the published [hybrid asset storage decision](../../../docs/architecture/storage-policy-adr.md).
