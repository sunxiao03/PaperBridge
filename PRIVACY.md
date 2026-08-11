# Privacy

PaperBridge 0.1.0 is a local-first Windows application. It has no PaperBridge server, account system, analytics, advertising, crash telemetry, or automatic update check.

## Data stored locally

By default, PaperBridge stores its SQLite database, managed PDF copies, settings, logs, and automatic database backups under `%LOCALAPPDATA%\PaperBridge`. Translation API keys are stored separately as generic credentials in Windows Credential Manager and are never written to settings JSON or SQLite.

## Network requests

Opening, indexing, annotating, searching, and rendering PDFs are local operations. Network requests occur only after the user configures an OpenAI, DeepSeek, or OpenAI-compatible endpoint and asks for translation or AI reading assistance. Selected text, nearby context, glossary constraints, and custom instructions needed for that action are sent to the configured provider. The provider's own privacy and retention terms apply.

PaperBridge does not upload an entire library in the background. Full-document translation and summary actions do send document text in bounded chunks because that is the requested operation.

## Removal and backup

Normal uninstall preserves local data and credentials. Destructive removal requires explicit `-DeleteUserData` and/or `-DeleteCredentials` switches. Data backups intentionally exclude credentials. See `INSTALLATION_AND_UNINSTALL.md` and `BACKUP_AND_RECOVERY.md` in the release archive.

## Sensitive documents

Users are responsible for confirming that a document may be processed by their selected API provider. For confidential or restricted PDFs, do not invoke network-backed translation or AI features unless permitted.
