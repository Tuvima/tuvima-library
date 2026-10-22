# Architecture decisions

One short record per major decision: what, why, when, and when to revisit. New decisions get the next number. If a decision is replaced, add a new record and mark the old one "Superseded by NNNN".

| # | Decision | Date | Status |
|---|---|---|---|
| [0001](0001-sqlite-and-dapper.md) | SQLite with hand-written SQL (Dapper), not Entity Framework | 2026-03-17 | Accepted — reaffirmed 2026-10-17 after the EF benchmark |
| [0002](0002-first-party-ui-components.md) | First-party `App*` UI components; MudBlazor removed | 2026-10-06 | Accepted |
| [0003](0003-storage-epoch-guid-blob.md) | Storage epoch `guid-blob-v1`; reset instead of migrate | 2026-05-30 | Accepted |
| [0004](0004-contracts-own-the-wire.md) | `MediaEngine.Contracts` alone owns Engine↔Dashboard wire types | 2026-04-24 | Accepted |
| [0005](0005-local-first.md) | Local-first; no Tuvima-hosted account | 2026-02-27 | Accepted |
| [0006](0006-management-workspace-retired.md) | All-in-one management workspace retired; shared editor + Needs Review | 2026-04-25 | Accepted |
| [0007](0007-accounts-and-profiles-access.md) | Accounts own access; profiles own experience; no guest keys | 2026-09-08 | Accepted |
| [0008](0008-public-docs-vs-engineering-docs.md) | Public docs site in `docs/`; internal planning in `engineering/` | 2026-10-17 | Accepted |
| [0009](0009-open-from-the-start.md) | Open from the start; apps always allowed | 2026-10-17 | Accepted |
| [0010](0010-helper-tools-report-hold-offer.md) | Missing helper tools: report them, hold the files, offer a checked download | 2026-10-18 | Accepted |
| [0011](0011-personal-activity-record-and-scopes.md) | Personal activity is its own lasting record; one scope check decides who sees whose | 2026-10-19 | Accepted |
| [0012](0012-live-tv-stays-possible.md) | Live TV is not built, and nothing in the plugin platform closes the door | 2026-10-21 | Accepted |
| [0013](0013-four-kinds-of-plugin.md) | Plugins come in four kinds, and nothing runs until it is approved | 2026-10-21 | Accepted |
| [0014](0014-one-scheduled-list-for-every-file-write.md) | Every write into a file goes through one scheduled list | 2026-10-22 | Accepted |

Also see the published [hybrid asset storage decision](../../../docs/architecture/storage-policy-adr.md).
