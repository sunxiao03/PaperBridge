# Security Policy

## Supported version

Only the latest published PaperBridge release candidate is supported. Version 0.1.0 is an unsigned personal-project preview for Windows 10/11 x64.

## Reporting a vulnerability

Use the repository's **Security** tab to submit a private vulnerability report when that option is available. If it is unavailable, open a minimal public issue requesting a private contact channel, but do not include vulnerability details. Do not include API keys, private PDFs, database files, or exploit payloads containing third-party confidential data in a public report.

Include the PaperBridge version, Windows version, reproduction steps, and the smallest synthetic test file that demonstrates the issue. Acknowledgement and remediation timelines are best effort; this project has no commercial support commitment.

## Security boundaries

- API keys use Windows Credential Manager under `PaperBridge/translation/*`.
- The app is local-first but configured AI/translation actions send selected document text to the chosen endpoint.
- Release 0.1.0 is not code signed; users must verify `SHA256SUMS.txt` before installation.
- PDF parsing uses native PDFium. Treat untrusted PDFs as potentially hostile and keep Windows current.
- The application does not sandbox providers, PDFs, or custom endpoints beyond normal Windows process boundaries.
