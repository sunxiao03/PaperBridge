# PaperBridge 0.1.0 release notes

PaperBridge 0.1.0 is the first local Windows x64 preview for reading English academic PDFs with Chinese translation, terminology control, annotations, bookmarks, and evidence-linked AI assistance.

Highlights:

- High-resolution PDFium rendering, smooth bounded pixel scrolling, selectable text on the original PDF, direct selection translation, multi-tab reading, outlines, thumbnails, and a managed SQLite library.
- Collapsible library/navigation sidebars keep the document and translation panes spacious without removing access to document controls.
- OpenAI, DeepSeek, and OpenAI-compatible translation with Windows Credential Manager secrets, bounded caches/queues, glossary constraints, bilingual views, and full-document task cancellation.
- Highlights, underlines, notes, and bookmarks with fingerprint/geometry anchors and migration states.
- An independent AI reading window can remain open beside translation, shows whether the current scope comes from a PDF selection or chapter/page context, and includes guidance for custom AI instructions.
- Selection explanation, chapter/document summaries, and current-document BM25 Q&A with locally verified `E1…En` citations and page navigation.
- Daily verified SQLite snapshots, complete local-data backup/restore scripts, data-preserving uninstall by default, explicit data/credential deletion options, and an auditable self-contained ZIP.

This preview has no telemetry, cloud account, auto-update, OCR, code signing, or commercial support. Read `PRIVACY.md`, `KNOWN_LIMITATIONS.md`, and `SECURITY.md` before use.
