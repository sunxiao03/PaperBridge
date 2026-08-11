# Release checklist — 0.1.0

## Automated gates

- [x] Release tests pass: 93/93.
- [x] Debug build produces zero warnings and errors.
- [x] `dotnet format --verify-no-changes` passes.
- [x] SQLite migration to schema 7 and backup/restore fault tests pass.
- [x] Accelerated multi-tab duration/resource probe passes on the generated 500-page fixture.
- [x] Fresh install, atomic upgrade, full-data backup/restore, preserving uninstall, and destructive isolated uninstall pass.
- [x] Release ZIP, SHA-256 list, file manifest, SPDX SBOM, licenses, and user documentation are present.
- [x] Worktree, Git history, tracked files, SQLite/log files, and final archive are scanned for likely secrets and restricted PDFs.
- [x] Self-contained executable starts with isolated data and creates schema 7.

## Human gates before GitHub publication

- [ ] Review privacy wording and known limitations.
- [ ] Verify the published archive SHA-256 on a second machine or clean Windows profile.
- [ ] Perform an eight-hour interactive reading/translation endurance session if treating 0.1.0 as more than a preview.
- [ ] Configure an intended GitHub remote; review the first local commit and complete repository visibility/settings.
- [ ] Create the GitHub Release manually only after acceptance. No tooling in this repository guesses or creates a remote.
