# Known limitations — 0.1.0

- Windows 10/11 x64 only. The release is self-contained but unsigned, so SmartScreen may warn.
- Complex multi-column, scanned, mathematical, or unusually encoded PDFs can produce imperfect text order or paragraph mapping. Scanned PDFs have no OCR fallback.
- Bilingual alignment is paragraph/page based and deliberately degrades to side-panel presentation when layout confidence is low.
- Translation and AI quality, latency, limits, retention, and availability depend on the configured third-party endpoint. Provider costs are the user's responsibility.
- Current-document Q&A is local BM25 retrieval with provider generation, not a global semantic research database. It refuses unverifiable citation forms but cannot guarantee factual correctness.
- Automatic backups cover the SQLite database once per UTC day and keep five snapshots. Managed PDFs and settings require the supplied full-data backup script.
- Full-data restores and database restores require PaperBridge to be closed. Credentials are never included in backups and must be recreated separately.
- There is no in-app backup/restore UI, auto-update, code signing, MSIX installer, cloud sync, multi-device merge, OCR, plugin system, or telemetry.
- The generated 500-page fixture and accelerated resource tests have passed. An eight-hour interactive desktop endurance session is a separate manual release gate and is not claimed as completed by the automated suite.
