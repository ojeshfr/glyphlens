# Implementation status

Durable checkpoint for the current milestone, updated after every substantial step so work can resume from the exact
unfinished step. On resume: inspect `git status` and `git log` first, then read this file.

## Current milestone

**Chinese-first offline screen translation** (primary: Simplified Chinese `zh` to English; secondary: Traditional `zt`):
real screen capture, local Chinese OCR, grouping, script-based language identification, offline translation with Argos model
packages, shown in the WPF app. After the Chinese path is verified, the **overlay-first UI** (floating translation window).

## Last verified state (all uncommitted; nothing has been committed or pushed in this milestone)

Every row below was **re-run on 2026-11-21 on the final code in the working tree** (commit `85653e8` plus the uncommitted changes; after the
runs only documentation changed). Machine: Windows 11 build 26200, one 2560x1600 display at 150 % scale. Earlier numbers are not carried over.

| Area | Result | How it was run |
|---|---|---|
| Last pushed commit | `85653e8` (Windows OCR milestone), `origin/main` identical (0 ahead / 0 behind after `git fetch` on 2026-11-20) | `git fetch origin`, `git rev-list --left-right --count` |
| `dotnet format --verify-no-changes` | PASS (exit 0, no output) | `dotnet format GlyphLens.sln --verify-no-changes --no-restore` |
| Release build | PASS (`Build succeeded`; warnings are errors in this solution) | `dotnet build GlyphLens.sln -c Release` |
| .NET tests | PASS: Core 455/455, Infrastructure 491/491, App 269/269, Windows 148 passed + 7 skipped (hardware tests, skipped by design) of 155 | `dotnet test tests\GlyphLens.<Project>.Tests -c Release --no-build` |
| Python worker, `worker\.venv` (Python 3.12.10, engines installed) with the real fr model, real PP-OCRv5 files and a real screenshot | PASS: 395 run, OK, 6 skipped (Lingua and py3langid are not installed there) | `python -m unittest discover -s tests -t .` with the three `GLYPHLENS_TEST_*` variables set |
| Python worker, CI mode on a real **Python 3.11.18** (uv-managed CPython, standard library only, env vars unset) | PASS: 395 run, OK, 26 skipped (real-engine tests, each with a visible reason) | the exact CI command `python -m unittest discover -s tests -t . -v` |
| Python worker, system Python 3.12.10, standard library only | PASS: 395 run, OK, 26 skipped | same command |
| Real end-to-end, Simplified (scene `chat-light`, fresh OCR-only models folder, model imported through the file dialog) | PASS: 13/13 checks, including no remote connection from the app **and its worker processes** (3 processes) | `eval\zh-en\drive-chinese-e2e.ps1` |
| Real end-to-end, Traditional (scene `traditional-chat`, zt model) | PASS: 13/13 checks | same script, `-SceneName traditional-chat` |
| Real end-to-end, multi-line Chinese messages (scene `chat-multiline`: bubbles wrapped over 3, 2, 1 and 2 lines) | PASS: 14/14 checks, including `multiline-grouped` (4 messages translated as exactly 4 groups from 11 OCR lines) | same script, `-SceneName chat-multiline -ExpectedMessages 4` |
| Real end-to-end, automatic choice of the local Chinese OCR (no engine picked by hand) | PASS: 12/12 (Ready: selected by itself, read and translated, explicit English-only choice kept and explained, Overview checklist agrees incl. the offline check) and 12/12 (MissingOcr: nothing switched, missing models named, "Open OCR folder" opens the created folder, no Chinese read silently) | `eval\zh-en\drive-ocr-autoselect-e2e.ps1 -Scenario Ready|MissingOcr` |
| Real overlay verification | PASS: 35/35 checks (above an ordinary app, no focus stealing, drag, resize, hide/show, pass-through, unlock handle, translations arrive, responsive, capture exclusion, position memory, lifecycle) | `eval\zh-en\drive-overlay-e2e.ps1` |
| Overlay control run (capture exclusion deliberately disabled) | EXPECTED RESULT: the exclusion check fails and the display capture reads the overlay's own labels (4), so the check can fail; with it enabled 0 were read | same script, `-ControlWithoutProtection` |
| Incremental OCR in Live mode | PASS (7/7 checks both modes); median pass 142 ms incremental vs 733 ms full; worker CPU 33.6 s vs 53.8 s over 61 s; see `eval\zh-en\results\incremental-ocr.md` | `eval\zh-en\drive-incremental-e2e.ps1` with and without `-FullOcr` |
| Start-up, memory, idle CPU | MEASURED (one run): worker ready 1.5 s after "translation on"; first OCR read 7.1 s (cold), worker 45 MB -> 478 MB after the OCR models load; idle app CPU 1.9 s per 20 s, worker 0; see `eval\zh-en\results\resources.md` | `eval\zh-en\measure-resources.ps1` |
| Screenshots of every page and the overlay | TAKEN on this machine at 150 % scale (`eval\zh-en\shoot-ui.ps1`; PNG files are kept outside the repository) | other DPI scales NOT TESTED |
| Hygiene | see the final report; `git diff --check` is re-run at the end of every session | |

Observations from these runs (not failures): the UI Automation list exposes only about five result rows at a time (virtualisation), so the
scripts read the complete result from the "copy translation" button rather than from the list. A page of the main window that is not shown is
not in the UI Automation tree, so the scripts select the page that holds an element before looking it up (`eval\zh-en\ui-pages.ps1`).

## What was built in this milestone

- **Worker** (`worker/glyphlens_worker`): `language.detect` (script rules for zh/zt/ja/ko + English, Lingua or py3langid optional),
  `translation.translate` (CTranslate2 + SentencePiece on Argos packages, no `argostranslate`), `models.list/status/install`
  (validating importer), `ocr.detect` (PP-OCRv5 mobile on ONNX Runtime, local model files), fail-closed network guard,
  hallucination guard (placeholders), contraction cleanup, Chinese sentence splitting. ADR-0006.
- **.NET**: contracts, `TranslationPipeline` (+ grouping of OCR lines into paragraphs/bubbles), `LruTranslationCache`,
  `WorkerLanguageDetector/TranslationEngine/ModelCatalog`, `WorkerOcrEngine`, `PngFrameEncoder`, `OcrEngineSelector`,
  `TranslationViewModel`, translation + models panels, OCR engine entry "Chinese + English (local OCR worker)".
- **Evaluation** (`eval/zh-en`): dataset (52 items), translation benchmark, OCR benchmark, screenshot renderer, end-to-end driver.

## Evidence (details in `docs/research/CHINESE-FIRST-EVALUATION.md`)

- Argos `zh->en` and `zt->en` packages are SentencePiece (not BPE), CC-BY 4.0, 74.5 MB each; their `model.bin`, tokenizer and
  vocabulary are byte-identical (one OPUS-MT model serves both scripts).
- PP-OCRv5 mobile (ONNX Runtime, 4 threads): overall CER 1.2 % on 10 real window captures, 0.4-1.3 s per window on CPU.
- Translation: gist-level; idioms, slang and some sentences are wrong; a UI sentence produced a hallucinated string
  (now rejected by the guard).

## Plan and status

| # | Step | Status |
|---|---|---|
| 1-5 | Baseline, contracts, research, Python worker, .NET clients | DONE |
| 6 | Translation pipeline + grouping | DONE |
| 7 | WPF translation + models panels, OCR engine selector | DONE (main-window UI; to be superseded by the overlay as the primary view) |
| 8 | Chinese evidence: translation benchmark, OCR benchmark, real end-to-end (zh, zt) | DONE |
| 9 | Overlay-first UI: floating WPF overlay window, pass-through mode, self-capture exclusion, multi-monitor placement logic, hotkeys | DONE (first version; verified on one monitor) |
| 10 | Docs, final verification | DONE; **commit and push deliberately NOT done: waiting for the user's review** |

## Design conformance

[DESIGN.md](../DESIGN.md) is the design target; this file reports what exists. Status words: IMPLEMENTED (code exists), TESTED
(automated tests), VERIFIED (observed on real Windows in the scripts above), NOT IMPLEMENTED.

| Design item | Status |
|---|---|
| Apple-inspired look (2026-11-21, checkpoint 7): Apple palette tokens, grey sidebar with icons, capsule buttons, switches, segmented control, Apple-style slider, status badges with symbols, overlay bubbles | IMPLEMENTED, TESTED (tokens, contrast, icons, card details), checked in offscreen renders of every page and the overlay at default and minimum size; NOT VERIFIED in the running app on screen |
| Overlay is a separate native window, 400x560 default, min width 320, header drag, collapse, pin, hide | IMPLEMENTED, TESTED (placement/view-model tests), VERIFIED (overlay script, drag/resize/hide/show) |
| Interactive and pass-through modes with a discoverable unlock handle | IMPLEMENTED, VERIFIED (overlay script) |
| Self-capture prevention (`WDA_EXCLUDEFROMCAPTURE`) | IMPLEMENTED, VERIFIED on Windows 11 build 26200 with a control run; NOT TESTED on other builds; no fallback for builds without the flag |
| Cards with original, translation, copy buttons; confidence only when real | IMPLEMENTED, TESTED, VERIFIED (screenshot) |
| Truthful overlay states (Off, Paused, Starting, Worker unavailable, Capture stopped, Model missing, Error, Uncertain, No text, Ready, Live) with a coloured indicator and words | IMPLEMENTED, TESTED (evaluator, 20 cases), VERIFIED for Ready on a screenshot; the other states are covered by unit tests only |
| Header indicator in the source script (中文 / 繁體中文 / Auto) | IMPLEMENTED, TESTED; "Auto" is shown when the source is "detect automatically" |
| Hotkeys Ctrl+Alt+T/O/P/I/C with conflict reporting | IMPLEMENTED, TESTED (service and view models), VERIFIED end to end for all five with real keystrokes while another application was in front (`drive-keyboard-e2e.ps1`, checkpoint 6): T on/off, O shows, P pauses (new results ignored) and resumes (they arrive), I pass-through and back, C clears; none of the keys reached the other application. Verified on the build before the checkpoint-6 layout fixes |
| Opacity and text size settings (persisted with the layout) | IMPLEMENTED, TESTED; opacity VERIFIED in the overlay script, text size on a screenshot only |
| Preserve scroll position + "new results" pill | IMPLEMENTED, VERIFIED on this machine (`drive-keyboard-e2e.ps1`, checkpoint 6): scrolled up, a new message keeps the reading position (same first card, 0 %) and shows the pill; the pill scrolls to 100 % and disappears; at the bottom, new cards are followed. Logic is in code-behind (no unit test) |
| Clear with confirmation | IMPLEMENTED, TESTED (first request asks, the second clears, it expires after 5 s) |
| Semantic colour tokens in one ResourceDictionary, light theme, no literal colours elsewhere | IMPLEMENTED, TESTED (token values, no-literal-colour scan of XAML and code, WCAG contrast of the text pairs), VERIFIED on screenshots; the muted token is below AA on white by specification and is only used for non-essential information |
| Main window: Overview / Capture / Models / Settings pages with a conventional title bar (History omitted: nothing is persisted) | IMPLEMENTED, TESTED (navigation), VERIFIED (all E2E scripts drive it, screenshots of every page); default size 1000x700, minimum 820x580 (enforced: VERIFIED). At the minimum size the Capture page was found clipped on screen (checkpoint 6) and fixed in XAML; the fix is IMPLEMENTED BUT UNVERIFIED on screen |
| Readiness checklist (Python, worker, Chinese OCR, zh and zt models, offline check, capture) with real actions only | IMPLEMENTED, TESTED (19 cases), VERIFIED (Ready and MissingOcr scenarios) |
| Measured frame rate in the Capture page | IMPLEMENTED, TESTED (meter and view model), VERIFIED on a screenshot ("0.0 frames/s" on a still window: Windows delivers frames only on change) |
| Settings: overlay opacity, text size, pin, shortcuts text, privacy text, version | IMPLEMENTED; only settings backed by real behaviour are shown. NOT in Settings: hotkey rebinding, target language other than English, capture behaviour options (do not exist) |
| Accessibility: accessible names on icon buttons and list items, WCAG contrast test | IMPLEMENTED, TESTED (contrast); keyboard-only operation VERIFIED on every page (checkpoint 6: Tab reaches every enabled control, comes back to the start, every stop is named and visibly changes with focus, arrow keys switch pages, Space starts a capture); accessible names of every control VERIFIED by a UI Automation audit. Behaviour with a real screen reader (Narrator, NVDA) is NOT TESTED; the scroll-bar and slider track parts of the Fluent theme have no names (not focusable) |
| DPI 100/125/200 %, mixed DPI, multi-monitor, Windows 10 | NOT TESTED (only 150 % on one monitor is available) |

## Priority changes received (both preserved)

1. **Chinese first** (zh P0, zt P1, English fixed target; no 49-language prerequisite; grouping; real E2E replaces the French test;
   reproducible dataset; evaluation without invented scores; do not commit until reviewed).
2. **Overlay-first UI** (floating WPF translation window is the primary experience; main window is configuration; self-capture
   prevention; two input modes; multi-monitor; hotkeys only when tested; Larpiano is read-only reference at
   `D:\projects\larpiano\larpiano-overlay-prototype`). Verify before claiming.

## Known limitations / not done

- Nine Argos packages with BPE tokenizers (cs, da, es, fi, hu, id, lt, ro, uk) are refused at import.
- Only one OCR model family evaluated end to end (PP-OCRv5 mobile); the server-model run did not finish and is not reported.
- Overlay: the self-capture fallback for Windows builds without `WDA_EXCLUDEFROMCAPTURE` (region masking) is not implemented;
  multi-monitor, mixed DPI and Windows 10 are NOT TESTED; the real-mouse verification script can be disturbed by a person using the mouse.
- OCR of unchanged regions is not skipped between frames (the latest frame is always recognised; translations are cached).
- The app's main window still contains the translation panel (kept as diagnostics); the overlay is the primary view.
- No alternative translation model was evaluated; Argos quality is gist-level with documented failure cases.
- The 52-item translation set and its reference translations were written by the assistant, not by an independent human reviewer.
- Real application screenshots (WeChat etc.) were not available; the test windows are real Windows windows drawn by a test app.
- Subagents hit the weekly usage limit (resets 2026-11-25); the lead did the remaining work alone.

## Exact next action

1. The user reviews the uncommitted changes (`git status`, `git diff`; evidence in `docs/research/CHINESE-FIRST-EVALUATION.md`).
2. After approval: conventional commit (with the Co-Authored-By line), normal fast-forward push, verify the remote commit and the
   GitHub Actions run (CI runs the model-independent tests only).
3. Then the next items: an alternative-model comparison on `eval/zh-en/dataset.json`, skipping OCR of unchanged regions, multi-monitor and
   Windows 10 verification of the overlay, BPE packages, UI Automation text.

## Checkpoints (autonomous session, 2026-11-20)

Newest last. Each entry: task, files, tests executed, result, what remains.

### 1. Stabilization re-verification and DESIGN.md - DONE
- Added `DESIGN.md` (design intent, kept separate from this file). Re-ran every check in "Last verified state" on the working tree.
- Added the `chat-multiline` scene and the `-ExpectedMessages` check to `eval/zh-en` (wrapped chat messages are one group each).

### 2. Chinese OCR selection and readiness - DONE (verified on this machine)
- New: `Core/Ocr/LocalOcrAdvisor.cs` (pure policy), `Core/Ocr/ModelsDirectory.cs`, `App/ViewModels/OcrEngineAdvisor.cs`; changed `OcrViewModel`
  (engine notice, automatic selection that does not count as the user's choice, "Open OCR folder"), `TranslationViewModel`
  (`InstalledSources`, `SetEngineNotice`), `App.xaml.cs`, `Views/OcrView.xaml`. Language option records now expose their label as the
  automation name (a combo item used to be announced as `OcrLanguageOption { Tag = ... }`).
- Behaviour: when Chinese is the language (chosen as source, or "detect automatically" while a Chinese model is installed) and the worker
  reports the local OCR ready, it is selected unless the user picked an engine; if it is not ready the notice says what is missing (models,
  runtime) and offers the folder; an explicit English-only choice is kept and explained. Nothing is selected before the worker has reported.
- Tests: Core 378 (+38), App 187 (+12), both pass. Real runs: `eval\zh-en\drive-ocr-autoselect-e2e.ps1` Scenario Ready 11/11 and
  Scenario MissingOcr 11/11 (no Chinese read silently; Explorer opened the created folder).
- Remaining: Windows 10 and machines whose Windows OCR can read Chinese are NOT TESTED (logic covered by unit tests only).

### 3. Title-bar filtering and incremental OCR - DONE (verified on this machine)
- New: `Core/Ocr/OcrChromeFilter.cs`, `FrameDiff.cs`, `DirtyRegionPlanner.cs`, `PixelRect.cs`, `IncrementalOcrEngine.cs`; changed `OcrViewModel`
  (filter on publish, "title-bar items set aside", "updated N % of the frame" / "screen unchanged"), `App.xaml.cs` (wrapper; `GLYPHLENS_FULL_OCR=1` turns it off).
- Behaviour: window-button glyphs in the top-right of the title strip and the window's own title are set aside (and counted in the status);
  a live feed reuses an unchanged result, re-reads only the changed area widened to whole text lines, and falls back to a full read for the
  first pass, a different target/size/engine, scrolling or other large changes, text touching the edge of a changed area, every 12 partial passes and every 30 s.
- Tests: Core 442 (340 at the start of the session), App 192 (175), all pass; includes a randomised equivalence test (partial result == full read over 40 edits x 8 seeds) with a synthetic recogniser.
- Real runs: `drive-incremental-e2e.ps1` incremental vs `-FullOcr`: median pass 142 ms vs 733 ms, all 11 messages read in both; details and the
  differences found (a punctuation variant and one emoji line) in `eval\zh-en\results\incremental-ocr.md`. Worker CPU over the 61 s Live run: 33.6 s incremental vs 53.8 s full (about 38 % less; an earlier equal-CPU result measured the wrong process and was corrected, see the results file).
- Chinese E2E re-run after the filter: 13/13 (status line "2 title-bar items set aside", no "need attention" rows left).
- Not done: scrolling detection by shifting (scroll always means a full read); region-level reuse across scrolls; Windows OCR path not measured.

### 4. Design system, overlay state, main-window pages, readiness, offline probe - DONE (verified on this machine)
- New/changed: `Themes/Tokens.xaml` (central tokens, light theme), `OverlayStatus.cs` (state evaluator), overlay font size / clear confirmation / new-results pill,
  `MainViewModel` + `MainWindow.xaml` (Overview, Capture, Models, Settings), `ReadinessViewModel` + `ReadinessView`, `Core/Abstractions/IProcessConnectionProbe.cs` +
  `Windows/Platform/WindowsConnectionProbe.cs` (which TCP/UDP endpoints GlyphLens' own processes own), `Core/Capture/FrameRateMeter.cs`.
- Tests: Core 455, Infrastructure 491, App 269, Windows 148 (+7 skipped), all pass; the probe tests use real sockets, including a connection to this PC's own
  LAN address that the probe must count as remote.
- Real runs: every E2E script re-run on the final build (see the table above). The E2E "no remote connections" check and the process-gone check originally looked
  only at the app's direct children (the venv launcher) and **missed the real worker, which is a grandchild**; they now cover every descendant process (3 here).
  Earlier statements that the E2E proved "no remote connections from the worker" were therefore based on the launcher process only for runs before 2026-11-21.
- A mistake caught on the way: a mis-quoted test command created a stray models folder (`_spike4$d`) next to the repository; it was verified (only a copy of a test model)
  and removed. The scripts now refuse to capture any window other than the test window and refuse to send keystrokes unless the GlyphLens window is in the foreground.
- Remaining: other DPI scales, the minimum window size on screen, keyboard-only and screen-reader checks, hotkeys T/P/C end to end, multi-monitor, Windows 10.

### 5. Alternative translation models - DESK RESEARCH ONLY (nothing evaluated)
- `docs/research/TRANSLATION-ALTERNATIVES.md`: six candidates compared from primary sources by a read-only research agent (the lead did not re-open every page).
  Licence screening: NLLB-200 is CC-BY-NC (not usable), HY-MT1.5 has territory and use restrictions (needs legal review), Qwen2.5-3B is not Apache.
  Apache-2.0 candidates worth testing first: Hy-MT2-1.8B (GGUF, runtime caveat unverified), Qwen3-1.7B/4B, MADLAD-400 3B.
- **Not done:** no candidate was run on the 52-sentence set, no latency or memory was measured, and Argos remains the production model. All judgements are provisional.

### 6. Keyboard, shortcuts, "new results" and minimum size (2026-11-21) - VERIFIED, one fix UNVERIFIED
- New script `eval\zh-en\drive-keyboard-e2e.ps1` (real keystrokes and UI Automation; refuses to send a key unless the expected window is in front, stops if focus
  leaves GlyphLens, backs up and restores `overlay.json`). Results on this machine, on the build before the layout fixes below:
  - **Accessible names:** every control on the four pages and in the overlay has a usable name (PASS) after two fixes: the readiness and models lists were
    focusable, unnamed Tab stops; they are now not focusable and named, and the OCR lines, translation results and overlay card lists got names.
  - **Keyboard only:** on every page Tab reaches every enabled control, returns to the start (no trap), every stop is named and visibly changes when focused
    (pixels compared before/after); Down in the navigation list switches pages; Space on "Start capture" starts the capture (all PASS).
  - **Shortcuts from another application** (all PASS): see the hotkey row above.
  - **New results / follow:** see the pill row above (PASS).
- **Minimum size - defects found and fixed, fix not yet verified on screen.** Screenshots at 820x580 showed: the Capture page's right column cut off by about
  33 DIP (the "Copy" button and labels clipped), "Screen capture" overlapping its status, the target list and Refresh pushed out of view; in the overlay at
  320 DIP the opacity slider ran under the mode button (its thumb was hidden), and the status and protection lines shared one cell (they would overlap when the
  status wraps). Fixes: the Capture columns share the width (`*` with minimums 240/300, the right one at most 420), the capture header wraps and the target
  list has its own line, long language names end in an ellipsis with a tooltip, the overlay slider shrinks to fit, the two status lines are stacked.
  The Overview, Models and Settings pages and the overlay header were not clipped.
- The first automated "on screen" check passed although the page was clipped (UI Automation reports the full rectangle of a clipped control); it was replaced by
  a hit test (what is really under the pointer at a control's edges), which caught the clipping on the old build. A refined version (tests text glyphs and each
  scroll area's visible height, removing false positives on full-width titles) has **not completed a run yet**: the run was stopped by the script's own guard
  when another application took the foreground.
- After the fixes: Release build OK; Core 455, Infrastructure 491, App 269, Windows 148 (+7 skipped) pass; format and `git diff --check` clean.
  **NOT TESTED on the fixed build:** the keyboard/shortcut/pill script and the minimum-size layout on screen (rerun `drive-keyboard-e2e.ps1 -ShotDir <folder outside the repo>`).

### 7. Apple-inspired restyle (2026-11-21) - IMPLEMENTED, checked offscreen only
- On the user's request the look changed from "native Windows utility" to Apple-inspired (DESIGN.md section 2 rewritten): Apple's light
  palette (accessible blue/green/orange/red where the bright system colours fail WCAG AA as text), Segoe UI Variable instead of SF Pro
  (licence), Segoe Fluent Icons in the sidebar, grey capsule and accent capsule buttons, switches for "Translate", "Live reading" and
  "Keep the overlay above other windows", a segmented Display/Window control, an Apple-style slider, readiness badges with symbols and
  hairline rows, and an overlay with grey bubbles (original small above, translation bold below; language, time and OCR confidence on
  one line). The Fluent theme's accent keys now use GlyphLens' accent, so the Windows accent colour (dark grey on this PC) no longer
  leaks into sliders and fields. The Capture page's left section fills the height when there is room and scrolls when the window is short.
- How it was checked: a scratch tool (outside the repository) renders the real views with the test fakes to PNG offscreen, without
  showing a window; every page and the overlay were reviewed at the default and the minimum size. A pre-existing defect it showed was
  fixed: the preview's "No capture running" text was dark on the dark preview.
- Tests: App 279 (new: contrast of the new tokens, one icon per page, card details), Core 455, Infrastructure 491, Windows 148 (+7 skipped);
  Release build, format and `git diff --check` clean.
- **NOT VERIFIED on screen:** the running app was not started (the PC was in use); hover/pressed states, the focus ring and the switch and
  segmented controls under real input, and all E2E scripts on this build. The E2E scripts address controls by AutomationId and pattern,
  which the restyle kept (the switches are still toggle buttons/check boxes, the segments still radio buttons), but that is unverified.

### 8. Dark mode, integrated updater, website (2026-11-21) - IMPLEMENTED, offscreen/unit checked only
- **Dark mode.** Colours moved into `Themes/Palette.Light.xaml` and `Palette.Dark.xaml` (same keys, enforced by a test); every view uses `DynamicResource`
  (a test forbids `StaticResource` brushes), so the appearance switches while the app runs. Settings > Appearance: System (follows Windows' app mode and
  switches with it), Light, Dark; remembered in `%LOCALAPPDATA%\GlyphLens\preferences.json`. New `ThemeService`, `AppearanceViewModel`, `AppPreferencesStore`.
  Contrast of every text/background pair is tested in both palettes (accent text has its own lighter variant in dark). Seen only in offscreen renders
  (all pages, overlay); not seen in the running app, and the Windows theme-change event is untested.
- **Updater** (the owner chose automatic background checks, which ends the old "no network requests" position; privacy text, SECURITY.md updated).
  Core: signed-feed reader (ECDSA P-256 over the manifest, HTTPS .exe/.msi only, size/SHA-256 checks) and schedule; Infrastructure: `HttpUpdateClient`
  (HTTPS, caps, redirect limit, SHA-256 verified or file deleted); App: `UpdateService` (first check ~30 s after start, then daily, retry after 6 h),
  Settings > Updates, Overview banner. Nothing downloads or installs without a click; the installer is re-hashed right before it runs.
  **Inert in this repository:** no feed URL or signing key is built in (`Updates/update-config.json`), so it contacts nothing until a release owner sets it up
  (`docs/UPDATES.md`, `tools/UpdateFeed.cs` makes the key and signs a feed; the tool was run once and produced a feed, but that feed was not fed back
  into the app). **NOT TESTED:** a real server, a real installer run, SmartScreen behaviour.
- **Website** in `website/` (static; `vercel.json` with a strict CSP): one page with a real-data hero (Argos outputs from the test set), how it works,
  features, light/dark screenshots, privacy facts with their limits, shortcuts, an honest "not released yet" status. Checked in a local browser at desktop and
  phone width (no horizontal overflow, light and dark). Later the same day it was also served locally **with the headers from `vercel.json` applied**
  (the first check used a server that sends none): the page renders, the screenshots load and no Content-Security-Policy violation was reported.
  The screenshots are now small AVIF files (`assets/*.avif`, 49 KB in total; the PNG masters stay next to them).
  **Vercel: BLOCKED.** The connector connected later, but both `create_project` and `create_deployment` were refused with
  `403 forbidden: You don't have permission to create a project` although the account is the owner of its only (hobby) team. The cause is not known.
  The Vercel CLI was not used with the locally stored login because the owner chose the connector. Nothing was created on Vercel.
  **Hosted on GitHub Pages instead (the owner's decision, 2026-11-21): https://ojeshfr.github.io/glyphlens/ - PASS for what was checked.** To do it, the owner
  chose to make the repository `ojeshfr/glyphlens` **public** (before that: a scan of the 4 commits and 203 tracked files found no secrets, personal paths,
  images or models; the commit author name and email are visible in the history; there is no licence file). Only the site files were pushed, as a new
  branch `gh-pages` (one commit, no app code); `main` and the uncommitted working tree were not touched. Paths in the page were made relative (the site lives
  under `/glyphlens/`) and the Content-Security-Policy was added as a `<meta>` tag because Pages sends no headers. Checked on the live URL: the Pages build
  succeeded, all six screenshots, the stylesheet and both scripts load (HTTP 200), no policy violation or console error, no horizontal overflow, theme
  switch works. **NOT TESTED** on the live URL: phone width and other browsers (Chrome-based browser pane only). The repository being public means the
  four existing commits and the dependabot branch are visible to everyone.
- Tests: Core 479, Infrastructure 497, App 345, Windows 148 (+7 skipped). Format and `git diff --check` clean.
  One intermittent failure seen once in the full run under load: `WorkerProcessManagerTests.AWorkerThatRanStably_DoesNotCountAgainstTheRestartBudget`
  (timing-based restart waits); it passed 4/4 alone and in the full-suite rerun. Root cause not investigated.

### Open items at the end of this session
- Rerun `drive-keyboard-e2e.ps1` and the other E2E scripts on the current (restyled) build, and look at the running app on screen.
- Other DPI scales, real screen readers, multi-monitor, Windows 10: NOT TESTED.
- Alternative translation model evaluation (above); BPE-tokenizer Argos packages; a region-masking fallback for self-capture on builds without `WDA_EXCLUDEFROMCAPTURE`.
- Nothing is committed or pushed; the working tree awaits the user's review.
