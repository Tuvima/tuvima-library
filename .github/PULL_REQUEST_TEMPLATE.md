## Summary

<!-- What does this change do? 1-3 bullet points. -->

-

## Why

<!-- The problem this solves. Link issues, one per line: Fixes #123 (or Part of #123 if only partly fixed). -->

## How to test

<!-- Steps a reviewer can follow to see it working. Say which checks you could not run and why. -->

1.

## Checklist

- [ ] It builds with warnings as errors: `dotnet build MediaEngine.slnx --no-restore --warnaserror`
- [ ] The tests for the projects I changed pass (the pull request runs every test; see [CONTRIBUTING.md](../blob/main/CONTRIBUTING.md#what-happens-when-you-open-a-pull-request))
- [ ] New or changed behavior has a test
- [ ] No secrets, API keys, local paths or private media are included
- [ ] New libraries are AGPLv3-compatible and credited (`clients/`: MPL 2.0 compatible, commits signed off)
- [ ] Documentation: linked the companion change in [tuvima-docs](https://github.com/Tuvima/tuvima-docs), or none is needed

<details>
<summary>Project rules a reviewer checks (tick what applies)</summary>

- [ ] No Vault/LibraryPage workflow, route, nav label, or media-management workbench was reintroduced
- [ ] Media editing uses the universal editor on the item's own page; review-only problems stay in Needs Review
- [ ] Database code uses `CreateConnection()` except startup/schema initialization
- [ ] Engine-to-Dashboard contract changes include the updated `*.approved.txt` snapshots
- [ ] Dashboard changes have visual evidence reviewed; capture images stay under ignored `.tmp/` and are not committed
- [ ] Docker changes were built (`docker build -t test .`)
- [ ] Tracked bugs: closure note posted on each linked issue after merge (plain English; UI bugs also mock and live side by side)

</details>
