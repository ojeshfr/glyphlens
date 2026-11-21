# Start-up, memory and CPU - measurements (2026-11-21)

Machine: Microsoft Windows NT 10.0.26200.0, 12th Gen Intel(R) Core(TM) i7-12700H, 16 GB RAM. One run on one machine with `eval\zh-en\measure-resources.ps1` (the app, the worker and the Chinese test window; PP-OCRv5
mobile on ONNX Runtime, CPU only; the zh model is installed but was not used here). These are measurements, not a benchmark.
The worker is started through the venv launcher, so the figures cover **every descendant process of GlyphLens.exe** (3 processes).

| Quantity | Result |
|---|---|
| App memory (working set), window open, nothing captured | 157 MB |
| Time from "translation on" until the worker reports Ready | 1.5 s |
| Worker memory right after Ready (engines not loaded yet) | 45 MB |
| First recognition (cold: the OCR models are loaded) | 7 lines . 7132 ms . 3 title-bar items set aside |
| Second recognition of the same still screen | 7 lines . 8 ms . 3 title-bar items set aside . screen unchanged |
| App memory after the first read | 218 MB |
| Worker memory after the first read (OCR models loaded) | 478 MB |
| CPU while capturing and idle, 20 s | app 1.86 s, worker 0 s |
| CPU during Live reading of a still screen, 20 s | app 1.2 s, worker 0 s (status: 7 lines . 0 ms . 3 title-bar items set aside . screen unchanged) |

Notes: the second read is answered from the incremental engine ("screen unchanged"), not by recognising again. Translation was not
exercised in this run, so the translation engine's own load time and memory are not included (a first translation loads the
CTranslate2 model, about 75 MB on disk). The app's idle CPU is mostly the capture preview and the metrics timer. Per-pass OCR and
translation latencies are in `incremental-ocr.md`, `ocr-eval.json` and `translation-eval.json`.
