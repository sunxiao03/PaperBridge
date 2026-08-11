# Backup and recovery

## Automatic SQLite snapshots

After database migration succeeds, PaperBridge creates at most one consistent SQLite online snapshot per UTC day under `%LOCALAPPDATA%\PaperBridge\Backups` and retains the newest five. Each snapshot is written to a temporary file, checked with `PRAGMA integrity_check`, and then atomically named. These snapshots include library metadata, folders/tags, reading positions, glossary data, translation/bilingual caches, annotations/bookmarks, and AI cache rows.

Automatic snapshots do not duplicate managed PDF files or export API keys.

## Complete local-data backup

Close PaperBridge and run the supplied script:

```powershell
.\Backup-PaperBridgeData.ps1 -OutputPath D:\Backups\PaperBridge-data.zip
```

It copies `Data`, `Library`, and `Settings`, records every file size and SHA-256, and creates a ZIP. Logs, old backups, and Windows Credential Manager secrets are excluded.

Restore only while PaperBridge is closed:

```powershell
.\Restore-PaperBridgeData.ps1 -BackupPath D:\Backups\PaperBridge-data.zip
```

All manifest paths and SHA-256 values are checked before replacement. Existing data is moved to a timestamped sibling such as `PaperBridge.rollback-20260811-120000`, allowing manual rollback. Delete that directory only after verifying the restored library.

## Database-only restore for maintainers

`SqliteDatabaseBackupService` verifies a selected snapshot before replacement and creates a `paperbridge-before-restore-*.db` rollback snapshot first. It must be called only when all application database users are closed. A corrupt snapshot is rejected without changing the current database.
