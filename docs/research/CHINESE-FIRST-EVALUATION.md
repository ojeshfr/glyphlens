# Chinese-first evaluation (Simplified Chinese to English, Traditional second)

Date: 2026-11-20. Host: Windows 11 Home 10.0.26200, Intel i7-12700H (20 threads), 15.8 GB, CPU only (a CUDA GPU is present and was
**not** used: `device="cpu"` is explicit). Python 3.12.10. Everything below was measured on this machine on that date; anything not
measured is marked NOT TESTED. Reproduction commands are in [`eval/zh-en/README.md`](../../eval/zh-en/README.md).

## 1. Decisions

| Question | Answer | Evidence |
|---|---|---|
| Chinese translation model | Official Argos `translate-zh_en` 1.9 (OPUS-MT, CC-BY 4.0), run directly with CTranslate2 | section 3 |
| Traditional Chinese | Argos `translate-zt_en` 1.9: **byte-identical weights to zh_en**; supported, separate E2E run passed | section 3.1, 5 |
| Tokenizer | SentencePiece (`sentencepiece.model`), **not BPE**; the package was inspected, not assumed | section 3.1 |
| Chinese OCR | PaddleOCR **PP-OCRv5 mobile** (official ONNX files) on ONNX Runtime, 4 threads | section 4 |
| Windows OCR for Chinese | impossible on this machine (only `en-US` installed; adding a language needs administrator rights and a download) | section 4.1 |
| Language identification | built-in script rules (Han, kana, Hangul, Simplified vs Traditional by GB2312/Big5 character sets, plain English); Lingua/py3langid optional for other languages | section 6 |
| Grouping | geometric paragraph/bubble grouping before translation | section 7 |
| Alternative translation model | **not evaluated** (no time left in this session; recommended next step) | section 9 |

## 2. Translation package facts (verified from the downloaded files)

Downloaded once for this research, from the official index links, outside the repository (`D:\projects\glyphlens_OST\_spike3\models`):

| File | Size | SHA-256 |
|---|---|---|
| `translate-zh_en-1_9.argosmodel` (https://argos-net.com/v1/) | 74,481,402 B | `62e7af5a3a48b530e47b7b3e5c78c2de79073ecd815750d2bf3ab35b4a67da2d` |
| `translate-zt_en-1_9.argosmodel` | 74,498,034 B | `a9ea826d801f059dd47f1f47b6c1dc9d762519a80ea3afd2a982130a42a25486` |

Contents (zip listing): `metadata.json`, `README.md` (OPUS-MT citation, "licensed CC-BY 4.0"), `sentencepiece.model` (804,374 B),
`model/{config.json, model.bin 82,713,318 B, shared_vocabulary.json}`, and a `stanza/` folder (unused; it is the network hazard that
makes the stock `argostranslate` library unusable offline, see [ADR-0006](../decisions/ADR-0006-direct-argos-model-engine.md)). There is
no `bpe.model`. The `model.bin`, `sentencepiece.model`, `shared_vocabulary.json` and `config.json` of zh_en and zt_en have identical
SHA-256 hashes: one OPUS-MT zh-en model serves both scripts and the two packages differ only in the unused Stanza data.

Licence and distribution: CC-BY 4.0 requires attribution if redistributed. GlyphLens does not redistribute the models; the user imports a
file they obtained. The package index has no checksum or signature, so the import is a trust decision (the worker records SHA-256).

## 3. Translation results (52-item set; `eval/zh-en/dataset.json`)

The set covers ordinary conversation, short chat, informal speech, slang, idioms, questions, sarcasm, Chinese mixed with English, numbers
and names, long paragraphs and interface text. **The Chinese texts and the English reference translations were written by the assistant;
no independent human reviewed them.** The scores below are mechanical; the reading of the outputs is in
[`translation-review.md`](../../eval/zh-en/results/translation-review.md).

| | Simplified (zh_en) | Traditional (zt_en) |
|---|---|---|
| items translated / rejected by the output guard | 51 / 1 | 50 / 2 |
| single request, median / p95 / max latency | 72 / 156 / 410 ms | 89 / 134 / 363 ms |
| 32 items in one batched request | 1.9 s | 1.9 s (batch output equals the 32 single outputs) |
| mean sentence BLEU / chrF (own simple implementation) | 35.5 / 49.9 | 33.5 / 46.1 |
| model load + first translation | 0.35 s | 0.39 s |
| process memory (working set) | 26 MB before, **132 MB** with zh loaded, 229 MB with both loaded | |
| network attempts (Python-level guard) | **0** | **0** |

chrF by category (Simplified): conversation 56, short chat 39, informal 34, slang 35, idiom 27, question 49, sarcasm 46, mixed 67,
numbers and names 63, long paragraph 59, interface text 77.

**Reading of the Simplified outputs** (assistant's judgement): 16 correct, 19 acceptable (gist right), 11 partial, 5 wrong,
1 hallucination rejected. Idioms, slang and sarcasm are weak; ordinary sentences, questions, numbers and mixed Chinese-English text are
mostly understandable. The hallucination ("Could not close temporary folder: %s" for a network-error sentence) led to the placeholder
guard in the worker; it detects only that family of fabrications. A contraction artefact ("Tomorrow ' s") of the Chinese models'
Moses-style output is repaired by a small clean-up.

Throughput through the worker engine: roughly 13 single-request translations per second; batching 32 lines is about 2x faster per line.

## 4. OCR results

### 4.1 Which engine

* Windows built-in OCR: `AvailableRecognizerLanguages` on this machine is `en-US` only. A Chinese OCR language needs
  `Add-WindowsCapability` (administrator rights, a download, a system change), which GlyphLens must not do. **Not usable here.**
* PaddleOCR PP-OCRv5 on PaddlePaddle's own CPU engine: ran (init 10 s), but the oneDNN path crashed on these models
  (`ConvertPirAttribute2RuntimeAttribute`), and the non-oneDNN path needed **5.4 s per 560x640 window**.
* The same official PP-OCRv5 weights as **ONNX files under ONNX Runtime** (no `paddlepaddle` wheel needed): 20 threads 2.9 s, 12 threads
  2.5 s, 8 threads 1.6 s, 4 threads **0.83 s**, 3 threads 0.95 s, 2 threads 1.2 s, 1 thread 2.2 s (more threads are slower on this hybrid
  CPU). Selected: ONNX Runtime, 4 threads.

### 4.2 Accuracy and speed of PP-OCRv5 mobile (10 real window captures, `eval/zh-en/ocr-scenes.json`)

Screens are real Windows windows drawn with ClearType text and captured with `PrintWindow` (physical pixels); the ground truth is the
text drawn. CER = Levenshtein distance of the whitespace-free, reading-ordered text / truth length.

| Scene | What it tests | CER | line recall | ms |
|---|---|---|---|---|
| chat-light | Simplified chat bubbles, 22 px, light | 2.4 % | 6/7 | 840 |
| chat-dark-small | small 15 px text on dark | 6.4 % | 5/6 | 743 |
| settings-ui | SimSun 13 px interface text | 0 % | 6/6 | 635 |
| mixed-latin | Chinese + English, numbers, usernames, URLs | 0.5 % | 8/8 | 1132 |
| traditional-chat | Traditional (JhengHei) chat | 0 % | 6/6 | 934 |
| document-paragraphs | multi-line paragraphs | 0.6 % | 2/2 | 1348 |
| gradient-background | white text over a gradient | 0 % | 4/4 | 410 |
| font-sizes | the same sentence at 12/14/18/24/36 px | 0 % | 5/5 | 406 |
| vertical-text | one vertical column | 0 % | 1/1 | 86 |
| serif-emoji | SimSun, quotes, full-width forms, emoji | 2.0 % | 3/3 | 341 |
| **overall (character-weighted)** | | **1.2 %** | | median **689 ms** / window, init 9 s |

The remaining errors are punctuation (full-width `？` read as `?`, a trailing `。` dropped on small text) and a missed emoji, not wrong
Chinese characters. The server-size PP-OCRv5 models were started but the run was stopped after more than 40 minutes without a result:
**not evaluated**. Dense full-screen text and non-synthetic applications (real chat clients, web pages, video subtitles) were NOT TESTED.
A display capture of the whole 2560x1600 desktop produced 210-250 recognised lines in the end-to-end test (time not recorded
separately).

## 5. End-to-end tests (real app, real capture, real OCR, no injected text)

`eval/zh-en/drive-chinese-e2e.ps1` drives `GlyphLens.exe` through UI Automation against a real top-most window that draws Chinese chat
bubbles: Windows Graphics Capture -> local OCR (PaddleOCR through the Python worker) -> grouping -> language identification ->
translation -> rows in the app. 13 checks each; **13/13 passed for Simplified (`zh_en`) and 13/13 for Traditional (`zt_en`,
`traditional-chat` scene)**:

capture works - the local Chinese OCR is offered - the worker starts when translation is turned on - Chinese rows are recognised - with
no model installed the app says "No installed model translates Chinese to English. Import the translate-zh_en package" and shows **no**
translation - the model is imported through the real file dialog - the same on-screen text is translated to English only - copy
original and copy translation are separate - **no remote TCP connection and no UDP endpoint** owned by the app or the worker
(`Get-NetTCPConnection`) - Live shows the cached answers ("7 cached") - stopping the capture clears the rows - clean exit and the
worker process is gone.

Sample translations produced in these runs: "Are you free tonight? Let's eat together." / "I just got off work, I'm on the subway, about
20 minutes from now." / "Have you eaten yet?" (Simplified chat bubbles). Title-bar text of the test window ("GlyphLensChineseTarget",
the window icon, the close button) was recognised too and is correctly marked as "language could not be identified" / "too short"
instead of being translated.

Offline evidence: (1) the worker's fail-closed Python socket guard recorded **0 attempts** over import, install, load, 52 + 52 single
translations and the batches; (2) `Get-NetTCPConnection` showed no remote connection for the app process in both end-to-end
runs (**correction, 2026-11-21:** those two runs looked at the app and its direct child, the venv launcher, and missed the Python worker, which
is a grandchild; the scripts were fixed to cover every descendant process (3 processes), and the final runs, plus a probe built into the
app, found no remote connection for all of them); (3) the models were read from local files only. **Not done**: a packet capture, a firewall rule or running with the network
adapter disabled, and the Python guard cannot see sockets opened by native code (CTranslate2, ONNX Runtime). Models and the
PaddleOCR files were downloaded **once, explicitly, for this research** (Argos zh/zt, fr; PP-OCRv5 mobile and server ONNX); the app and
the worker never download anything.

## 6. Language identification

`worker/glyphlens_worker/engines/language_detection/script.py` (no library, no model): Han ideographs decide Chinese (>= 20 % of the
letters, so "Hello 你好" and "我用 iPhone 15 Pro" are Chinese, an English sentence with one stray ideograph is not); Simplified or
Traditional from characters that exist in only one of the GB2312 / Big5 character sets (Python's standard codecs; shared-only text such
as "你好" is reported as Simplified); kana means Japanese, Hangul Korean; several English function words and nothing else means English.
The result carries no confidence (a rule has no probability; none is invented). Anything else goes to Lingua (Python 3.12+) or py3langid
when installed, and is "language unknown" otherwise. A single CJK character is a complete word, so short Chinese messages ("好", "谢谢")
are identified.

## 7. Grouping

`ParagraphRegionGrouper` merges recognised lines of one bubble or paragraph (small vertical gap relative to the line height, similar
height, aligned edges) before translation and joins pieces of one row; Chinese lines are joined without spaces, Latin lines with one.
Each result keeps the member regions (identity, coordinates), the merged bounding box, the reading order and the OCR confidence
(the lowest reported). Separate chat bubbles stay separate (gap of about 1.5 line heights), side-by-side columns stay separate. The
unit tests cover these cases; the **end-to-end test did not contain a multi-line bubble** (each bubble was one line); the multi-line
paragraph scene is covered only by the OCR benchmark and the unit tests.

## 8. Resource use

CPU only. Translation: 132 MB per loaded model (two are cached), 0.35 s load. OCR: about 0.4-1.3 s per window (4 threads), 9 s first
load; the ONNX session adds roughly 300-700 MB of working set (measured 776 MB total for the benchmark process). The overlay and the
translation panel never run OCR themselves; Live uses the existing latest-frame-only loop, translations are cached (7 of 7 repeated
answers came from the cache in the end-to-end run), requests are batched, and a pass superseded by a newer one is cancelled.
Not implemented: skipping OCR of regions that did not change between frames (recognition always processes the latest frame), and GPU
acceleration (not evaluated; the CUDA device on this machine was deliberately not used).

## 9. Limitations and what was not done

* Argos quality is gist-level with failure cases (section 3). No alternative offline model was compared; the next step would be one
  CTranslate2-compatible alternative evaluated on the same 52 items (candidates need a licence check; NLLB-200 is CC-BY-NC).
* The reference translations and the reading of the outputs are the assistant's; no independent human review.
* The OCR test windows are real Windows windows but not real applications (WeChat, browsers); no photos or video frames; the PP-OCRv5
  server models were not completed; vertical text was one column only.
* Nine Argos packages with BPE tokenizers (cs, da, es, fi, hu, id, lt, ro, uk) are refused at import.
* Multi-monitor placement of the overlay cannot be tested on this one-monitor machine (logic is unit-tested only).
