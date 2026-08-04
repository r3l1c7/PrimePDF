# Security policy

The whole job of this app is opening files sent by other people, so untrusted PDF input
is the threat model that matters. The README has the [full write-up](README.md#security):
what was checked, what was fixed during the audit, and what risk remains.

## Reporting a vulnerability

**Please do not open a public issue for a vulnerability.**

Use [private vulnerability reporting](https://github.com/r3l1c7/PrimePDF/security/advisories/new)
on this repository. If you can, include:

- a sample PDF that triggers it, and
- what you observed — a crash, a hang, unexpected file or network access, or text
  surviving a redaction.

A redaction that leaves recoverable text behind is treated as a security bug, not a
cosmetic one. The engine suite asserts against the raw bytes of the saved file for
exactly this reason.

Expect an acknowledgement within a few days. This is a single-maintainer project, so
please allow reasonable time for a fix before disclosing publicly.

## In scope

- Anything reachable by opening, rendering, OCRing, editing or saving a PDF.
- The per-user file-association registration under `HKEY_CURRENT_USER`.
- The local settings file and its deserialisation.
- Any path where a redacted or edited page still carries the original content.

## Out of scope

- Memory-safety bugs inside PDFium itself. Report those upstream to the
  [PDFium project](https://pdfium.googlesource.com/pdfium/); keeping the bundled build
  current is the mitigation here. Do tell us if a shipped PDFium is behind a known CVE.
- Anything that requires the attacker to already be running code as the user.
- The absence of AppContainer sandboxing, which is a known and documented gap rather
  than a finding.

## Supported versions

The project is early and unversioned. Fixes land on `main`; build from there.
