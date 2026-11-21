# Chinese-to-English evaluation and end-to-end tests

Everything here is reproducible on a Windows machine. Nothing in this folder downloads anything; model files and screenshots stay
**outside the repository** (they are large and not ours to commit).

| File | Purpose |
|---|---|
| `dataset.json` | 52 Chinese sentences (Traditional variants for most) with English reference translations. Written by the assistant that built GlyphLens, **not** reviewed by an independent human. |
| `ocr-scenes.json` + `render-scenes.ps1` | Ten Chinese "application" windows (chat bubbles, small dark text, interface text, mixed Latin, Traditional, paragraphs, gradient, font sizes, vertical text, punctuation) drawn as real Windows windows and captured with `PrintWindow` into PNG files plus `truth.json`. |
| `run_translation_eval.py` | Runs the worker's real translation engine on the dataset; writes `results/translation-eval.{json,md}`. |
| `run_ocr_eval.py` | Runs PaddleOCR PP-OCRv5 on the screenshots and reports CER, line recall and latency. |
| `drive-chinese-e2e.ps1` | The acceptance test: drives the real `GlyphLens.exe` (capture, local OCR, worker, model import, translation, copy, no remote connections, Live cache, clean exit). |
| `drive-overlay-e2e.ps1` + `ordinary-app.ps1` | Verifies the floating overlay: above an ordinary window, drag, resize, hide/show and shortcuts, pass-through, unlock handle, translations arrive, responsiveness, capture exclusion (with a control run), position memory, main-window lifecycle. |
| `drive-ocr-autoselect-e2e.ps1` | The local Chinese OCR is chosen by the app itself when Chinese is the language (`-Scenario Ready`), or the missing OCR models are named with a working "Open OCR folder" (`-Scenario MissingOcr`); the Overview checklist is checked too. |
| `drive-incremental-e2e.ps1`, `measure-resources.ps1`, `run_crop_timing.py` | Incremental OCR in Live mode (run with and without `-FullOcr`), start-up/memory/CPU, and the cost of OCR on crops. |
| `drive-keyboard-e2e.ps1` + `ordinary-app.ps1` | Accessible names; keyboard-only use of every page (Tab order, no trap, visible focus, arrow keys, Space); the five global shortcuts pressed while another application is in front, with their real effect and no key leaking into that application; the overlay's "new results" button and scroll-following; the minimum window sizes, tested by what is really under the pointer at each control's edges. `-ShotDir <folder outside the repo>` also writes screenshots at the minimum size. |
| `shoot-ui.ps1` | Real screenshots (PrintWindow / screen) of every main-window page and the overlay at this machine's DPI scale. Keep the PNG files outside the repository. |
| `ui-pages.ps1` | Shared helpers: select the page that holds an element, choose the test window safely (never any other window), bring GlyphLens to the foreground before keystrokes, list every descendant process of the app. |
| `results/` | Generated numbers and `translation-review.md` (the reading of the outputs). |

## Commands

```powershell
# 1. Models (not in the repository): the user obtains the Argos packages themselves, for example
#    translate-zh_en-1_9.argosmodel and translate-zt_en-1_9.argosmodel from https://argos-net.com/v1/ (CC-BY 4.0),
#    and the two official PP-OCRv5 ONNX model folders (PP-OCRv5_mobile_det_onnx, PP-OCRv5_mobile_rec_onnx).

# 2. Python environment of the worker (found automatically by the app as worker\.venv)
py -3.12 -m venv worker\.venv
worker\.venv\Scripts\python.exe -m pip install -e "worker[translation,ocr]"

# 3. Translation benchmark
worker\.venv\Scripts\python.exe eval\zh-en\run_translation_eval.py --zh <zh_en.argosmodel> --zt <zt_en.argosmodel>

# 4. Screenshots and OCR benchmark
powershell -File eval\zh-en\render-scenes.ps1 -Mode Capture -OutDir <screenshots>
worker\.venv\Scripts\python.exe eval\zh-en\run_ocr_eval.py --shots <screenshots> --models mobile

# 5. End-to-end tests (isolated models folder containing ocr\PP-OCRv5_*_onnx)
dotnet build GlyphLens.sln -c Release
powershell -File eval\zh-en\drive-chinese-e2e.ps1 -Exe src\GlyphLens.App\bin\Release\net10.0-windows10.0.19041.0\GlyphLens.exe -ModelsDir <models> -ModelFile <zh_en.argosmodel>
powershell -File eval\zh-en\drive-overlay-e2e.ps1 -Exe src\GlyphLens.App\bin\Release\net10.0-windows10.0.19041.0\GlyphLens.exe -ModelsDir <models-with-zh_en-installed>
powershell -File eval\zh-en\drive-keyboard-e2e.ps1 -Exe src\GlyphLens.App\bin\Release\net10.0-windows10.0.19041.0\GlyphLens.exe -ModelsDir <models-with-zh_en-installed> -ShotDir <folder-outside-the-repo>
```

The end-to-end scripts use the real mouse and keyboard and global shortcuts: do not touch the mouse or keyboard while they run.
See [docs/research/CHINESE-FIRST-EVALUATION.md](../../docs/research/CHINESE-FIRST-EVALUATION.md) for the results and their limits.
