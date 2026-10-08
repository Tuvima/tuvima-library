---
title: "Keep repository builds small"
description: "Ordinary development builds copy native libraries for one platform."
audience: "developer"
category: "guide"
product_area: "platform"
---
Ordinary development builds copy native libraries for one platform. An explicit
`dotnet build -r <rid>` or publish runtime takes precedence; otherwise the installed
SDK's host platform is used. The SDK's [RID fallback graph](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog)
supplies compatible parent runtimes. Managed and unscoped files remain available.
This applies before build and publish copies, without changing the usual
`bin/Debug/net10.0` launch paths.

`Directory.Build.targets` enforces this policy. A build fails if obsolete foreign
platform files remain in its output. Clean that output once, then rebuild. CI runs
`node --test tests/build/*.test.mjs` to exercise host selection, explicit Linux and
ARM64 targets, publish filtering, portable opt-in, and stale-output detection.

## Remove obsolete generated output

Stop the repository's Engine and Dashboard first. From the repository root:

```powershell
pwsh -File tools/Clean-RepoOutputs.ps1 -WhatIf
pwsh -File tools/Clean-RepoOutputs.ps1
```

The default removes immediate `bin` and `obj` directories belonging to tracked
projects, including all old build configurations. The next restore/build recreates
them. To also remove the script's explicitly listed old QA build sandboxes, old
publish folders, test results, and generated documentation site:

```powershell
pwsh -File tools/Clean-RepoOutputs.ps1 -IncludeQa -WhatIf
pwsh -File tools/Clean-RepoOutputs.ps1 -IncludeQa
```

Use `-QaOnly` to remove that same listed QA output without removing the current
project `bin`/`obj` directories.

The script validates the entire deletion plan before removing directories. It
rejects targets outside this checkout, tracked files, symlinks, junctions, and
unknown reparse points. Recognized OneDrive cloud placeholders are supported.
It preserves library data, original media, models, configuration, Git history,
unmarked QA fixtures and database backups, review evidence outside the listed
build sandboxes, temporary notes/patches, and documentation tool environments.

Use the default Debug configuration for normal verification rather than creating
another named configuration each time. Keep QA builds in ignored `.tmp/` folders
and remove compiled copies after acceptance. Retain the evidence still needed for
review; the cleaner deliberately has no blanket deletion of `.tmp/` or `tools/reports/`.

## Packaging for other platforms

Choose the actual deployment RID for platform-specific publishing:

```powershell
dotnet publish src/MediaEngine.Api -c Release -r linux-x64 --self-contained false
```

If one distribution intentionally must carry every packaged platform, opt in:

```powershell
dotnet publish src/MediaEngine.Api -c Release -p:TuvimaKeepAllRuntimeAssets=true
```

That portable opt-in can produce a much larger folder. Do not use it for everyday
builds. It does not disable the separate shared AI dependency policy; see
[shared AI storage](shared-ai-storage.md).

The global NuGet cache is separate from this checkout and may legitimately contain
packages for many platforms. This policy prevents repeatedly copying that catalog
into every app, test, and QA output.
