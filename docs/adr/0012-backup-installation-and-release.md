# ADR 0012: Backup, installation, and release strategy

## Status

Accepted for 0.1.0.

## Decision

PaperBridge uses SQLite's online backup API for daily database snapshots rather than copying an active WAL database. Snapshots are integrity-checked before publication and restore; restore verifies first and creates a rollback snapshot.

Complete user-controlled backups are closed-app ZIP archives of `Data`, `Library`, and `Settings` with per-file SHA-256. Credential Manager secrets are excluded by design.

The first release is an unsigned, self-contained Windows x64 ZIP with reviewable PowerShell install/uninstall scripts. Program-only uninstall preserves user data and credentials. Destructive data or credential removal requires separate explicit switches.

Release construction is deterministic in structure and produces a versioned archive, checksums, file manifest, SPDX SBOM, licenses, privacy/security/support documents, and known limitations. Version 0.1.0 is frozen in shared build properties. GitHub publication is a later human-authorized action and is not inferred from a local repository.

## Consequences

The approach avoids a new installer toolchain and signing claims but causes SmartScreen/script-policy friction. Full-data backup can be large because managed PDFs are included. Restore and complete backup require a closed application. The output is easy to inspect and can later be wrapped in MSIX after a signing and upgrade-identity decision.
