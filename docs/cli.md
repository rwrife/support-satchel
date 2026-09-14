# CLI workflow contract (issue #7)

The CLI is a local-only front end over the existing profile store,
collector, redactor, and exporter. It makes no network calls and does not
upload or share bundles.

## Commands

```text
support-satchel [--json] profiles list [--store PATH]
support-satchel [--json] run --profile ID_OR_NAME [--store PATH] [--workspace-root PATH]
support-satchel [--json] export --run ID [--store PATH] --output DIRECTORY --reviewed
```

`--json` may appear anywhere. On success, JSON mode writes one JSON object
to stdout and nothing to stderr. On failure it writes a stable `error` code
and non-sensitive `message` to stdout, then returns non-zero. Detailed IO,
SQLite, collector, and redactor exception messages are deliberately not
emitted because they may contain selected paths or secret material.

When paths are omitted, the store and run workspaces live below the user's
local application-data `SupportSatchel` directory. `--output` and the
`--reviewed` acknowledgment are required: export is always an explicit user
action after inspecting the staged artifacts and report. The acknowledgment
records intent only; it is not tamper resistance or cryptographic approval.

## Lifecycle and the pending desktop UI

The lifecycle shared with the planned desktop UI is:

1. **Select** a saved profile by exact name or GUID.
2. **Capture** sources into a unique local run workspace; originals are
   only read and never modified.
3. **Redact and review** staged copies. `run` applies the profile rules,
   writes `redaction-report.json`, persists the run, and returns
   `status: "readyForReview"` plus `reviewRequired: true`. Operators review
   the staged copies and report before proceeding.
4. **Export** that run explicitly with `export --run ... --reviewed`. Export
   refuses failed runs, symlinks/reparse points in the workspace input paths,
   and runs without the Core exporter's successful-report gate.

The report's artifact inventory drives export. Provenance sidecars and any
file not represented by the reviewed report are excluded. A failed
collection is not redacted; a failed redaction is not exportable.

The workspace remains ordinary user-controlled storage. Reparse-point checks
reduce accidental path substitution, but a same-user process can still race
file reads or change content concurrently. Do not treat the workspace,
`--reviewed`, or the generated checksum as protection from a malicious process
running as the same user.

Issue #6's desktop UI remains pending. This command contract is intended
to align the future UI state machine, but no desktop selection, review, or
export interaction is implemented or claimed as tested here.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success |
| 2 | Invalid command or option |
| 3 | Profile or run not found |
| 4 | Capture/redaction lifecycle blocked |
| 5 | Sanitized storage, IO, or export failure |
