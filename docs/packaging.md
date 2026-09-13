# Deterministic bundle packaging (issue #5)

`SupportSatchel.Core.Packaging` turns a redacted run workspace into a
portable ZIP bundle plus manifest/checksum evidence. It is the final stage
of the pipeline:

```
collect (#3) → redact (#4) → package (#5) → share
```

## What a bundle contains

| Bundle entry | Content |
|---|---|
| `manifest.json` | Artifact list with sizes and SHA-256 hashes, profile ID/name/version, export timestamp (UTC). |
| `redaction-report.json` | The redaction report copied from the workspace root, so recipients can audit what was masked. |
| `staging/<source-id>/...` | Every staged (redacted) artifact, at its collector-relative path. |

Provenance sidecars are **deliberately excluded**: they record absolute
source paths, and a bundle must not leak the collecting machine's layout
(same rule the redaction report follows, see `docs/redaction.md`).

## Fail-closed gate

Export refuses to run (`BundleExportException`) unless
`{workspace}/redaction-report.json`:

1. exists,
2. parses as a report document, and
3. reports `isSuccessful: true`.

An absent or unsuccessful report means at least one staged copy may hold
un-redacted content, and the exporter prefers refusing over shipping
secrets. This implements the downstream contract from `docs/redaction.md`.

## Determinism contract (tested)

- **Entry order.** ZIP entries are written in ordinal path order
  (`manifest.json` → `redaction-report.json` → `staging/...`), independent
  of file-system enumeration order.
- **Fixed timestamps.** With `ExportOptions.Deterministic` (the default),
  every entry is stamped with the DOS epoch (1980-01-01), so archive bytes
  depend only on content. Re-exporting identical staged input produces a
  byte-identical ZIP and a byte-identical manifest.
- **Injectable clock.** The manifest timestamp and the `{utc}` bundle-name
  token come from `PackagingOptions.Clock`, never wall clock directly.
- **Checksums.** `manifest.json` records size and lowercase-hex SHA-256 of
  each entry's uncompressed bytes; `ExportResult.BundleSha256` covers the
  ZIP file itself. All are verified by tests after extraction.

## Bundle naming

`ExportOptions.BundleNameTemplate` supports `{profile}` (name sanitized to
a single safe file-name component — unsafe characters and whitespace
collapse to `-`), `{version}` (profile document schema), and `{utc}`
(`yyyyMMdd'T'HHmmss'Z'` from the clock). The sanitization set is a fixed
list, not `Path.GetInvalidFileNameChars()`, so rendered names are
byte-identical on Windows and macOS.

## Verifying a received bundle

1. Check the ZIP against `BundleSha256` (or the manifest's sibling file
   `<bundle>.zip.manifest.json`).
2. Extract and confirm `manifest.json` inside the archive is byte-identical
   to the sidecar.
3. Recompute SHA-256 per entry against the manifest's `artifacts` list.
4. Read `redaction-report.json` to see which rules ran and what was masked.
