# ADR-0007: License the project under Apache-2.0

- Status: accepted
- Date: 2026-11-21
- Supersedes: the open question in `LICENSE-CHOICE.md` (now answered there too)

## Context

The repository was made public and a first preview release was requested, but no license was granted: by default nobody could lawfully
use, copy or redistribute the code or a build. The owner asked for the license to be chosen on their behalf.

Inputs: the runtime dependencies that a release redistributes or requires (Vortice.* and SharpGen.Runtime: MIT; the .NET runtime: MIT);
the optional Python engines the user installs (CTranslate2: MIT, ONNX Runtime: MIT, SentencePiece: Apache-2.0, PaddleOCR: Apache-2.0);
model packages that are never bundled and have their own licenses. No dependency is GPL or AGPL.

## Decision

Apache License 2.0 for the project's own code, documentation and website, with a `NOTICE` file and `THIRD-PARTY-NOTICES.md`.

Considered and rejected:

- **MIT**: also permissive, but no patent grant, which matters for OCR and translation code.
- **GPL-3.0 / AGPL-3.0**: forces derivative works open; incompatible with later closed distribution and with some contributors' needs.
  Not needed to protect a local, offline tool.
- **Source-available or proprietary**: contradicts the already public repository and the privacy promise that users can read what runs.
- **No license**: leaves everyone without rights; not acceptable for a public download.

## Consequences

- Released versions stay Apache-2.0 for everyone who received them; later versions may use other terms (copyright holder's choice).
- Every release must ship `LICENSE`, `NOTICE` and `THIRD-PARTY-NOTICES.md`, and the notices must be updated when a redistributed
  dependency changes.
- The name and icon are not licensed for endorsement use.
- Models stay outside the repository and releases; their licenses are the user's to check.
- The copyright holder is stated as "GlyphLens contributors"; the owner may replace it with a legal name in `NOTICE`.
