# Protected consumers and historical reference exceptions

The protected source hashes and exact incoming references are in `reference-ledger.json`. Preserve `docs/reference/approved-plugins.json` and `docs/reference/wikidata-property-map.md` byte-for-byte. No runtime/test/config consumer needs a change when these paths remain stable.

| Consumer | Baseline reference | Required decision |
| --- | --- | --- |
| `src/MediaEngine.Domain/Configuration/CoreConfiguration.cs:154` | Raw GitHub approved-plugin catalogue URL | Preserve consumer and catalogue path; no runtime edit. |
| `config/core.json:69` | Same catalogue URL | Preserve configuration and catalogue path. |
| `docker/config/core.json:65` | Same catalogue URL | Preserve configuration and catalogue path. |
| `tests/MediaEngine.Storage.Tests/PipelineBugRegressionTests.cs:193` | Comment naming the property map | Preserve path and comment; this is not proof of a runtime file loader. |
| `src/MediaEngine.Web/CLAUDE.md:7` | Player-update verification record | Preserve new user-authored authority. Integrator may repair this documentation reference while retaining its full meaning, or explicitly exempt its historical spelling if source guidance is frozen. |
| `src/MediaEngine.Web/CLAUDE.md:145` | CSS-ownership acceptance record | Same documentation-only decision. Runtime sources are unchanged. |
| `scripts/css/README.md:34,57` | CSS cleanup and migration evidence | Repoint maintained documentation references to `engineering/reports/`; do not delete retained reports. |
| `scripts/visual-qa/home-media-cards/README.md:70` | Old `docs/reports` capture output example | Future QA captures belong under ignored `.tmp/`; repair the maintained example rather than deleting the script/evidence. |
| `tools/reports/player-surfaces-2026-10-01/README.md:63` | Historical playback evidence link | The QA directory is outside cleanup scope. An exact historical-path exception is valid; rewriting source/evidence in that directory is not necessary to this overhaul. |

An exact historical-path exception is needed for inventory manifests, the captured MkDocs baseline and retained execution records that quote the old layout or historical commands. Broad exceptions for all live docs/scripts would hide broken references and are not appropriate. `.agent/` is retired and must not become a synchronization target merely because its historical file mentions a moved record.

Local `.codex/context` reference state is separately recorded in `reference-ledger.json`. Its five generated context files must be regenerated through the maintained script after cleanup. They are not hand-maintained authority, and ignored local screenshots/logs elsewhere under `.codex/` are not tracked cleanup targets.

## Plain-English completion summary

The files the app reads keep their original locations and contents. Maintainer guidance can point to the retained engineering records after relocation, while old evidence can honestly retain historical path spellings when changing it would exceed the cleanup scope.
