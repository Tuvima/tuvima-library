# Contributing to Tuvima Library

Help improve a local-first library for media people already own. Contributions can be a reproducible bug report, clearer documentation, a provider, a plugin or a code change.

## Code of Conduct

Follow the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md) in project spaces.

## Before you start

Open an [issue](https://github.com/Tuvima/tuvima_library/issues) to discuss substantial changes before implementing them. Check existing issues so related work can be coordinated.

The project uses the [AGPLv3](LICENSE). Contributions use those terms. New tools must have compatible licenses, and new assets must retain their notices. Record added dependencies in the relevant package manifest and [attributions](https://tuvima.github.io/tuvima_library/reference/attributions/).

## Set up development

Install a .NET 10 SDK compatible with `global.json` (10.0.100 with feature-band roll-forward). Node 24 is needed for documentation and Release CSS verification; the application itself does not start through npm. Docker is optional unless your change affects container deployment.

```sh
git clone https://github.com/Tuvima/tuvima_library.git
cd tuvima_library
dotnet restore MediaEngine.slnx
```

Keep provider credentials in ignored `config/secrets/`, never in tracked examples. Before a build, stop running Engine and Dashboard processes to avoid locked outputs. Read [AGENTS.md](AGENTS.md) for project rules and [shared AI storage](https://tuvima.github.io/tuvima_library/guides/shared-ai-storage/) before enabling local AI.

Run the Engine first, from the repository root:

```sh
dotnet run --project src/MediaEngine.Api
```

After it listens on `http://localhost:61495`, run the Dashboard in another terminal:

```sh
dotnet run --project src/MediaEngine.Web
```

Open `http://localhost:5016`. The [developer setup guide](https://tuvima.github.io/tuvima_library/tutorials/dev-setup/) explains prerequisites, settings and recovery from setup problems.

## Make a focused change

Use a branch and keep unrelated changes separate.

| Prefix | Purpose |
| --- | --- |
| `feature/` | New behavior |
| `fix/` | Bug fix |
| `docs/` | Documentation |
| `chore/` | Tooling or CI |
| `refactor/` | Reorganization with unchanged behavior |

Before submitting code changes, run the repository gate:

```sh
dotnet restore MediaEngine.slnx
dotnet build MediaEngine.slnx --no-restore
dotnet test MediaEngine.slnx --no-build
```

CI additionally treats build warnings as errors, verifies formatting and checks dependencies:

```sh
dotnet format MediaEngine.slnx --verify-no-changes --no-restore
dotnet list MediaEngine.slnx package --vulnerable --include-transitive
```

After an intended Engine-to-Dashboard contract change, regenerate the approved fixtures with `powershell -ExecutionPolicy Bypass -File tools\Update-ContractFixtures.ps1` (works in Windows PowerShell 5.1 and PowerShell 7) and commit the changed `tests/MediaEngine.Contracts.Tests/Fixtures/*.approved.txt` files.

CI excludes tests marked `Category=LiveProvider`; ordinary tests should not depend on a live provider or paid credential. Use the current workflow as the source for coverage and Release CSS checks.

For Docker changes, validate the image in a disposable environment:

```sh
docker build -t tuvima-test .
```

Do not change native runtime filtering just to make local verification pass. Keep generated QA output under ignored `.tmp/`. Capture relevant visual evidence for presentation changes; do not commit review screenshots.

## Change documentation

Read [Writing documentation](https://tuvima.github.io/tuvima_library/develop/writing-docs/). Author pages in `docs/`; `website/` contains the Astro Starlight site and explicit publication/navigation manifest.

```powershell
pwsh -File scripts/docs/build-docs.ps1 -InstallDependencies
pwsh -File scripts/docs/serve-docs.ps1
```

Moving a page requires a route decision and redirect where the old address should remain useful. Add new pages to `website/publication.json`. Keep dated engineering plans and verification reports in `engineering/`, outside published documentation.

Existing product screenshots have been removed pending better replacements. Do not add empty image placeholders. Logos and other purposeful documentation assets remain available.

## Commit style

Use [Conventional Commits](https://www.conventionalcommits.org/), for example:

```text
feat: add a metadata provider
fix: preserve episode identity in progress
docs: explain persistent Docker storage
chore: update the documentation toolchain
```

Stage specific files, keep secrets and generated outputs out of the change, and describe the concrete result.

## Pull requests and review

Open a pull request against `main`. Explain the problem, resulting behavior and verification. Note any check you could not run and why. `CODEOWNERS` identifies `@shyfaruqi` as the repository owner.

The application is Early Access; avoid claiming that code presence alone proves a deployment or user journey is accepted. Update affected docs and current repository guidance in the same change. The retired `.agent/` mirror is not synchronized.

## Project architecture

The Engine is `src/MediaEngine.Api`; the Dashboard is `src/MediaEngine.Web`. Domain owns core models, Contracts owns wire types, Storage owns SQLite persistence, and Providers, Processors, Ingestion and Intelligence own the media pipeline. Intelligence uses the Priority Cascade. Identity owns accounts and access, while Plugins defines in-process extension contracts.

Start with [AGENTS.md](AGENTS.md) and the [technical overview](https://tuvima.github.io/tuvima_library/architecture/technical-overview/). Do not add direct data access to Dashboard components.

## Report bugs

Use [Issues](https://github.com/Tuvima/tuvima_library/issues) for non-security bugs. Include the commit, deployment type, reproduction steps and relevant logs with private data removed.

## Security issues

Follow [SECURITY.md](SECURITY.md). Report possible vulnerabilities privately, not through public issues.

## Questions

Ask in [Issues](https://github.com/Tuvima/tuvima_library/issues). For installation help, include the platform and the exact failed step without posting credentials or private media.
