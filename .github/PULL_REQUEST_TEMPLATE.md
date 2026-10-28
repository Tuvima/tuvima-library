## Summary

<!-- What does this PR do? 1-3 bullet points. -->

-

## Motivation

<!-- Why is this change needed? Link to an issue if applicable: Fixes #123 -->

## Changes

<!-- List the key changes. Group by area if touching multiple parts. -->

### Engine (`src/MediaEngine.Api`, `Domain`, `Storage`, etc.)

-

### Dashboard (`src/MediaEngine.Web`)

-

### Configuration (`config/`)

-

## How to test

<!-- Step-by-step instructions for reviewers to verify this works. -->

1.

## Checklist

- [ ] `dotnet build MediaEngine.slnx --no-restore` passes with 0 errors
- [ ] `dotnet test MediaEngine.slnx --no-build` passes
- [ ] Product docs updated if behavior, navigation, or product concepts changed
- [ ] No Vault/LibraryPage workflow, route, nav label, or media-management workbench was reintroduced
- [ ] Media editing uses the universal editor on the item's own page
- [ ] Review-only problems stay in the Review Queue
- [ ] Database code uses `CreateConnection()` except startup/schema initialization
- [ ] Documentation updated in tuvima-docs when behavior changed (link the proposed change there)
- [ ] Dashboard visual evidence reviewed; capture images stay under ignored `.tmp/` and are not committed
- [ ] Docker build succeeds (`docker build -t test .`)
- [ ] New dependencies are AGPLv3-compatible (see `CLAUDE.md` section 6.1)
- [ ] No secrets, API keys, or local paths committed
- [ ] Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `chore:`)
