# ADR-0006: Run Argos model packages directly with CTranslate2 (no `argostranslate` at runtime)

- Status: accepted
- Date: 2026-11-20
- Evidence: [research/TRANSLATION-COMPATIBILITY.md](../research/TRANSLATION-COMPATIBILITY.md)

## Context

GlyphLens translates recognised screen text to English **offline**, using the free Argos Translate model packages
(`.argosmodel`: a CTranslate2 model, a SentencePiece tokenizer and metadata). The first plan was to import the
`argostranslate` Python library in the worker. The dependency research measured it on Windows (Python 3.11 and 3.12):

- The first `translate()` of every process makes an HTTP request to `raw.githubusercontent.com` (Stanza sentence-boundary
  resources) for **all 49** official into-English packages, and fails when the machine is offline. Other sentence splitters
  either download as well (`MINISBD`, `SPACY`) or silently skip the package (`NONE`).
- Importing it takes about 16 s and 295 MB of memory; the environment is about 980 MB (torch, spaCy, sympy, onnxruntime).
- Several other network entry points (`update_package_index`, `download`, `Package.update`, remote providers selected by
  environment variable) are one call away, and importing its settings module creates directories in the user's profile.
- `install_from_path` is an unvalidated `zipfile.extractall`.

The product rule is that normal operation never touches the network and nothing is downloaded automatically.

## Decision

The worker runs the Argos **model packages** with its own small engine on `ctranslate2` and `sentencepiece` only, reading the
package layout (`metadata.json`, `model/`, `sentencepiece.model`) directly, following the algorithm of Argos'
`apply_packaged_translation` (sentence split, SentencePiece encode, `translate_batch`, decode). `argostranslate` is not imported.

- Model import is a separate, explicit operation with our own validating extractor (zip-slip, symlinks, entry allow-list,
  entry/ratio/size caps enforced while streaming, metadata schema). Only the needed files are extracted; the bundled Stanza data
  (the network hazard) is skipped. A SHA-256 manifest is written.
- Package discovery, route finding (direct edge, else shortest path of at most two hops) and the cache's model version string are
  ours. The model version includes the engine tag, the package version and a hash of `model.bin`.
- The worker installs a fail-closed Python-level network guard at start-up as defence in depth. It cannot see native sockets.
- Translation uses `device="cpu"` explicitly and `compute_type="auto"` (int8).

## Consequences

- Import 0.25 s, model load 0.35 s, about 131 MB per loaded model, about 120 MB of dependencies (Lingua adds 290 MB).
- 40 of the 49 into-English packages work. **9 use a BPE tokenizer** (cs, da, es, fi, hu, id, lt, ro, uk), including Spanish; they
  are refused at import with `unsupported_package` until BPE support (a vendored subword-nmt `apply_bpe` plus `sacremoses`) is
  added. This is a roadmap item.
- We own the sentence splitter and the length chunking (CTranslate2 silently truncates over about 1024 input tokens), and keep
  parity with Argos by test rather than by construction.
- Argos is still the model source: users import packages they downloaded themselves. The package index carries no checksums or
  signatures, so importing an untrusted package remains a trust decision.

## Alternatives rejected

- Stock `argostranslate`: fails offline, heavy, network entry points (above).
- `argostranslate` with a patched sentence splitter: works offline but keeps the weight and the reachable network calls.
