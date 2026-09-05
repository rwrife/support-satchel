# Support Satchel Plan

## Scope

Build a local-first desktop utility for Windows/macOS that creates sanitized troubleshooting bundles from reusable profiles.

### In scope (MVP)

- Profile-driven collection of logs/config/probe outputs
- Staging area and preview before export
- Configurable redaction rules with safe defaults
- ZIP export with manifest/checksum evidence
- Basic accessibility for keyboard and screen-reader workflows

### Out of scope (MVP)

- Cloud synchronization or ticketing integrations
- Mobile client
- Fully automated root/admin-only diagnostics
- Enterprise policy orchestration

## Architecture

- **SupportSatchel.Core**
  - Domain model (`BundleProfile`, `CaptureSource`, `BundleArtifact`, `RedactionRule`)
  - Orchestration service for capture → redact → package
- **SupportSatchel.Collectors**
  - File/folder collectors
  - Built-in read-only probe collectors
- **SupportSatchel.Redaction**
  - Pattern detectors + replacement strategies
  - Redaction preview diff model
- **SupportSatchel.Packaging**
  - ZIP builder + manifest/checksum generation
- **SupportSatchel.App** (Avalonia)
  - Profile editor, run history, redaction review, export flow
- **SupportSatchel.Cli**
  - Headless profile run for automation and reproducibility

## Technology choices and rationale

- **.NET 8**: stable LTS runtime and strong file/process APIs.
- **Avalonia UI**: one UI stack for Windows + macOS.
- **SQLite**: local metadata store with reliable transactions.
- **System.IO.Compression + SHA256**: deterministic packaging and integrity metadata.
- **xUnit + snapshot fixtures**: reliable automated tests for redaction and manifest output.

## Milestones and dependency order

1. **Repository skeleton + CI**
   - Solution/projects, formatting, lint/test pipeline.
2. **Domain + persistence**
   - Profile schema, migration strategy, local DB.
3. **Collectors + staging**
   - File/folder collectors and initial probes.
4. **Redaction engine + review model**
   - Rule sets, masking behavior, preview data model.
5. **Packaging + manifest/checksum**
   - Deterministic ZIP generation.
6. **Desktop UI workflows**
   - Profile authoring, run execution, review, export.
7. **CLI + release packaging**
   - Headless operation, installers/zips, release docs.

## Testing strategy

- Unit tests for profile validation, collector edge cases, redaction logic, packaging integrity.
- Fixture-based integration tests using sanitized sample logs.
- Cross-platform smoke tests (Windows + macOS runners) for end-to-end profile run.
- Regression tests for redaction false positives/negatives and path handling.

## Packaging and distribution plan

- GitHub Actions matrix builds for Windows/macOS.
- Unsigned preview artifacts during early development.
- Milestone to add code-signing/notarization documentation and release checklist.
- Publish ZIP/app bundles and checksums in GitHub Releases.

## Risks

- Over-redaction can remove needed debugging context.
- Under-redaction can leak secrets/PII.
- macOS permission model can block selected sources unexpectedly.
- Collector stability across app-specific log layouts.

## Explicit non-goals

- Realtime remote support sessions
- Hidden telemetry
- Any automatic sharing outside user intent
- Claims of forensic-grade completeness in MVP
