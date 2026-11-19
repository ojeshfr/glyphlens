# License choice

**Status: not yet decided.**

No distribution license has been selected for GlyphLens. Until one is chosen and a `LICENSE` file
is added, the code is not licensed for redistribution and all rights are reserved by its authors.

## Inputs to the decision

The decision should be made deliberately by the project owner, considering at least:

- **Third-party engine licenses.** The proposed OCR (PaddleOCR) and translation (Argos Translate)
  stacks, their transitive dependencies, and any bundled model weights each carry their own
  licenses. These must be reviewed for compatibility before any distribution, including the
  licenses of individual language-model packages.
- **Distribution model.** Open source, source-available, or proprietary/commercial.
- **Packaging.** A bundled Python runtime and worker executable redistribute third-party code and
  require the corresponding notices.

## Until then

- Do not copy third-party code into this repository without recording its source and license.
- Do not add a `LICENSE` file without an explicit decision recorded here (and, ideally, an ADR).
