# License choice

**Status: decided on 2026-11-21 - Apache License 2.0.** The text is in [LICENSE](LICENSE), the copyright notice in [NOTICE](NOTICE),
the third-party notices in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the reasoning in
[docs/decisions/ADR-0007-apache-2-license.md](docs/decisions/ADR-0007-apache-2-license.md).

The project owner asked for the choice to be made on their behalf and to be recorded here. It applies to the GlyphLens source in this
repository (the WPF app, the Python worker, the tools, the tests, the website), including the commits that were public before this
file was added.

## Why Apache-2.0

- It is permissive and widely understood: use, modify, redistribute, including commercially, with the licence and the notices kept.
- It has an explicit patent grant and a patent-retaliation clause. GlyphLens sits next to OCR and machine-translation code, where
  that matters more than for a typical UI tool.
- It is compatible with every runtime dependency that is redistributed or required (all MIT, Apache-2.0 or BSD-style; see the notices).
  It would not be compatible with GPLv2-only code, and none is used.
- Contributions are licensed to the project under the same terms by default (section 5 of the licence).

## What it does not cover

- **Models.** No model weights are in the repository or in a release. PaddleOCR (PP-OCRv5) and Argos Translate packages carry their own
  licences; the user imports them, and each must be checked before use or redistribution (see [docs/SECURITY.md](docs/SECURITY.md)).
- **Trademarks and names.** The licence grants no right to use the name "GlyphLens" or its icon to suggest endorsement.
- **Third-party code** keeps its own licence (THIRD-PARTY-NOTICES.md).

## Consequence the owner should know

A permissive licence cannot be taken back for copies already published: anyone who received the code or a release under Apache-2.0 keeps
that right for those versions. Later versions can be released under other terms by the copyright holder (and by contributors who agree).
If a closed or commercial distribution is planned, review this decision with a lawyer before publishing more.

## Rules that continue to apply

- Do not copy third-party code into this repository without recording its source and licence.
- Adding a dependency needs a licence check (docs/SECURITY.md); do not add GPL/AGPL code to the shipped app.
