# Desktop workflow and accessibility (issue #6)

The Avalonia desktop app is a local-only front end over the production
`ProfileStore`, `Collector`, `RedactionEngine`, and `BundleExporter`. It does
not require an account, make a network request, upload a bundle, or modify an
original source file.

## Start the app

From the repository root with the pinned .NET 8 SDK:

```bash
~/.dotnet/dotnet run --project src/SupportSatchel.App/SupportSatchel.App.csproj
```

Profiles and run workspaces are stored under the operating system's local
application-data `SupportSatchel` directory. Exports go only to the directory
entered by the operator.

## Profile operation

1. Choose **New profile** (`Ctrl+N`) or select a saved profile in the left list.
2. Enter a unique profile name and optional description.
3. Edit **Advanced configuration JSON**. This is the complete functional
   source/redaction/packaging editor; it is not metadata or a placeholder.
4. Choose **Save profile** (`Ctrl+S`). The same Core validator used by storage
   validates source ids/paths/globs, regex rules, and bundle naming before the
   SQLite write. Invalid JSON or configuration remains unsaved with a concise
   error.
5. **Duplicate** (`Ctrl+D`) creates a new identity retaining the full saved
   configuration. Names are generated as `<name> copy`, `<name> copy 2`, and so
   on, choosing the first locally available name.
6. To delete, select a profile, type its exact current name into the deletion
   confirmation field, then choose **Delete**. A stale or mismatched value is
   refused. Deletion also removes that profile's run metadata through the
   existing database cascade; staged workspace directories are not silently
   deleted.

Unsaved editor values are protected. While a saved profile has edits, profile
selection, **New profile**, **Duplicate**, and **Delete** are disabled; use
**Save profile** to persist them or **Revert edits** to discard them explicitly.
A new, unsaved draft uses the same gate. For that draft, **Revert edits** stays
available and explicitly clears its name and description and restores the
default configuration, so the operator can discard it and select a saved
profile without being trapped.

The advanced document has exactly three top-level fields:

```json
{
  "sources": [
    {
      "id": "application-logs",
      "kind": "folder",
      "path": "/absolute/path/to/logs",
      "includePatterns": ["**/*.log"],
      "excludePatterns": ["**/cache/**"],
      "required": true
    }
  ],
  "redaction": {
    "rules": [
      {
        "id": "email",
        "name": "Email addresses",
        "pattern": "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}",
        "replacement": "[REDACTED]",
        "enabled": true
      }
    ]
  },
  "export": {
    "bundleNameTemplate": "{profile}-{utc}.zip",
    "deterministic": true,
    "includeManifest": true
  }
}
```

`kind` is `file` or `folder`. File sources cannot have include/exclude globs.
Folder globs use `/` separators. Rule order is significant and regex backslashes
must be JSON-escaped. Bundle names require `{profile}` and `{utc}`; `{version}`
is optional. Selecting another clean profile, saving/duplicating/deleting a
clean profile, or starting a capture clears all prior run, acknowledgment, and
export state. Editing the profile name, description, or advanced JSON also
clears that state immediately. Capture and profile-changing actions are
disabled until the draft is either saved or explicitly discarded with
**Revert edits**; visible unsaved values are never silently replaced and
capture never silently uses an older persisted configuration.

## Guided capture, review, and export

1. Select a saved profile and choose **Capture selected profile** (`Ctrl+R`).
   Capture and preview run off the UI thread in a unique local workspace. A
   required-source or preview failure persists a failed run and leaves review
   and export unavailable. Raw IO exception details, source paths, and file
   contents are withheld from UI errors.
2. In **Review redactions and contents**, visit every artifact. The left list
   shows its text/binary/missing classification and match count. The right side
   intentionally shows original staged text beside the sanitized result for
   local human review; redaction match snippets are not displayed. Binary files
   cannot be text-redacted and require separate inspection or exclusion.
3. Clear **Include artifact in export** for anything that should not ship.
   Every inclusion change clears the acknowledgment. Excluded staged copies are
   neither redacted nor packaged.
4. Check the explicit review acknowledgment. This is an intent record, not a
   security boundary or tamper proofing.
5. Enter an output directory and choose **Export reviewed bundle** (`Ctrl+E`).
   Export rechecks that the persisted profile is exactly the captured revision,
   rejects symbolic links/reparse points in reviewed input paths, applies
   redactions only to included copies, and invokes the Core exporter's
   successful-report gate. The resulting ZIP, manifest sidecar, and checksum
   remain local until the operator shares them manually.

An operation gate rejects duplicate clicks while work is running, and a run
token rejects concurrent or repeated export. Profile selection, profile editor,
artifact selection/inclusion, and review acknowledgment controls are disabled
for the full operation. The included artifact paths are copied synchronously
before export moves to a background thread, binding the package to the
acknowledged set.

Every export attempt consumes its run once the input and acknowledgment gates
pass. Redaction mutates staged copies before packaging, and configured rules
need not be idempotent, so neither a successful nor a failed attempt can be
retried from that workspace. The UI removes the consumed review and requires a
fresh capture. Original source files remain untouched.

## Keyboard and screen-reader guidance

- `Tab` and `Shift+Tab` move through profile selection, CRUD controls, editor
  fields, workflow tabs, artifact inclusion checkboxes, acknowledgment, output,
  and export in visual order. Arrow keys navigate lists and tabs; `Space`
  toggles the focused checkbox; `Enter` activates the focused button.
- `Ctrl+N` creates a draft, `Ctrl+S` saves, `Ctrl+D` duplicates, `Ctrl+R`
  captures, and `Ctrl+E` exports when its privacy gates are satisfied.
- Every interactive control has an explicit automation name. Profile and
  artifact lists expose their item text; original and sanitized review panes
  have distinct names. Status uses a polite live region and errors an assertive
  live region. Disabled commands communicate unavailable lifecycle steps.
- The advanced JSON field deliberately does not capture `Tab`, so keyboard-only
  users can leave it. Insert spaces rather than a literal tab for indentation.

## Test-driven implementation record

Each vertical slice began with an expected failure, followed by the smallest
implementation and a green rerun (all commands used `~/.dotnet/dotnet`):

| Slice | Red command/result | Green command/result |
|---|---|---|
| Persisted CRUD | `dotnet test tests/SupportSatchel.App.Tests/... --no-restore` — compile failed because `SupportSatchel.App.ViewModels` did not exist. The first implementation run then correctly exposed an invalid test bundle template through Core validation. | Same command — 1/1 passed after implementing the workflow/codec and using the production template contract. |
| Capture/review/export | Same command — compile failed because `CaptureAsync` and `ExportAsync` did not exist. | Same command — 2/2 passed after using real temporary source files and the production collector/redactor/exporter. |
| Failure and state invalidation | Same command — compile failed because `MainWindowViewModel` did not exist. | Same command — 5/5 passed after the state controller, sanitized failures, acknowledgment invalidation, and typed delete guard. |
| Reentrancy/staleness/exclusion | Same command — compile failed because the injectable `IDesktopWorkflow` boundary did not exist. | Same command — 8/8 passed after the operation gate and remaining export guards. |
| Reviewed-path link substitution | `dotnet test tests/SupportSatchel.App.Tests/... --no-restore --filter ExportRefusesStagedSymbolicLinkWithoutTouchingExternalTarget` — failed because export followed the substituted test-only link. | Same command — 1/1 passed after applying the CLI-equivalent link/reparse-point guard before redaction. The external test file remained unchanged. |
| Review/export lifecycle fixes | Focused Release test compilation failed because dirty-draft/revert state did not exist. | The same focused filter passed 4/4 after synchronous inclusion snapshotting, permanent export-token consumption, dirty-draft capture gating/revert, and unique duplicate naming. |
| Headless Avalonia bindings | The two focused headless tests failed: busy-state control lookup found no named/bound controls, and artifact inclusion controls exposed no usable per-path result. | The same focused filter passed 2/2 after binding busy interaction state and staged paths into distinct automation names. |
| Unsaved profile navigation guard | Four focused controller/headless tests failed because saved edits and new-draft values were replaced by profile actions, and CRUD controls had no dirty-state enabled bindings. | The same focused filter passed 4/4 after gating profile selection/New/Duplicate/Delete, tracking new drafts as dirty, binding actual enabled states, and making Revert reset new-draft defaults. |
| Avalonia view | `dotnet build src/SupportSatchel.App/SupportSatchel.App.csproj --no-restore` — XAML compile failed on unqualified scrollbar attached properties. | Same command — succeeded with 0 warnings and 0 errors after correcting the Avalonia property syntax. |

## Linux X11 desktop-lifetime smoke evidence (not platform acceptance)

Recorded 2026-09-17 on the Linux implementation host. This is the strongest
evidence obtainable here and it is explicitly **not** a substitute for the
Windows/macOS checklists below.

- Environment: Xvfb `:99`, `1280x800x24`, Release build (`net8.0`), .NET SDK
  8.0.424, isolated `XDG_DATA_HOME`/`XDG_CONFIG_HOME` under `/tmp`.
- Result: the real `SupportSatchel.App` executable started under the classic
  desktop lifetime, opened an X11 display connection, stayed running until the
  45-second `timeout` expired (exit code 124), and its log's only X-related line
  is `X connection to :99 broken (explicit kill or server shutdown)` from the
  server teardown. No XAML load, resource, or runtime exception output.
- What this proves: the full (non-headless) Avalonia desktop path loads
  `App.axaml`/`MainWindow.axaml`, instantiates `MainWindow`, and reaches a
  stable running state on a real windowing platform.
- What this does not prove: Windows Narrator/UIA, macOS VoiceOver, DPI scaling,
  packaged-app launch, native file/privacy behavior, or any checklist item
  below.

## Platform manual acceptance checklist

This implementation host is Linux. Linux compilation, headless tests, and the
X11 smoke above do **not** substitute for target-platform desktop accessibility
testing.

### Windows 10/11 — NOT RUN

- [ ] NOT RUN — launch packaged/app build as a standard user; restart and verify profile persistence.
- [ ] NOT RUN — complete create/edit/duplicate/typed-delete and a real capture/review/exclude/export workflow.
- [ ] NOT RUN — keyboard-only pass at default, 125%, and 200% display scaling; verify focus visibility and no focus trap.
- [ ] NOT RUN — Narrator pass for control names, list items, live status/error announcements, disabled export, and original-versus-sanitized panes.
- [ ] NOT RUN — verify a denied/unreadable Windows path fails without displaying that path or source contents.
- [ ] NOT RUN — inspect ZIP/report/manifest and confirm excluded/original secret data and provenance absolute paths are absent.

### macOS — NOT RUN

- [ ] NOT RUN — launch app bundle as a standard user; restart and verify profile persistence under Application Support.
- [ ] NOT RUN — complete create/edit/duplicate/typed-delete and a real capture/review/exclude/export workflow.
- [ ] NOT RUN — keyboard-only pass with Full Keyboard Access enabled; verify tab order, shortcuts, focus visibility, and no focus trap.
- [ ] NOT RUN — VoiceOver pass for control names, list items, live status/error announcements, disabled export, and review panes.
- [ ] NOT RUN — verify a denied privacy-protected path fails without displaying that path or source contents.
- [ ] NOT RUN — inspect ZIP/report/manifest and confirm excluded/original secret data and provenance absolute paths are absent.

**Acceptance blocker:** both target-platform checklists remain NOT RUN. Issue #6
must not be considered fully accepted or closed until real Windows and macOS
manual UI/accessibility validation is completed and recorded.
