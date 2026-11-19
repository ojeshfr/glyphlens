# Python engine compatibility spike (PaddleOCR, Argos Translate)

Date: 2026-11-19. Host: Microsoft Windows 11 Home Single Language 10.0.26200, x64, CPU only (no CUDA used).
Python 3.12.10 (python.org install) and Python 3.11.18 (uv-managed `cpython-3.11-windows-x86_64`), both 64-bit.
Disposable virtual environments outside the repository; the production worker environment was not touched.
**No model weights were downloaded** and no OCR or translation was performed; only install, import and CPU runtime initialisation were tested.
Raw logs: `_spike2/results2` and `_spike2/results3` (not committed).

## Method

Per Python version, three fresh venvs: `ocr` (`paddlepaddle paddleocr`), `mt` (`argostranslate`), `all` (all three). Each: `pip install`, `pip check`, `pip freeze`, import tests.
Import tests: `paddle.utils.run_check()` (CPU), `import paddleocr`, `ctranslate2.get_supported_compute_types("cpu")`, `import argostranslate.package/translate`.
No version pins were given, so these are the latest releases PyPI resolved on the test date.

## Results

| Environment | Install | `pip check` | Import / runtime init | Size |
| --- | --- | --- | --- | --- |
| 3.12 ocr | PASS, 192 s | PASS | PASS (`run_check` OK, 12 s) | 0.82 GB |
| 3.11 ocr | PASS, 293 s | PASS | PASS (`run_check` OK, 12 s) | 0.83 GB |
| 3.12 mt (argos only) | PASS, 196 s | PASS | PASS (ctranslate2 CPU types `float32, int16, int8, int8_float32`; 0 language packages installed) | 0.96 GB |
| 3.11 mt (argos only) | PASS, 186 s | PASS | PASS (same) | 0.97 GB |
| 3.12 all | PASS, 467 s | PASS | **ctranslate2-first: PASS; paddle-first: FAIL** | 1.68 GB |
| 3.11 all | PASS, 567 s | PASS | **ctranslate2-first: PASS; paddle-first: FAIL** | 1.70 GB |

Invalid earlier result: the first `mt` runs failed ("requirement r") because of a bug in the spike script (a one-element array splatted as characters). They were rerun correctly (rows above); the failure says nothing about Argos.

### Known issue: native DLL conflict when Paddle is imported first

In the combined environment on both Python versions, `import paddle, ctranslate2, argostranslate.translate` fails:
`ctranslate2/__init__.py line 29, ctypes.CDLL(library)` -> `OSError: [WinError 127] The specified procedure could not be found`.
The reverse order (`import ctranslate2, argostranslate.translate, paddle`, then `paddle.utils.run_check()`) succeeds on both versions.
Cause not investigated; it is consistent with a conflict between native runtime DLLs loaded by Paddle and those CTranslate2 expects, but that is a hypothesis, not a finding.
Not tested: running real OCR and translation in one process (no models), thread-pool or memory interaction, or other import orders involving `torch`/`onnxruntime`.

## Resolved versions (identical on 3.11 and 3.12)

paddlepaddle 3.3.1, paddleocr 3.7.0, paddlex 3.7.2, numpy 2.3.5, opencv-contrib-python 4.10.0.84, protobuf 7.36.2 (ocr env).
Adding Argos: argostranslate 1.11.0, ctranslate2 4.9.0, onnxruntime 1.32.0, sentencepiece 0.2.3, spacy 3.8.18, stanza 1.10.1, torch 2.15.0 (installed by Argos' dependencies; `pip freeze` of the `all` envs).
Wheels: ctranslate2, sentencepiece and onnxruntime resolved to `cp312-cp312-win_amd64` wheels (3.12 log); no source builds occurred. cp311 wheels were likewise installed without errors on 3.11 (install exit 0); individual wheel filenames were not extracted for 3.11.
Argos pulls a large dependency tree (torch, spacy, stanza), which is why the combined environment is about 1.7 GB.

## Conclusions

- Both Python 3.11 and 3.12 install and import both stacks on Windows x64, CPU only. Neither version is distinguished by this spike.
- A single environment is **installable and conflict-free under `pip check`**, but a single *process* is only viable if `ctranslate2` is imported before `paddle`. That is fragile (any future code that imports Paddle first breaks).
- Recommendation: **Python 3.11** for production. It is the version the project already targets (CI baseline, `requires-python >= 3.11`), passed every test the same as 3.12, and has a longer-supported ecosystem wheel history; 3.12 is an acceptable alternative. This preference is a judgement, not a measured difference.
- Recommendation for engine layout: keep one worker environment, but either enforce import order (import CTranslate2/Argos first, covered by a startup test) or isolate OCR and translation in separate worker processes if runtime testing with real models shows further conflicts. The runtime test with models is NOT TESTED and must precede the choice.
- Before locking dependencies: pin exact versions in a lock file (these are latest-at-test-date), re-run this spike on a clean machine, and test model loading offline.
