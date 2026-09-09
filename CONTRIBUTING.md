# Contributing to Support Satchel

## Required SDK

- **.NET 8 SDK** — the exact build SDK is pinned in [`global.json`](./global.json)
  (currently `8.0.424` with `rollForward: latestPatch`). Any machine with a newer
  8.0.4xx SDK or the .NET 10 SDK installed will resolve the pinned 8.0.4xx band
  automatically if present, otherwise `dotnet` downloads it via `setup-dotnet` in CI.
- OS targets: Windows 10/11 and macOS. Linux works for `Core`/`Cli` development and CI-adjacent
  checks, but the Avalonia desktop app is only validated on Windows/macOS.

## Local setup

```bash
# From the repository root
dotnet restore SupportSatchel.sln --locked-mode
dotnet build SupportSatchel.sln
dotnet test SupportSatchel.sln
```

## Repository conventions

- **Solution layout**: `src/SupportSatchel.Core`, `src/SupportSatchel.App`
  (Avalonia desktop UI), `src/SupportSatchel.Cli`, and `tests/` (xUnit).
- **Build policy**: `Directory.Build.props` sets `net8.0`, nullable reference
  types, and `TreatWarningsAsErrors` — warnings break the build, so fix them.
- **Dependency policy**: package versions are centrally managed in
  `Directory.Packages.props`; lock files (`packages.lock.json`) are committed
  and CI restores with `--locked-mode`. After changing any package reference,
  refresh locks locally with:

  ```bash
  dotnet restore SupportSatchel.sln --force-evaluate
  ```

  and commit the updated lock files.
- **Formatting**: `dotnet format SupportSatchel.sln --verify-no-changes`
  runs in CI; run it locally before pushing.
- **Line endings**: `.gitattributes` normalizes all text files to LF.

## CI

[`.github/workflows/ci.yml`](./.github/workflows/ci.yml) runs restore (locked),
format check, Release build, tests, and a transitive vulnerability scan on
`windows-latest` and `macos-latest` for every PR and push to `main`.
