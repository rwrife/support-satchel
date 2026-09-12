# Redaction engine (issue #4)

`SupportSatchel.Core.Redacting` masks secrets and PII in the **staged
copies** produced by the collector (issue #3), before packaging (issue #5)
builds an exportable bundle. Original source files are never in reach of
the engine; it only ever rewrites files under
`{workspace}/staging/`, and provenance sidecars under
`{workspace}/provenance/` are never modified.

## Pipeline position

```
collect (issue #3) → preview → user review (issue #6) → apply → package (issue #5)
```

- `RedactionEngine.PreviewWorkspace` is **read-only** and yields, per
  artifact, the original staged text next to the exact text an export
  would contain, plus every rule hit (rule id, index, length, snippet).
  This is the data model the review UI consumes.
- `RedactionEngine.ApplyWorkspace` performs the masking in place and
  writes `redaction-report.json` at the workspace root. Packaging must
  check the report (`IsSuccessful`) before zipping.

## Default rules

`RedactionSettings.CreateDefault()` (issue #2) ships four conservative
rules: e-mail addresses, bearer tokens, `api_key|secret|token|password`
key/value pairs, and `hostname|user_name|computer_name` hints. Custom
rules support capture-group replacements (`${name}`) and can be disabled
without deleting them.

## Engine semantics

- **Sequential application.** Rules run strictly in list order; each rule
  sees the output of its predecessors. Match positions in previews refer
  to the text *as that rule saw it*. The engine never rescans after a
  replacement, so rule order is part of the configuration.
- **Text only.** An artifact is redacted iff it decodes as strict UTF-8
  (optional BOM, which is preserved). Anything else is classified
  `Binary` and copied unchanged.
- **Fail closed.** A rule with an invalid pattern, or one that exceeds
  `RedactionOptions.PerRuleTimeout`, aborts that artifact: the staged
  copy keeps its un-redacted content and the run records an error.
  An unsuccessful run is an export blocker — the engine prefers leaking
  into a *reviewable* workspace over silently half-redacting text.
- **Determinism.** Report timestamps come from an injectable clock and
  artifact lists are ordinal-sorted, so identical workspaces produce
  byte-identical reports. Reports never contain absolute workspace or
  source paths, so they can ship inside the bundle without leaking
  machine layout.

## Documented limits (tested)

These are deliberate trade-offs, each pinned by a unit test:

| Behaviour | Why |
|---|---|
| `user@localhost` (no dotted TLD) is **not** redacted | Requiring a dotted TLD keeps unix socket names, `FROM alpine:latest`-style strings, and cron `MAILTO=""` noise out of the false-positive zone. |
| `token: abc123` (value < 12 chars) is **not** redacted | Short values look like ordinary config words (`token: none`); the 12-char floor is the false-positive/coverage balance point. |
| Unicode/UTF-16 logs are treated as **binary** | Strict UTF-8 decoding is the text/binary boundary; a UTF-16 log will be exported raw and is the reviewer's responsibility to exclude. |
| Match **positions** shift as rules chain | Previews show positions per rule-application, not in the original text; UIs should render per-rule segments, not a single global diff. |
| Over-redaction can remove debug context | Rules are user-editable and previewable precisely so a human reviews losses before export. |

## Downstream contract (issue #5 / #6)

- Read `redaction-report.json` before packaging; refuse to export when
  `isSuccessful` is false or the report is missing.
- Artifacts listed with `kind: "binary"` or `kind: "missing"` must be
  surfaced in the review UI as "exported un-redacted" / "absent".
- `ApplyWorkspace` is idempotent: running it again over redacted text is
  a no-op match-wise.
