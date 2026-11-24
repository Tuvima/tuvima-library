# Contributing to Tuvima Library

Help improve a local-first library for media people already own. Contributions can be a reproducible bug report, clearer documentation, a provider, a plugin or a code change.

## Code of Conduct

Follow the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md) in project spaces.

## Before you start

Open an [issue](https://github.com/Tuvima/tuvima-library/issues) to discuss substantial changes before implementing them. Check existing issues so related work can be coordinated.

The project uses the [AGPLv3](LICENSE). Contributions use those terms. The phone, TV and car apps under `clients/` are the exception: they use the Mozilla Public License 2.0 and ask for a sign-off line on each commit; read [clients/CONTRIBUTING.md](https://github.com/Tuvima/tuvima-library/blob/main/clients/CONTRIBUTING.md) before changing them. New tools must have compatible licenses, and new assets must retain their notices. Record added dependencies in the relevant package manifest and [attributions](https://docs.tuvima.com/products/library/policies/attributions/).

## Set up development

Install a .NET 10 SDK compatible with `global.json` (10.0.100 with feature-band roll-forward). Node 24 is needed for documentation and Release CSS verification; the application itself does not start through npm. Docker is optional unless your change affects container deployment.

```sh
git clone https://github.com/Tuvima/tuvima-library.git
cd tuvima-library
dotnet restore MediaEngine.slnx
```

Keep provider credentials in ignored `config/secrets/`, never in tracked examples. Before a build, stop running Engine and Dashboard processes to avoid locked outputs. Read [AGENTS.md](AGENTS.md) for project rules and [shared AI storage](https://docs.tuvima.com/products/library/guides/shared-ai-storage/) before enabling local AI.

Run the Engine first, from the repository root:

```sh
dotnet run --project src/MediaEngine.Api
```

After it listens on `http://localhost:61495`, run the Dashboard in another terminal:

```sh
dotnet run --project src/MediaEngine.Web
```

Open `http://localhost:5016`. The [developer setup guide](https://docs.tuvima.com/products/library/tutorials/dev-setup/) explains prerequisites, settings and recovery from setup problems.

## Make a focused change

Use a branch and keep unrelated changes separate.

| Prefix | Purpose |
| --- | --- |
| `feature/` | New behavior |
| `fix/` | Bug fix |
| `docs/` | Documentation |
| `chore/` | Tooling or CI |
| `refactor/` | Reorganization with unchanged behavior |

Before submitting code changes, build with warnings as errors and run the tests for the projects you changed. Each source project has one test project with the same name:

```sh
dotnet restore MediaEngine.slnx
dotnet build MediaEngine.slnx --no-restore --warnaserror
dotnet test tests/MediaEngine.Storage.Tests --no-build --filter "Category!=LiveProvider"
```

You do not need to run every test yourself. The whole suite takes over twenty minutes on one machine; the pull request runs it for you on several machines at once (see [What happens when you open a pull request](#what-happens-when-you-open-a-pull-request)). To run it anyway, use `dotnet test MediaEngine.slnx --no-build --filter "Category!=LiveProvider"`.

The pull request also verifies formatting and checks dependencies. To check those first:

```sh
dotnet format MediaEngine.slnx --verify-no-changes --no-restore
dotnet list MediaEngine.slnx package --vulnerable --include-transitive
```

After an intended Engine-to-Dashboard contract change, the Contracts snapshot tests fail and write the actual output next to each approved file as `tests/MediaEngine.Contracts.Tests/Fixtures/<name>.received.txt` (git-ignored). Accept the change by copying each `.received.txt` over its `.approved.txt`, or run `powershell -File tools\Update-ContractFixtures.ps1` (Windows PowerShell 5.1 or PowerShell 7), which runs the tests, copies the received files into place and re-runs the tests. No environment variables are needed. Review and commit the changed `*.approved.txt` files.

A change to the data structure is a new numbered upgrade step, never an edit to an existing one; follow [Changing the data store](https://docs.tuvima.com/products/library/develop/changing-the-data-store/).

Each test process works in its own temporary folder, `%TEMP%\tuvima-tests\<pid>-<time>`, which is deleted when the process exits; folders left by killed runs are removed at the next start. Fixtures that create SQLite files should clean up with `TestTemp.DeleteDatabase(path)` (`tests/Shared/TestTemp.cs`), which releases pooled connections and deletes the `-wal`/`-shm` files, rather than a bare `File.Delete` inside an empty `catch`.

CI excludes tests marked `Category=LiveProvider`; ordinary tests should not depend on a live provider or paid credential. Use the current workflow as the source for coverage and Release CSS checks.

For Docker changes, validate the image in a disposable environment:

```sh
docker build -t tuvima-test .
```

Do not change native runtime filtering just to make local verification pass. Keep generated QA output under ignored `.tmp/`. Capture relevant visual evidence for presentation changes; do not commit review screenshots.

## Change documentation

The public documentation lives in the [tuvima-docs](https://github.com/Tuvima/tuvima-docs) repository and is published at [docs.tuvima.com](https://docs.tuvima.com/). Read [Writing documentation](https://docs.tuvima.com/products/library/develop/writing-docs/) before you edit a page.

A change that needs documentation gets a companion proposed change in `tuvima-docs`; link it in your pull request (the template has a box for it). Keep dated engineering plans and verification reports in `engineering/`, outside published documentation.

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

Open a pull request against `main`, from a branch in your own fork. Explain the problem, resulting behavior and verification. Note any check you could not run and why. `CODEOWNERS` identifies `@shyfaruqi` as the repository owner.

## What happens when you open a pull request

The checks start by themselves and need nothing from you: no account, key or secret. Pull requests from forks run with read-only access and never see the project's secrets, so nothing in them can publish, sign or merge. If this is your first contribution, a maintainer approves the first run before it starts.

| Check | What it does | Typical time |
| --- | --- | --- |
| **Build & Test** (required) | Builds with warnings as errors, checks the format of the C# files you changed, publishes the Dashboard, checks for vulnerable libraries and runs every test over six machines at once. It passes only when all of that passes. | about 8 minutes |
| Native clients | Builds the phone, TV and car apps. Runs only when you change `clients/` or the Engine routes they use. | about 5 minutes |
| Docker image | Builds the container image and starts it. Runs only when something the image is made from changed. | 10 to 15 minutes |

A change that only touches `engineering/` skips the build and tests and passes in under a minute.

`Build & Test` is the only check `main` requires. A maintainer reviews the change and merges it once that check is green; the automatic merge used for the Tuvima team's own branches never applies to forks. If a check fails, open it on the pull request page: the failing step prints what went wrong. Push a fix to the same branch and the checks run again.

The deeper checks run at set points, not on your pull request, so they never slow you down:

| When | What runs |
| --- | --- |
| Every merge to `main` | Everything above again, plus a format check of the whole solution, code coverage and the coverage threshold |
| Every night (06:00 UTC) | The same deeper run, so a problem that slipped past is seen within a day |
| On demand | A maintainer can start the deeper run on any branch (Actions, CI, Run workflow) |
| Weekly | Read-only checks against live metadata providers, which need the project's keys |
| Before a batch of work is called verified | A full run on Windows with real media, by the Tuvima team |

The application is Early Access; avoid claiming that code presence alone proves a deployment or user journey is accepted. Update affected docs and current repository guidance in the same change. The retired `.agent/` mirror is not synchronized.

## Project architecture

The Engine is `src/MediaEngine.Api`; the Dashboard is `src/MediaEngine.Web`. Domain owns core models, Contracts owns wire types, Storage owns SQLite persistence, and Providers, Processors, Ingestion and Intelligence own the media pipeline. Intelligence uses the Priority Cascade. Identity owns accounts and access, while Plugins defines the extension contracts every kind of plugin shares.

Start with [AGENTS.md](AGENTS.md) and the [technical overview](https://docs.tuvima.com/products/library/architecture/technical-overview/). Do not add direct data access to Dashboard components.

## Report bugs

Use [Issues](https://github.com/Tuvima/tuvima-library/issues) for non-security bugs. Include the commit, deployment type, reproduction steps and relevant logs with private data removed.

## Security issues

Follow [SECURITY.md](SECURITY.md). Report possible vulnerabilities privately, not through public issues.

## Questions

Ask in [Issues](https://github.com/Tuvima/tuvima-library/issues). For installation help, include the platform and the exact failed step without posting credentials or private media.
