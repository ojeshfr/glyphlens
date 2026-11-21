# Incremental OCR - measurements (2026-11-20)

Machine: Windows 11 build 26200, one 2560x1600 display at 150 % scale, CPU-only (PP-OCRv5 mobile on ONNX Runtime, 4 threads).
Scene `chat-progressive` (a 560x1000 Chinese chat window; 4 messages at first, one more every 7 s). Live reading on, local Chinese OCR
selected by the app, translation on. Script: `eval\zh-en\drive-incremental-e2e.ps1` (run once normally, once with `-FullOcr`).
One run each, one machine: these are measurements, not a benchmark.

## Cost of reading a crop instead of the frame (`run_crop_timing.py`, worker recogniser, median of 5 after warm-up)

| Image | Full frame | Bottom third | One-line band |
|---|---|---|---|
| chat-light (560x640) | 1387 ms | 436 ms | 248 ms |
| chat-dark-small (520x420) | 1194 ms | 267 ms | 163 ms |
| document-paragraphs (760x420) | 2111 ms | 140 ms | 70 ms |
| settings-ui (460x360) | 1027 ms | 208 ms | 156 ms |

(The document and settings crops found fewer or no lines in the cut band; the timing shows the cost of the pixels, not equal output.)

## Live run: pass duration (ms shown in the OCR status line)

| Mode | Passes | Duration of each pass | Median |
|---|---|---|---|
| incremental (default) | 8 | 6 (unchanged), 127, 154, 65, 175, 868 (a full read), 168, 129 | 142 ms |
| full frame every time (`GLYPHLENS_FULL_OCR=1`) | 8 | 650, 647, 610, 673, 792, 804, 871, 1027 | 733 ms |

Partial passes re-read 6-16 % of the frame. All 11 messages were read and translated in both runs.

## Differences found (reported, not hidden)

- The last message was recognised slightly differently by the crop than by the full frame: a full-width question mark instead of an
  ASCII one, and an emoji that the full-frame read returned as its own line was not returned by the crop (15 vs 16 lines). A recogniser
  sees a different image when it reads a crop, so results are equivalent in layout but not guaranteed byte-identical. Every 12 partial
  passes or 30 s the whole frame is read again, so such a difference cannot persist.
- **Correction of an earlier version of this file.** The first CPU comparison (22.7 s vs 22.9 s) measured the app and the worker's
  *launcher* process only, not the Python worker itself (the worker is a grandchild of the app), so it said nothing about recognition
  cost. It was repeated with every descendant process included (second run, same scene and settings, one run each):

  | Mode | App CPU | Worker CPU | Total over the 61 s Live run |
  |---|---|---|---|
  | incremental (default) | 28.7 s | 33.6 s | 62.3 s |
  | full frame every time | 29.7 s | 53.8 s | 83.5 s |

  The worker used about 38 % less CPU with incremental reading; the app's own CPU is the same (and is inflated here by the test script
  polling the app's UI every 200 ms; with nobody polling, the app used 1.9 s of CPU in 20 s while capturing, see `measure-resources.ps1`).
