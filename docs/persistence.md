# Local Persistence: Schema and Migration Strategy

Support Satchel stores everything locally in a single SQLite database
(`Microsoft.Data.Sqlite`). There is no cloud sync and no account; the file
is the whole system of record.

## Files

- Default location: per-user application data directory
  (e.g. `%LOCALAPPDATA%\SupportSatchel\satchel.db` on Windows,
  `~/Library/Application Support/SupportSatchel/satchel.db` on macOS).
- WAL journaling is compatible; the store assumes single-writer,
  multi-reader access typical of a desktop app.

## Two independent version axes

1. **Database schema version** — the SQLite layout. Tracked in
   `PRAGMA user_version` and mirrored in the `schema_migration` table
   (`version`, `name`, `applied_at`). The build constant is
   `Schema.TargetVersion`.
2. **Profile document schema** (`documentSchema` inside the profile JSON)
   — the shape of the stored profile document. Readers accept documents
   with `documentSchema <= BundleProfile.CurrentDocumentSchema` and reject
   anything newer with a `ProfileSerializationException`. This makes
   downgrades explicit rather than silently lossy.

## Migration rules

- Migrations are **forward-only** and **ordered** by `ToVersion`; there is
  no down-migration path (restore from backup instead).
- Each migration runs inside a transaction together with its audit record.
- Opening a database whose `user_version` is **newer** than the build's
  `TargetVersion` fails fast with a clear error instead of guessing.
- Migrations must be additive and non-destructive in practice: new tables
  and columns with defaults, never dropping or rewriting existing data.
- A profile document written by an older build remains readable; document
  fields may gain defaults but never change meaning in place.

## Version history

| Version | Name                  | Change |
| ------- | --------------------- | ------ |
| 1       | `initial-schema`      | `profiles` (id, unique name, canonical JSON document, timestamps) and `runs` (run metadata referencing a profile, cascade delete). |
| 2       | `run-notes-and-index` | Adds `runs.notes` (default `''`) and index `idx_runs_profile_started` for run-history queries. |

## Adding a new migration (checklist)

1. Append a `Migration(ToVersion, "kebab-name", Up)` entry to
   `Schema.Migrations` with the next version number; bump
   `Schema.TargetVersion`.
2. Keep the DDL additive; new columns need defaults.
3. Add a test that opens a database fabricated at the **previous**
   version (see `SchemaMigrationTests`) and asserts the migration runs,
   existing rows survive, and new behaviour works.
4. If profile *document* semantics change, bump
   `BundleProfile.CurrentDocumentSchema` instead and keep old documents
   readable, or reject newer documents explicitly.

## Integrity guarantees

- Profile writes are validated with `ProfileValidator` before touching the
  database; invalid documents can never be persisted through the store.
- Profile names are unique (`UNIQUE` constraint) and enforced with a typed
  `ProfileNameConflictException`.
- Run rows cascade-delete with their profile, so deleting a profile can
  never orphan metadata.
- Timestamps are stored as round-trip `"O"`-format UTC strings; ordering
  is lexicographically stable.
