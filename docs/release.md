# Unsigned preview release checklist (issue #7)

CI exercises packaging on every pull request and `main` push on
`windows-latest` and `macos-latest`. Only version-tag builds matching the
assembly metadata exactly as `v<Version>` upload the ZIP and SHA-256 files
as GitHub Actions artifacts. The script
publishes framework-dependent .NET 8 desktop and CLI files and runs the
published CLI's `--version` command before creating the archive.

These are **unsigned engineering previews**, not installers and not
production-ready releases. CI does not create tags or GitHub Releases.
Users need a compatible .NET 8 runtime, and Windows SmartScreen or macOS
Gatekeeper may warn or block execution.

The shared version is declared in `Directory.Build.props`; the CLI reads its
informational version from the built assembly. Tagged CI verifies the tag
against that metadata before packaging.

## Current signing and notarization gaps

- No Windows Authenticode certificate is configured; executables and DLLs
  are unsigned, there is no trusted timestamp, and SmartScreen reputation
  has not been established.
- No Apple Developer ID Application identity is configured. The app is not
  signed with hardened runtime, submitted to Apple's notary service,
  stapled, or assessed with Gatekeeper.
- CI has no protected signing secrets, isolated signing job, installer
  format, provenance attestation, or signed update channel.
- The preview archives themselves are not cryptographically signed. The
  `.sha256` file detects accidental corruption but is not proof of origin.

## Required checklist before a production release

- [ ] Choose supported CPU architectures and produce RID-specific,
      lockfile-controlled publishes for each one.
- [ ] Acquire and protect a Windows code-signing certificate; sign all
      shipped PE binaries and timestamp signatures with a trusted service.
- [ ] Verify Authenticode signatures on a clean Windows runner and test
      SmartScreen/install and uninstall behavior as a standard user.
- [ ] Configure an Apple Developer ID Application certificate in a
      protected signing job and review entitlements.
- [ ] Sign nested macOS code first, then the app with hardened runtime;
      verify with `codesign --verify --deep --strict`.
- [ ] Submit the exact macOS artifact for notarization, wait for success,
      staple the ticket, and validate with `spctl` on a clean Mac.
- [ ] Select and test production installer/container formats, including
      upgrade, uninstall, permissions, accessibility, and rollback paths.
- [ ] Generate an SBOM and signed build provenance; sign archives and
      checksums with a separately protected release identity.
- [ ] Run the canonical restore/format/build/test suite and sanitized CLI
      process smoke on all supported release platforms.
- [ ] Manually inspect the redaction/export fixture bundle to confirm it
      contains no provenance or unsanitized fixture values.
- [ ] Publish from a protected, approval-gated environment and document
      certificate rotation, revocation, and incident procedures.
