# Offline translation compatibility (Argos models, CTranslate2, language identification)

Date: 2026-11-20. Host: Windows 11 Home 10.0.26200, x64, Intel i7-12700H (14 cores / 20 threads), 15.8 GB RAM, CPU only.
Python 3.12.10 (python.org) and 3.11.18 (uv-managed), both 64-bit, `win_amd64` wheels only for the recommended path.
Disposable virtual environments and downloads live **outside the repository** under `D:\projects\glyphlens_OST\_spike3` (not committed).
Raw logs: `_spike3\results`. Everything here was measured on this machine on the date above; anything not measured is marked **NOT TESTED**.

This document answers: how should the GlyphLens worker run Argos translation models and identify languages, with the hard rule
**no automatic network activity, ever, during normal operation**.

## 1. Decisions

| # | Decision | Evidence |
| --- | --- | --- |
| 1 | **Run Argos models with our own thin engine: `ctranslate2` + `sentencepiece` reading the installed Argos package layout directly (option c). Do not import `argostranslate` in the worker.** | Stock `argostranslate` 1.11.0 contacts GitHub at the first `translate()` for every one of the 49 official into-English packages and fails when offline (section 3). Direct path: 0.25 s import, 0.35 s model load, 131 MB RSS, zero network attempts (sections 3, 4). |
| 2 | Minimal runtime dependencies for translation: `ctranslate2==4.9.0`, `sentencepiece==0.2.3` (pulls `numpy`, `PyYAML`). ~120 MB installed. | `pip check` clean on 3.11 and 3.12; section 4. |
| 3 | **Language identification: `lingua-language-detector` 2.2.0 (Python 3.12 only), restricted to the Argos-supported language set.** On Python 3.11 only Lingua 2.1.1 exists and it is far heavier (878 MB RSS, 15.7 s first call); use **py3langid** there or require 3.12 for the translation extra. | Section 7. |
| 4 | Model import is a separate, explicit operation with our own validating extractor (zip-slip, size and ratio caps, entry allow-list, metadata schema). Argos' `install_from_path` is `zipfile.extractall` with no validation and is not used. | Section 5. |
| 5 | Packages are looked up in our own directory by our own registry; no Argos environment variables are needed on the recommended path. | Section 6. |
| 6 | 40 of the 49 into-English packages use SentencePiece and work with the direct engine. **9 use a BPE tokenizer (`bpe.model`) - cs, da, es, fi, hu, id, lt, ro, uk - including Spanish. They are NOT supported by the direct engine in v1** and must be reported as `unsupported_package_format`. | Section 8. BPE end-to-end translation **NOT TESTED**. |

## 2. Options for running Argos models

| Option | Imports | Network behaviour (measured) | Verdict |
| --- | --- | --- | --- |
| (a) Stock `argostranslate` 1.11.0, default config | 16.3 s warm (33 s cold, 3.12), RSS 295 MB after import; 980 MB of site-packages (torch 513, spacy 92, sympy 66, onnxruntime 52, ...) | **Fails offline**; contacts `raw.githubusercontent.com` at first translate (section 3.1) | Rejected |
| (b) `argostranslate` with another `chunk_type` / sentence splitter | same | `MINISBD`: tries `github.com/LibreTranslate/MiniSBD/.../fr.onnx` at first translate. `NONE`: `get_translation` returns `None` (package silently skipped, `AttributeError` on use). `SPACY`: by code reading, `argostranslate/sbd.py:32` calls `networking.cache_spacy()` **at import**, which runs `spacy.cli.download("xx_sent_ud_sm")` when not cached (NOT TESTED). `STANZA`/default: case (a). Patching `stanza.Pipeline` to `download_method=None`: no network, but `KeyError: 'packages'` (bundled `resources.json` is an old format) | Rejected |
| (b') `argostranslate` with `StanzaSentencizer.split_sentences` replaced by our own splitter | same 16 s / 980 MB | Zero network attempts; output identical to (c) on 11 of 11 samples | Works but keeps the 16 s import, 980 MB environment, and several unaudited network entry points (`update_package_index`, `download`, `Package.update`, `apis.py`, `ARGOS_MODEL_PROVIDER=LIBRETRANSLATE/OPENAI`) one call away |
| **(c) Direct CTranslate2 + SentencePiece** | **0.25 s** (3.12), 0.50 s (3.11); no spaCy/stanza/torch/argostranslate in `sys.modules` | **Zero attempts** at import, install, load, translate | **Recommended** |

Side effect of stock Argos worth knowing: merely importing `argostranslate.settings` creates empty directories in the user profile
(`%USERPROFILE%\.local\share\argos-translate\packages`, `...\.config\argos-translate`, `...\.local\cache\argos-translate\downloads`) unless
`XDG_DATA_HOME`/`XDG_CONFIG_HOME`/`XDG_CACHE_HOME` are redirected first. They were created by this research (empty, left in place). The recommended path never imports Argos, so it creates nothing outside GlyphLens' own directory.

## 3. Network behaviour (evidence)

### 3.1 What tried to reach the network, and when

Test: official `translate-fr_en-1_9.argosmodel` installed, then `translate("Enregistrer sous...")`, with a socket guard that simulates a machine without network
(`socket.getaddrinfo` raises `gaierror 11001`) and records every attempt (script `_spike3\scripts\translate_test.py`, mode `stock`).

| When | What | Result |
| --- | --- | --- |
| `import argostranslate.*` (3.11 and 3.12) | nothing | no attempt (guard also tested with a raising `Blocked` exception) |
| `package.install_from_path` | nothing | local `extractall` only |
| `get_installed_languages()` | nothing | no attempt |
| **first `translate()`** (every process, every stanza-packaged model) | `GET https://raw.githubusercontent.com/stanfordnlp/stanza-resources/main/resources_1.10.0.json` | `requests.ConnectionError` raised out of `translate()`: **translation does not work offline** |
| first `translate()` with `ARGOS_CHUNK_TYPE=MINISBD` | `urlopen https://github.com/LibreTranslate/MiniSBD/releases/download/v0.0.1/fr.onnx` | `gaierror` raised |

Call chain of the first row: `translate.py:64 ITranslation.translate` -> `CachedTranslation.hypotheses:328` -> `PackageTranslation.hypotheses:201` ->
`apply_packaged_translation:451` -> `sbd.StanzaSentencizer.split_sentences:160` -> `lazy_pipeline:149` -> `stanza.Pipeline.__init__` (`stanza/pipeline/core.py:208`)
-> `resources/common.py:459 download_resources_json` -> `requests.get`.
Root cause: stanza 1.10.1's `Pipeline` defaults to `download_method=DOWNLOAD_RESOURCES`, and Argos 1.11.0 does not pass `download_method`. All 49 official into-English packages bundle a `stanza/` SBD directory, so all take this path.
Patching the default to `None` stops the network call but fails with `KeyError: 'packages'`: the bundled `stanza/resources.json` (141 top-level keys) is an older format than stanza 1.10.1 expects.
With network available the stock path would write a fresh `resources.json` into the model directory and then work, contacting GitHub on the first translation of every process (**NOT TESTED with network enabled**; deliberately not done).

Other network entry points in Argos (code reading, **NOT TESTED**): `package.update_package_index()`, `AvailablePackage.download()`, `Package.update()`, `apis.py` (LibreTranslate/OpenAI providers), `cache_spacy()`.
They run only on explicit calls except `cache_spacy()` under `ARGOS_CHUNK_TYPE=SPACY`. `ARGOS_MODEL_PROVIDER` can select non-local providers; the recommended path never reads it.

### 3.2 Reproducible proof the recommended path makes zero network calls

`direct_translate.py` installs a guard at the top of the process, **before** any import, that replaces
`socket.socket.connect`, `connect_ex`, `socket.create_connection`, `getaddrinfo`, `gethostbyname` and `urllib.request.urlopen` with functions that
**record the attempt and raise** (recording matters: a library can swallow the exception). It prints `network_attempts` at the end.

```python
ATTEMPTS = []
def _deny(name):
    def f(*a, **k):
        ATTEMPTS.append(name + " " + repr(a)[:80])
        raise socket.gaierror(11001, "getaddrinfo failed (simulated offline)")
    return f
socket.socket.connect = _deny("connect"); socket.socket.connect_ex = _deny("connect_ex")
socket.create_connection = _deny("create_connection"); socket.getaddrinfo = _deny("getaddrinfo")
socket.gethostbyname = _deny("gethostbyname")
import urllib.request; urllib.request.urlopen = _deny("urlopen")
```

Result on Python 3.12.10 and 3.11.18 (minimal envs): `network_attempts: []` across import, validated install, engine load, cold first translation, 11 sample translations and 35 benchmark calls.
Lingua 2.2.0 / 2.1.1, langid, py3langid and langdetect were run under the same guard: no attempts.
The guard is Python-level only. It cannot see native code opening sockets (CTranslate2 and SentencePiece are native). Additional (cheap) check that was **NOT performed**: run the worker under a Windows Firewall outbound block rule or watch with `netstat`/ETW; recommended as a CI-independent manual check. A shipped worker should also install the guard (fail closed) at start-up; section 9 lists this.

## 4. The direct engine

Prototype: `_spike3\scripts\direct_translate.py` (appendix). Tested with the official `translate-fr_en-1_9` package (section 10).

### 4.1 Runtime dependencies (exact, tested)

| Package | Version | Wheel | Installed size | Licence |
| --- | --- | --- | --- | --- |
| `ctranslate2` | 4.9.0 | `cp311`/`cp312` `win_amd64` | 61.9 MB | MIT |
| `sentencepiece` | 0.2.3 | `cp311`/`cp312` `win_amd64` | 3.6 MB | Apache-2.0 |
| `numpy` (dep of ctranslate2) | 2.5.5 on 3.12, 2.4.6 on 3.11 | `win_amd64` | 51 MB | BSD-3 and others |
| `PyYAML` (dep of ctranslate2) | 6.0.3 | `win_amd64` | 0.7 MB | MIT |

`pip install --only-binary=:all: ctranslate2==4.9.0 sentencepiece==0.2.3` succeeded on 3.11.18 and 3.12.10; `pip check`: *No broken requirements found.* on both.
Total site-packages: about 120 MB (about 415 MB with Lingua, which alone is 290 MB installed / 170 MB wheel). Compare 980 MB for stock Argos.
Freeze files: `_spike3\results\freeze_min312.txt`, `freeze_min312_lingua.txt`, `freeze_min311_lingua.txt`.

### 4.2 Measured behaviour (fr->en, 79.9 MB `model.bin`)

| Metric | 3.12 | 3.11 |
| --- | --- | --- |
| import `ctranslate2` + `sentencepiece` | 0.25 s warm (1.1 s first ever run) | 0.5 s (2.1 s first run) |
| validated install (extract 82 MB, no Stanza) | 1.2 s | - |
| engine load (`Translator` + SP model) | 0.35 s | 0.4-0.65 s |
| RSS after load | 131 MB (`int8`, the `auto` choice) | 133 MB |
| first translation (cold) | 22-48 ms | 36-59 ms |
| short UI string (`Enregistrer sous...`), warm, beam 4 | median 41 ms, p95 45 ms | median 42 ms |
| 12-word sentence, warm, beam 4 | median ~180 ms | median 172 ms |
| same sentence, beam 1 (greedy) | ~52 ms | - |
| 30 OCR lines: 30 separate calls vs one `translate_batch` | 989 ms vs 529 ms (identical output) | - |

Compute type: `auto` resolves to int8 (131 MB, same speed as explicit `int8`); `float32` was 380 MB and 2x slower. Thread count (`intra_threads` 0/2/4) made no measurable difference. A single `Translator` is thread-safe (4 threads x 10 calls: no errors, identical output) but calls are effectively serialised (7.2 s for 40 calls), so use one translation thread/queue.
CPU only is enforced with `device="cpu"`: `ctranslate2.get_cuda_device_count()` returns 1 on this machine, so never rely on the default device.
One odd measurement: a 12-word sentence took 83 ms once cold but ~180 ms in a warm loop; cause not investigated (variance, hybrid P/E cores are a guess).

### 4.3 Correctness and semantic limits

* **Equivalence**: for 11 of 11 sample inputs (UI words, sentences, multi-line, numbers, accents, all-caps, empty) the direct engine output equals the Argos library output when both use the same sentence splitter (`_spike3\results\argos_regex.json` vs `direct_auto.json`).
* Algorithm copied from Argos (`apply_packaged_translation`): split text on `\n` into paragraphs; split a paragraph into sentences; `sp.encode(out_type=str)`; `translate_batch(..., replace_unknowns=True, max_batch_size=32, batch_type="tokens", beam_size=4, num_hypotheses=1, length_penalty=0.2)`; concatenate hypothesis tokens; `sp.decode_pieces(...)`; replace `U+2581` with a space; drop one leading space; strip `target_prefix` if the package sets one (none of the 49 into-English packages does).
* Deliberate difference: Argos' decoder replaces every `_` with a space (`filename_x.txt` -> `filename x.txt`). The direct engine keeps underscores (`file_name.txt cannot be found`).
* Sentence splitting is ours: `(?<=[.!?\u2026\u3002\uff01\uff1f])\s+(?=\S)` per line. Known weakness: abbreviations ("M. Dupont") split wrongly. For OCR output each recognised line is already a segment; translate per OCR line (batched) and let the caller decide whether to join lines of one paragraph.
* **Length limit - must be enforced by the engine**: CTranslate2 defaults `max_input_length=1024`, `max_decoding_length=256` tokens. A 480-token unpunctuated input produced a 157-word output that ends in repetition ("eats the grey mouse eats the grey mouse ..."), i.e. silent truncation/degeneration. Chunk long segments (suggested: at most ~100-150 tokens per segment, splitting at punctuation, then spaces) and set an application limit on total text per request.
* Garbage in, garbage out: `Enreg1strer sous... Ann u1er` -> `Register under... Annual`. The pipeline should expose OCR confidence (Windows OCR gives none) rather than imply certainty.
* Already-English text sent to a fr->en model is returned nearly unchanged (`Hello world, this is already English.`), but the app should skip translation by language identification, not rely on this.

## 5. Argos package layout and import validation

### 5.1 Layout (verified on `translate-fr_en-1_9.argosmodel`, 66,585,033 bytes)

Zip with a single top-level directory named like the archive (`translate-fr_en-1_9/`):

```
translate-fr_en-1_9/README.md                          400 B  (authors, OPUS-MT citation, licence: CC-BY 4.0)
translate-fr_en-1_9/metadata.json                      134 B
translate-fr_en-1_9/sentencepiece.model            802,397 B  (or bpe.model for the 9 BPE packages)
translate-fr_en-1_9/model/config.json                  196 B  (CTranslate2 model config)
translate-fr_en-1_9/model/model.bin             79,860,078 B  (int8 CTranslate2 weights)
translate-fr_en-1_9/model/shared_vocabulary.json 1,112,211 B
translate-fr_en-1_9/stanza/resources.json         121,862 B  (Stanza SBD data: NOT needed, network hazard)
translate-fr_en-1_9/stanza/fr/tokenize/gsd.pt     659,647 B  (NOT needed)
```

`metadata.json` of that package (complete):
`{"package_version":"1.9","argos_version":"1.9.0","from_code":"fr","from_name":"French","to_code":"en","to_name":"English"}`.
Argos' loader also reads optional `code`, `type` (default `"translate"`), `target_prefix` (default `""`), `links`, `languages`, `from_codes`, `to_codes`, `dependencies`, `source_languages`, `target_languages`.
Across the 49 into-English packages (read via HTTP range requests of the zip central directory, `_spike3\results\remote_listing.json`): all `type == translate`, all `target_prefix == ""`, all bundle exactly `stanza/` as SBD data, 40 have `sentencepiece.model`, 9 have `bpe.model`, none has both or neither.
Multi-target packages (`target_prefix`, `to_codes`, en->X direction) were **NOT inspected**.

### 5.2 Validation rules for a user-supplied `.argosmodel` (implemented in the prototype, appendix)

Reject (with a stable error code, never echoing archive text into logs) unless **all** hold:

1. `zipfile.is_zipfile`; entry count <= 2000.
2. Every entry name uses `/` only; is not absolute; has no drive letter, no `..`, no empty or `.` segment (zip-slip). Entry is not a symlink (`external_attr >> 16 & 0o170000 == S_IFLNK`).
3. Exactly one top-level directory, matching `^[A-Za-z0-9._-]{1,64}$`; second-level names restricted to an allow-list: `metadata.json`, `README.md`, `LICENSE`, `LICENSE.md`, `LICENSE.txt`, `sentencepiece.model`, `bpe.model`, `model/`, `stanza/`, `minisbd/`, `spacy/`.
4. Sum of uncompressed sizes <= 2 GiB (largest official package is 314 MB unzipped), enforced **while streaming** (not just from headers, which can lie); per-entry expansion ratio <= 400 for entries over 1 MB.
5. `metadata.json` exists, <= 64 KiB, JSON object; `from_code` and `to_code` match `^[A-Za-z]{2,3}([-_][A-Za-z0-9]{2,8})?$`; `type` is `translate` (or absent); `package_version` is a string (used for display and the model version string).
6. `model/model.bin` and `model/config.json` present; `sentencepiece.model` present (else `unsupported_tokenizer`, reporting BPE explicitly).

Install procedure: extract **only the needed files** (skip `stanza/`, `minisbd/`, `spacy/`: 659 KB + 122 KB for fr, and they are the network hazard) into `.staging-<uuid>` under the models root, then `os.replace` into `<from>_<to>` (atomic on one volume; remove a previous version first). On any failure delete the staging directory. Never extract directly into the final directory. Compute SHA-256 of the archive (and of `model.bin`) during import and store it in our own manifest (`glyphlens-model.json`: archive sha256, model.bin sha256, from/to codes and names, package_version, licence text hash, import time, byte size).
Observed cost: 1.2 s for fr (82 MB), dominated by `model.bin`.

Not done by Argos and not solved by validation: **there is no signature or trusted hash** for packages (the package index carries no checksum field; the index entries have `links` only). `model.bin` is parsed by native code, so importing an untrusted package is a trust decision for the user; the import UI should show the file name, size, SHA-256, languages, version and README/licence text and require confirmation.
Licences differ per package: of the 49 READMEs, 29 state CC-BY 4.0, one CC-BY-SA (et), one a "no rights reserved" paracrawl note (ru), 18 contain no recognisable licence line (**not verified further**). Display the README on import; never assume a licence.

### 5.3 Where models come from (for documentation, never fetched by the app)

The official index (`https://raw.githubusercontent.com/argosopentech/argospm-index/main/index.json`, 100 packages, fetched once during this research) links
`https://argos-net.com/v1/translate-<from>_<to>-<ver>.argosmodel` (for example `translate-fr_en-1_9.argosmodel`). The user downloads the file themselves; GlyphLens only imports a local file. `HEAD` requests to `argos-net.com` fail; `Range` GETs work and need a non-default `User-Agent`.

## 6. Installing and discovering packages (custom directory)

Recommended path: **no Argos settings or environment variables are needed**, because Argos is not imported.
GlyphLens owns the layout, for example `%LOCALAPPDATA%\GlyphLens\models\<from>_<to>\{metadata.json, sentencepiece.model, model\...}` plus `glyphlens-model.json`.
Discovery = list subdirectories, read `metadata.json`, validate the same rules, load lazily on first use (0.35 s per model, ~130 MB RSS each, so keep an LRU of loaded translators, e.g. 2-3).

If Argos is ever imported (for development comparison only), it must run with, set **before** the import:
`ARGOS_PACKAGES_DIR=<dir>` (read in `settings.py:138`), `XDG_DATA_HOME`/`XDG_CONFIG_HOME`/`XDG_CACHE_HOME=<private dirs>` (otherwise it creates directories in the profile), `ARGOS_DEVICE_TYPE=cpu`, and the network guard in section 3.2. Argos installs packages by extracting the archive into `ARGOS_PACKAGES_DIR` and discovers them by constructing a `Package` for **every** subdirectory of the package directories (`package.get_installed_packages`); a subdirectory without `metadata.json` raises `FileNotFoundError`, so a stray folder breaks discovery. These settings were verified (packages landed in the isolated directory) but are not required by the recommended path.

## 7. Language identification

All candidates ran in isolated venvs under the section 3.2 guard (no attempts). Corpus: 21 languages x 6 strings (2 single words, 2 two-to-three-word UI labels, 1 sentence, 1 paragraph; 126 items) plus 12 "noise" strings (numbers, URL, code, `OK`, `###`, garbled OCR, Latin). Candidate set for restricted detectors: the 49 Argos source languages + English (`pb`->`pt`, `zt`->`zh`). The corpus is small and hand-written by us; it gives relative ranking, not production accuracy. Files: `_spike3\scripts\corpus.py`, `detect_bench.py`, `results\detect_*.json`, `detect_summary.txt`.

| Detector | Version / wheel | Installed | Word | Label | Sentence | Paragraph | Overall | Noise flagged (conf < 0.9) | Median / p95 per call | Import | First call | RSS |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Lingua, restricted** | 2.2.0, cp312 win_amd64, 170.1 MB | 290.6 MB | **88.1 %** | **92.9 %** | 100 % | 100 % | **93.7 %** | 12/12 | 0.21 ms / 2.2 ms | 0.01 s | 17.7 ms | 127 MB |
| Lingua, all 75 languages | 2.2.0 | same | 85.7 % | 92.9 % | 95.2 % | 100 % | 92.1 % | 12/12 | 0.32 ms / 3.4 ms | 0.01 s | 25.9 ms | 161 MB |
| Lingua, low-accuracy mode | 2.2.0 | same | 78.6 % | 81.0 % | 100 % | 100 % | 86.5 % | 11/12 | 0.32 ms / 2.7 ms | 0.01 s | 20 ms | 116 MB |
| langid | 1.1.6 (sdist only, built locally) | 4.9 MB (+numpy) | 76.2 % | 88.1 % | 100 % | 100 % | 88.1 % | 12/12 | 2.5 ms / 2.8 ms | 0.72 s | 3 ms | 53 MB |
| py3langid | 0.4.0 (wheel, 4.6 MB) | 4.4 MB (+numpy) | 66.7 % | 85.7 % | 100 % | 100 % | 84.1 % | 12/12 | 0.04 ms / 0.23 ms | 0.37 s | 0.5 ms | 123 MB |
| langdetect | 1.0.9 (sdist only) | 2.3 MB | 66.7 % | 88.1 % | 100 % | 100 % | 84.9 % | **6/12** | 3.6 ms / 36 ms | 0.08 s | **1.6 s** | 85 MB |

fastText `lid.176` and CLD3 were **not tested** (no trivially installable win_amd64 wheels for 3.12 and a separate model file/protobuf build).

Findings:

* Lingua is the most accurate on short text and the only one with a sound confidence value: `compute_language_confidence_values(text)` returns a probability distribution over the **candidate set** (sorted, sums to 1). Languages can be restricted with `LanguageDetectorBuilder.from_languages(...)`; this was tested with 49+1 languages. `from_iso_code_639_1(getattr(IsoCode639_1, "FR"))` is the working mapping (`IsoCode639_1["FR"]` raises).
* langdetect is unusable as a gate: confidence 1.0 on `OK`, `A1`, `Windows 11 Pro` (called Portuguese, Hungarian, Polish), needs a fixed seed for determinism, 1.6 s first call. langid/py3langid confidences are low for noise (<= 0.24) but also low for many real short strings; py3langid is the lightest and gave 100 % precision at confidence >= 0.7 but only 58 % coverage on short text.
* Short-text errors for Lingua: `pt`->`es` (2), `en`->`it`, `en`->`nl`, `it`->`ro`, `ru`->`bg`, `pl`->`ro`, `ja`->`zh` (a Han-only word such as a kanji label is genuinely ambiguous between Japanese and Chinese; use the OCR language and script hints, see below). No error on any sentence or paragraph.
* **Python version constraint**: Lingua 2.2.0 has `Requires-Python: >=3.12`. On Python 3.11 only 2.1.1 is installable; measured on 3.11.18: same accuracy (93.7 %) but **first call 15.7 s and RSS 878 MB**. If the worker must stay on 3.11, use py3langid for identification (or require 3.12 for the translation extra). The worker's `pyproject.toml` currently says `requires-python = ">=3.11"`.
* Wheel size matters for distribution: Lingua is 170 MB to download, 290 MB installed. It lives in the user's worker environment, never in the repository.
* Mixed-script/CJK: Lingua also separates `ko` (Hangul) and `zh`/`ja` well on sentences; only Han-only fragments are ambiguous.

### Confidence semantics and recommended thresholds

Lingua confidence is **relative to the candidate set**. Therefore:

1. **Always detect over the full Argos-supported set (49+1 languages), not only over the installed models.** If restricted to installed languages (say fr and en), a Spanish line would be named French with high confidence. After detection, check whether a model for the detected language is installed and, if not, return `model_missing` for that language (honest state) instead of forcing a wrong language.
2. Recommended gate (suggestion derived from the numbers above; not tuned on real OCR data): treat as **uncertain** when any of: fewer than 3 letter characters; top confidence < 0.5 (on our short fragments: coverage 80 %, precision 97 %); or top confidence < 0.7 for a single word. At >= 0.7: coverage 67 %, precision 98 %. All 12 noise strings had top confidence <= 0.16. Margin between first and second candidate was **NOT evaluated**.
3. Use the user's OCR language / chosen source language as a *prior*: when it equals the detected language, accept at the lower 0.5 gate; when it differs and confidence < 0.9, report `language_uncertain` and let the user choose. (Policy suggestion, NOT TESTED.)
4. Detection operates on the whole recognised line or paragraph (join lines of one region first); do not detect on one-word regions in isolation if a neighbouring region has more text (NOT TESTED).
5. Skip translation when the detected language is English with confidence >= 0.5 (already-English text); keep the original.

## 8. Catalog: official into-English packages (49)

Sizes are the zip size from `Content-Range`; "BPE" packages need a different tokenizer (section 1, row 6). All bundle Stanza SBD data (ignored). Total of all 49: 4.47 GB.

| Smallest / common | zip MB | tokenizer | | Larger | zip MB | tokenizer |
| --- | --- | --- | --- | --- | --- | --- |
| vi | 65.9 | SP | | ja | 117.2 | SP |
| **fr** (tested) | 66.6 | SP | | ko | 118.9 | SP |
| nb | 67.4 | SP | | tr | 120.6 | SP |
| id | 68.7 | **BPE** | | de | 150.5 | SP |
| pt / pb | 69.4 | SP | | ru | 156.2 | SP |
| nl | 69.9 | SP | | **es** | 285.2 | **BPE** |
| pl | 71.7 | SP | | it | 87.2 | SP |
| zh / zt | 74.5 | SP | | ar | 81.9 | SP |
| uk | 76.1 | **BPE** | | hi | 102.4 | SP |

Full table: `_spike3\results\sizes_all.txt` and `remote_listing.txt`. BPE: cs, da, es, fi, hu, id, lt, ro, uk.

**BPE support (NOT TESTED end-to-end)**: Argos' BPE path is `sacremoses` (`MosesPunctNormalizer`, `MosesTokenizer`, `MosesDetokenizer`) + `argostranslate/apply_bpe.py` (subword-nmt style). Tokenizer round-trip was verified offline using only the 362,679-byte `bpe.model` range-read from the `es` package (no model weights): `Configuración de la impresora, por favor reinicie la aplicación.` -> `['Con@@', 'figuración', 'de', 'la', ...]` (13 tokens) -> decoded back identically; import plus load 3.6 s (sacremoses alone 1.5 s). Supporting these 9 packages means vendoring `apply_bpe.py` (check licence, it is subword-nmt-derived) and adding `sacremoses` (+ `regex`, `click`, `joblib`, `tqdm`); translation with a real BPE model.bin was not run. Importing the whole `argostranslate.tokenizer` module is light (0.22 s; its `__init__` is empty) but needs `argostranslate` installed, which defeats the minimal environment.

## 9. Pivot routes and model version string

Argos behaviour (code reading of `translate.get_installed_languages`; **pivot translation NOT TESTED**, only one model was installed): every installed package is an edge `from_code -> to_code`. Every language gets an identity translation. Then, for each language, Argos repeatedly adds `CompositeTranslation(first, second)` whenever a language can reach an intermediate language that has an onward edge to a language not yet reachable; the loop runs until no new route appears. This is a fixed-point closure over **any** intermediate language (not only English), and the first route found wins, with no cost weighting. `Language.get_translation` returns the first matching `to_lang`. With the official catalog (into-English and from-English packages) all non-English pairs therefore route through English: `de -> en -> fr` = two models, two translations in sequence, the English intermediate text is the input of the second hop.

Recommended for GlyphLens: implement the route finder ourselves over the installed registry: breadth-first search on edges; prefer a direct edge; otherwise the shortest path (at most 2 hops for v1, reject longer); return `model_missing` naming the exact missing hop (`en->fr`). Cost doubles (about 2x latency, quality loss compounds); show the route to the user. v1 target is English only (`x -> en` is always one hop); the route finder is the extension point for other targets. Only into-English packages were inspected, so whether the en->x packages share the `sentencepiece.model` layout is **NOT TESTED**.

Model version string for cache keys and invalidation (the translation cache key is normalized text + source + target + model version): per hop, `argos:<from>-<to>:<package_version>:<first 12 hex of sha256(model.bin)>`, hops joined with `>`, prefixed by the engine tag `ct2-<ctranslate2 version>` and a splitter/engine revision constant, for example
`ct2-4.9.0/seg1|argos:fr-en:1.9:12f4dd41956b`. Compute the hash once at import time and store it in `glyphlens-model.json` (do not hash 80 MB on every start); re-importing, deleting, or changing `compute_type`/`beam` changes the string and invalidates cache entries. `package_version` alone is not enough because users can import a different file with the same version.

## 10. The model that was downloaded, and how to reproduce

Exactly one model package was downloaded (explicit research step, then installed in two isolated locations):

* File: `D:\projects\glyphlens_OST\_spike3\models\translate-fr_en-1_9.argosmodel`
* Source: `https://argos-net.com/v1/translate-fr_en-1_9.argosmodel`
* Size: 66,585,033 bytes; **SHA-256 `3b3052fee6bb1e8e8e632a26a723eb2a2c7710dfe73ba61ffd9b83e85d4f14c1`**; downloaded in 7 s.
* Installed copy used by the direct engine: `D:\projects\glyphlens_OST\_spike3\data\direct-models-min\fr_en` (files: `model\model.bin` sha256 `12f4dd41956b8975067b710675ce0668073b4d682892a9b1c496287cecbd0802`, 79,860,078 B; `sentencepiece.model` sha256 `78d0e717c77053f1c4b856d8661d9cb87c64f083a35418c087b9146300e4f585`; `metadata.json` sha256 `b10f3c55823794941855b002d8056db85cb5ae19e58ceb3349f2f27461255188`).
* Licence: OPUS-MT (Tiedemann and Thottingal), CC-BY 4.0 per the package README; attribution required if redistributed.
* The only other network content fetched: the package index JSON, HTTP range reads of zip directories and of one 362,679-byte `bpe.model` (es). No other model weights.

Best end-to-end offline translation, one command (minimal env, network guarded, validated import, 11 samples, benchmarks):

```powershell
$s='D:\projects\glyphlens_OST\_spike3'; $env:PYTHONIOENCODING='utf-8'
& "$s\envs\min312\Scripts\python.exe" "$s\scripts\direct_translate.py" "$s\models\translate-fr_en-1_9.argosmodel" "$s\data\direct-models-min" auto 0 4 "$s\scripts\samples.json"
```

Arguments: archive, models root, compute type, intra threads, beam size, samples JSON. Expect `network_attempts: []`, `Save As...` for the first translation, `modules_loaded_heavy: []`.
Reproduce the stock-Argos failure (needs an Argos env; section 3.1):

```powershell
$s='D:\projects\glyphlens_OST\_spike3'; $env:PYTHONIOENCODING='utf-8'
$env:XDG_DATA_HOME="$s\data\xdg-data"; $env:XDG_CONFIG_HOME="$s\data\xdg-config"; $env:XDG_CACHE_HOME="$s\data\xdg-cache"
$env:ARGOS_PACKAGES_DIR="$s\data\packages"; $env:ARGOS_DEVICE_TYPE='cpu'; $env:BLOCK_ERR='oserror'
& 'D:\projects\glyphlens_OST\_spike2\py312-mt2\Scripts\python.exe' "$s\scripts\translate_test.py" "$s\models\translate-fr_en-1_9.argosmodel" stock
```

Expect `first_translate_error: ConnectionError ... raw.githubusercontent.com ... resources_1.10.0.json` and one `getaddrinfo` attempt.

## 11. Not tested / open risks

* Translation with a BPE package (es, id, ...): NOT TESTED; only the tokenizer round trip. Spanish is the largest (285 MB) and BPE.
* Any package other than fr->en was never loaded. Quality was spot-checked on 11 French strings only; no BLEU or other quality measurement. Non-Latin source languages (ja, zh, ko, ar, ru) NOT TESTED, including whether `sentencepiece.model` byte-fallback and our sentence splitter behave for CJK.
* Pivot routes (two installed models): NOT TESTED. en->x packages: NOT INSPECTED.
* Stock Argos with network available: NOT TESTED (deliberately). `ARGOS_CHUNK_TYPE=SPACY` import-time download: code reading only.
* The Python-level socket guard cannot see native sockets; no firewall/ETW verification was done.
* Detector accuracy is from a small hand-written corpus (no OCR noise except 1 garbled string, no real screenshots). fastText/CLD3 not evaluated. Margin-based uncertainty not evaluated.
* Windows OCR language tags to Argos codes (`zh-Hans`->`zh`, `zh-Hant`->`zt`, `pt-BR`->`pb`, `nb-NO`->`nb`, `he-IL`->`he`) need an explicit mapping table; not verified against an installed OCR language list here.
* CUDA is present on this machine (`get_cuda_device_count() == 1`); GPU paths were not used and must stay disabled (`device="cpu"`).
* Python 3.11 + Lingua 2.1.1 memory/latency problem (section 7) needs a decision from the worker owner.
* Memory with several loaded translators and Lingua together was not measured (estimate 130 MB per int8 model + 127 MB Lingua + interpreter).

## Appendix: prototype (condensed from `_spike3\scripts\direct_translate.py`)

```python
# Validated import + direct engine. No argostranslate import. Network guard omitted here (section 3.2).
import json, os, re, shutil, stat, uuid, zipfile
from pathlib import Path, PurePosixPath

MAX_ENTRIES, MAX_TOTAL, MAX_META, MAX_RATIO = 2000, 2 * 1024**3, 64 * 1024, 400
CODE = re.compile(r"^[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{2,8})?$")
ROOT = re.compile(r"^[A-Za-z0-9._-]{1,64}$")
ALLOWED_TOP = {"metadata.json", "README.md", "LICENSE", "LICENSE.md", "LICENSE.txt",
               "sentencepiece.model", "bpe.model", "model", "stanza", "minisbd", "spacy"}

class ImportRejected(Exception):
    def __init__(self, code, detail=""):
        super().__init__(f"{code}: {detail}"); self.code = code

def validate_and_install(archive: Path, models_root: Path) -> Path:
    if not zipfile.is_zipfile(archive): raise ImportRejected("not_a_zip")
    with zipfile.ZipFile(archive) as z:
        infos = z.infolist()
        if len(infos) > MAX_ENTRIES: raise ImportRejected("too_many_entries")
        total, root, files = 0, None, []
        for i in infos:
            n = i.filename
            if "\\" in n or n.startswith("/") or re.match(r"^[A-Za-z]:", n): raise ImportRejected("bad_path", n)
            parts = PurePosixPath(n).parts
            if not parts or ".." in parts or any(p in ("", ".") for p in parts): raise ImportRejected("bad_path", n)
            if (i.external_attr >> 16) & 0o170000 == stat.S_IFLNK: raise ImportRejected("symlink", n)
            if not ROOT.match(parts[0]): raise ImportRejected("bad_root", parts[0])
            root = root or parts[0]
            if parts[0] != root: raise ImportRejected("multiple_roots", parts[0])
            if len(parts) > 1 and parts[1] not in ALLOWED_TOP: raise ImportRejected("unexpected_entry", n)
            if i.is_dir(): continue
            total += i.file_size
            if total > MAX_TOTAL: raise ImportRejected("too_large_uncompressed")
            if i.compress_size and i.file_size / i.compress_size > MAX_RATIO and i.file_size > 1_000_000:
                raise ImportRejected("suspicious_ratio", n)
            files.append(i)
        meta_info = next((i for i in files if i.filename == f"{root}/metadata.json"), None)
        if meta_info is None or meta_info.file_size > MAX_META: raise ImportRejected("metadata_missing_or_large")
        meta = json.loads(z.read(meta_info))
        if (not isinstance(meta, dict) or not CODE.match(str(meta.get("from_code", "")))
                or not CODE.match(str(meta.get("to_code", "")))): raise ImportRejected("bad_metadata")
        if meta.get("type", "translate") != "translate": raise ImportRejected("not_a_translation_package")
        names = {i.filename for i in files}
        if f"{root}/model/model.bin" not in names or f"{root}/model/config.json" not in names:
            raise ImportRejected("missing_ctranslate2_model")
        if f"{root}/sentencepiece.model" not in names:
            raise ImportRejected("unsupported_tokenizer", "bpe" if f"{root}/bpe.model" in names else "none")
        final = models_root / f"{meta['from_code']}_{meta['to_code']}"
        staging = models_root / f".staging-{uuid.uuid4().hex}"
        staging.mkdir(parents=True)
        try:
            written = 0
            for i in files:
                rel = PurePosixPath(i.filename).relative_to(root)
                if rel.parts[0] in ("stanza", "minisbd", "spacy"): continue   # not needed, network hazard
                out = staging.joinpath(*rel.parts); out.parent.mkdir(parents=True, exist_ok=True)
                with z.open(i) as src, open(out, "wb") as dst:
                    while b := src.read(1 << 20):
                        written += len(b)
                        if written > MAX_TOTAL: raise ImportRejected("too_large_uncompressed")
                        dst.write(b)
            if final.exists(): shutil.rmtree(final)
            os.replace(staging, final)
        except BaseException:
            shutil.rmtree(staging, ignore_errors=True); raise
    return final

_SENT = re.compile(r"(?<=[.!?\u2026\u3002\uff01\uff1f])\s+(?=\S)")

class DirectEngine:
    def __init__(self, pkg_dir: Path, compute_type="auto", intra_threads=0, beam=4):
        import ctranslate2, sentencepiece as spm
        self.meta = json.loads((pkg_dir / "metadata.json").read_text(encoding="utf-8"))
        self.sp = spm.SentencePieceProcessor(model_file=str(pkg_dir / "sentencepiece.model"))
        self.prefix, self.beam = self.meta.get("target_prefix", ""), beam
        self.tr = ctranslate2.Translator(str(pkg_dir / "model"), device="cpu", inter_threads=1,
                                         intra_threads=intra_threads, compute_type=compute_type)

    def translate(self, text: str) -> str:
        out = []
        for line in text.split("\n"):
            sents = [s for s in _SENT.split(line) if s] if line.strip() else []
            if not sents: out.append(line); continue
            toks = [self.sp.encode(s, out_type=str) for s in sents]   # TODO: chunk > ~120 tokens (section 4.3)
            res = self.tr.translate_batch(toks, target_prefix=[[self.prefix]] * len(toks) if self.prefix else None,
                replace_unknowns=True, max_batch_size=32, batch_type="tokens", beam_size=self.beam,
                num_hypotheses=1, length_penalty=0.2)
            pieces = [p for r in res for p in r.hypotheses[0]]
            v = self.sp.decode_pieces(pieces).replace("\u2581", " ")
            if self.prefix and v.startswith(self.prefix): v = v[len(self.prefix):]
            out.append(v[1:] if v.startswith(" ") else v)
        return "\n".join(out)
```
