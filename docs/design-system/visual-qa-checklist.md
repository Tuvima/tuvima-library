---
title: "Visual QA Checklist"
description: "Checklist for verifying Tuvima Library visual quality across Dashboard and reader surfaces."
audience: "designer"
category: "reference"
product_area: "design"
status: current
---

# Tuvima Library Visual QA Checklist

## In this page

Verify shared styling and responsive behavior on representative Dashboard and reader surfaces. Use these checks alongside functional tests; keep review captures in ignored temporary storage.

## Where this lives in the code

- `src/MediaEngine.Web/Components`
- `src/MediaEngine.Web/wwwroot/tuvima.tokens.css`
- `tests/MediaEngine.Web.Tests/UiConsistencyGuardrailTests.cs`

Use this checklist after design-token or shared styling changes. Verify desktop, tablet, and mobile widths where the screen supports responsive layouts.

## Global Checks

- Root background uses the deep cinematic dark app gradient.
- Shell, app bar, and navigation use the shared shell background.
- Cards use shared surface, border, radius, and raised/selected/warning states.
- Primary actions are violet.
- Informational and processing states are cyan.
- Review, provisional, and warning states are amber.
- Error and destructive states are red.
- Healthy, registered, verified, and matched states are green.
- Metadata and helper text use muted text tokens.
- Tables and lists use subtle dividers, standard hover, and standard selected states.
- Inputs use raised/surface backgrounds, default borders, violet focus border, and focus glow.
- Tabs use violet active state, transparent inactive state, and surface hover.
- Dialogs, menus, popovers, and drawers use elevated surfaces.

## Required Screens

- Home and For Me
- Read, Watch, and Listen browse lists and grids
- Operations at `/settings/ingestion`
- Review queue/manual verification
- Provider/API configuration
- Settings overview
- Playback settings
- Admin overview
- Detail page for a media item
- Dialogs and modals
- Mobile/responsive layout

## Regression Watchpoints

- External provider logos and media artwork retain their original colors.
- Charts retain intentional semantic/chart palettes unless explicitly updated.
- Warning amber appears only for attention, review, provisional, degraded, or conflict states.
- No page-specific blue, yellow, or gold accents override the shared token system.
- Long tab labels, buttons, and status pills do not overflow their containers.

