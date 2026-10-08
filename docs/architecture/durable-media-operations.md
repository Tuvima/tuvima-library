---
title: "Durable media operations"
description: "How durable operations, leases and outcomes drive recovery and truthful ingestion progress."
audience: developer
category: architecture
product_area: ingestion
status: current
---

# Durable Media Operations

## In this page

How durable operations, leases and outcomes drive recovery and truthful ingestion progress.

## Where this lives in the code

- `src/MediaEngine.Storage/MediaOperationRepository.cs`
- `src/MediaEngine.Storage/MediaOperationTracker.cs`
- `src/MediaEngine.Storage/IngestionBatchActivitySql.cs`

Tuvima Library tracks operational truth in durable rows instead of inferring it
from missing artifacts.

## Batch recovery and presentation

`IngestionBatchActivitySql` supplies the shared activity predicate for the batch repository, presentation queries, history, and completion reconciliation. Outstanding identity jobs or media operations keep their original batch active even if its stored status predates a restart. A long wait or expired lease is not a terminal outcome. Startup requeues intermediate identity states, including unleased enrichment work from the previous process, before identity workers begin; it emits progress for all active batches without a recent-history limit.

Progress counts distinct skipped input paths even when the duplicate points to an asset already identified in that batch. A repeated log for an already-handled input path does not add another settled file. Ingestion cards and drawers show added counts only; provider container totals do not drive per-item progress.

## Core Tables

`media_operations` is the durable work ledger. One row represents one unit of
work that can wait, run, retry, fail, become stale, or require action. Examples
include file ingestion, Wikidata bridge resolution, text track lookup, commercial
skip detection, plugin work, writeback, and AI enrichment.

`media_operation_events` is the append-only timeline for operations. State
changes and important progress updates are recorded here so support can answer
what happened, when it happened, and which worker touched the item.

`entity_capability_states` is the media-item readiness view. It answers whether
an asset has lyrics, subtitles, cover art, commercial skip output, writeback,
Wikidata identity, plugin output, or AI output, and whether that capability is
pending, running, stale, blocked, failed, not applicable, or complete.

## Status Vocabulary

Operation statuses are:

`pending`, `queued`, `leased`, `running`, `retry_waiting`, `succeeded`,
`no_result`, `missing_confirmed`, `not_applicable`, `blocked`,
`failed_retryable`, `failed_terminal`, `dead_lettered`, `cancelled`,
`interrupted`, and `skipped`.

Capability statuses are:

`not_applicable`, `pending`, `queued`, `running`, `succeeded`, `no_result`,
`missing_confirmed`, `blocked`, `failed_retryable`, `failed_terminal`,
`skipped`, and `stale`.

## Queue Visibility

Files create an `ingestion.file` operation as soon as they are discovered. The
operation stage then moves through `discovered`, `settling`, `waiting_for_lock`,
`queued`, `hashing`, `parsing`, `scoring`, `registered`, `queued_identity`, and
`completed`.

The dashboard and batch item endpoints read this table for item-level status.
This means a process crash no longer erases the visible queue just because the
in-memory debounce or worker queue was lost.

## Wikidata Progress

The `Tuvima.Wikidata` package now emits progress events for bridge resolution.
Tuvima Library keeps ownership of product status and ETA, while Wikidata reports
the current phase, item counts, work-unit counts, elapsed time, and failure kind.

The `identity.wikidata_bridge` capability version is based on
`WikidataLibraryInfo.PackageVersion`.

## Review Queue Rule

Review Queue is for terminal or actionable states only. Pending, queued,
running, retrying, and stale work belongs in Operations and Capabilities, not
Review Queue.

Optional missing outputs such as lyrics or commercial markers normally end as
`no_result` and do not create review noise. Required or policy-actionable
terminal states can be routed through `ReviewQueueRouter`.

## Recovery

`MediaOperationRecoveryHostedService` runs at startup and reclaims expired
leased/running operations as `interrupted`. Workers can then requeue or retry
work without losing queue visibility.
