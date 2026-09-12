# Support Satchel

**Pitch:** Local-first desktop utility for Windows and macOS that builds sanitized support bundles from reusable checklists so users can share troubleshooting evidence without exposing private data.

## Overview

Support Satchel helps people collect exactly the diagnostics they intend to share (logs, config snapshots, app/version details, and selected command output), preview sensitive data exposure, and export a portable ZIP bundle with a manifest.

## Motivation

Troubleshooting usually means scrambling across folders, grabbing the wrong files, and leaking more private data than needed. Support Satchel makes this repeatable, reviewable, and privacy-first.

## Target users

- Individual users sending support evidence to app vendors
- Developers/support engineers reproducing customer environments locally
- IT/helpdesk staff preparing redacted artifacts for escalation

## Concrete use cases

1. **"My app keeps crashing" bundle**: Collect crash logs + app config + OS version in one click.
2. **Driver/setup issue bundle**: Gather USB/device enumeration snapshots and installer logs.
3. **Repeatable team workflow**: Save a bundle profile so every report uses the same evidence shape.

## Intended end-to-end workflow

1. Create a **Bundle Profile** (name + included sources).
2. Select data sources:
   - specific files/folders
   - built-in read-only probes (OS/app metadata, process list, disk/memory summary)
   - optional freeform notes
3. Run capture to a local staging area.
4. Review privacy scan results (sensitive-pattern detections) and exclude/re-include items.
5. Export ZIP + JSON manifest + checksums.
6. Share the bundle manually via email/ticket/chat.

## MVP feature list

- Cross-platform desktop app (Windows 10/11, macOS)
- Saved bundle profiles (local only)
- Source collectors: files/folders + built-in read-only probes
- Redaction pipeline with preview (copy of data, never mutate originals)
- Export as ZIP with manifest/checksums
- Session history and re-run from previous profile

## Non-goals (MVP)

- Cloud upload, hosted storage, or accounts
- Remote device collection/agent deployment
- Automated ticket submission to external services
- Kernel-level diagnostics or privileged system modification
- Linux support in MVP

## Privacy, permissions, and storage behavior

- **Local-first**: no required network calls for core functionality.
- **Data ownership**: bundles and profile definitions remain on the user’s machine unless explicitly exported/shared.
- **Permissions**:
  - Windows: standard user by default; elevated mode only when user explicitly requests protected paths.
  - macOS: may request user-granted file access (and Full Disk Access only when required by selected sources).
- **Redaction defaults**: mask likely secrets/PII patterns in exported copies; preserve original files untouched.
- **Storage**: local SQLite for profile/session metadata + local filesystem bundle artifacts.

## Current status

Issue #4 complete: redaction engine (`SupportSatchel.Core.Redacting`)
applies profile rules to staged collector copies only, exposes
per-artifact original-vs-redacted previews for the review step, and emits
a deterministic `redaction-report.json`; documented pattern limits live
in [`docs/redaction.md`](./docs/redaction.md). Earlier: issue #2 profile
domain model (`BundleProfile`, `CaptureSource`, `RedactionRule`,
`ExportOptions`, `RunRecord`) with validation, canonical JSON
serialization, and SQLite persistence + migration strategy
([`docs/persistence.md`](./docs/persistence.md)); issue #3 collector
pipeline with staging + provenance. Packaging, UI, and CLI proceed via
the issue backlog.

## Milestones

1. Core domain + profile model
2. Collector pipeline + staging
3. Redaction/preview/export
4. Desktop UI and accessibility pass
5. Packaging and release artifacts

## Development quickstart (planned)

```bash
# planned stack
- .NET 8 SDK
- Avalonia UI

# once skeleton exists
dotnet restore
dotnet build
dotnet test
```

## Repository notes

- Roadmap and execution plan: [`PLAN.md`](./PLAN.md)
- Persistence schema + migration strategy: [`docs/persistence.md`](./docs/persistence.md)
- Redaction engine semantics + documented limits: [`docs/redaction.md`](./docs/redaction.md)
- Core domain and local persistence are implemented; UI/CLI workflows are backlog items.
