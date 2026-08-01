## What this changes

<!-- One or two sentences. Link the issue if there is one. -->

## The document that caused it

<!--
If this fixes a bug that a particular PDF triggered, attach the file (or a dummy one
that still reproduces it). A file beats a description.
-->

## Before it lands

- [ ] `dotnet run --project tests/EngineTests` passes (engine assertions, headless)
- [ ] `PrimePdf.exe --selftest sample.pdf` passes (interface assertions)
- [ ] Added the assertion that would have caught this
- [ ] No new network call, no new `Process.Start`, no new dependency — or it is explained below
- [ ] Redaction still removes the underlying text, and untouched pages are still copied through unchanged

## Anything a reviewer should know

<!-- Trade-offs, things you were unsure about, things you deliberately left out. -->
