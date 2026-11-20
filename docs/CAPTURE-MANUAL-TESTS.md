# Capture: verification record and manual checklist

This file separates what is **automated**, what needs **real Windows hardware** (opt-in), and what is **manual**,
and records what was actually run. Statuses: **PASS**, **FAIL**, **NOT TESTED**, **BLOCKED**. Nothing is marked
PASS without having been executed.

Environment of the recorded run (2026-11-19): Windows 11 Home 10.0.26200 x64, **1 display** (2560×1600), NVIDIA
GeForce RTX 3060 Laptop GPU, .NET SDK 10.0.401.

## 1. Automated tests that run everywhere (CI)

`dotnet test GlyphLens.sln -c Release`. No display, GPU, window or network needed.

| Area | Examples | Result |
| --- | --- | --- |
| Capture models | targets, frame metadata validation, DPI conversion at 100/125/150/200 %, mixed-DPI, negative coordinates, portrait | PASS |
| Status transitions | every one of the 25 state pairs checked against the documented table | PASS |
| Frame publication | latest-wins, no backlog (1000 frames → 1 live), consumer keeps frame after replacement, cancellation, dispose wakes waiters, concurrent producer/consumers | PASS |
| Resource lifecycle | ref counting, buffer released exactly once, retain-after-release throws, concurrent retain/dispose | PASS |
| Windows helpers | target-id round trip/rejection, row compaction with GPU padding, HRESULT → error mapping, options validation | PASS |
| Service guard rails | idle stop no-op, unknown target → `Faulted`, restart after fault, cancelled start, dispose idempotent, throwing subscriber isolated | PASS |
| View model | target listing/selection, start/stop/refresh, error display, fault display, double-invoke, dispose cancels pending start, UI-thread marshalling | PASS |

## 2. Automated tests that need real hardware (opt-in)

Skipped unless `GLYPHLENS_RUN_CAPTURE_TESTS=1` and an interactive desktop is available:

```powershell
$env:GLYPHLENS_RUN_CAPTURE_TESTS = "1"; dotnet test tests/GlyphLens.Windows.Tests -c Release
```

| Test | What it proves | Result (3 consecutive runs) |
| --- | --- | --- |
| Provider lists displays, exactly one primary | enumeration | PASS |
| Primary display delivers frames of the display's physical pixel size, non-black, then stops cleanly | real capture, DPI-correct size, status sequence | PASS |
| 30 start/stop cycles do not grow kernel Section handles | native resource release | PASS |
| Start while capturing rejected; cancelled start returns to `Idle` | lifecycle | PASS |
| Unknown window faults with `TargetNotFound`, service restarts afterwards | error path | PASS |
| Separate-process window is captured; killing it faults with `TargetClosed` | window capture, closure during capture | PASS |
| Dispose while capturing stops capture and raises no later events | shutdown | PASS |

## 3. Real application, driven through UI Automation

The built `GlyphLens.exe` was launched and driven by reading/invoking its own controls (no screenshots of the
desktop). Script is not part of the repository; results below were observed.

| Check | Result |
| --- | --- |
| Launches, window responsive, status "Not capturing", Start enabled, Stop disabled | PASS |
| Display capture: status "Capturing", frame size `2560 × 1600 px` (the display's physical size), frame counter advancing | PASS |
| Stop returns to "Not capturing", frame info cleared, Start re-enabled; repeated 3 times | PASS |
| Window capture of a separate-process window; frame size equals the target size (938×619) | PASS |
| Own GlyphLens window absent from the window list | PASS |
| Closing the captured window shows "The captured window was closed.", status faulted, Start enabled, Stop disabled | PASS |
| Closing the application while capturing exits with code 0 in about 0.5 s | PASS |
| Preview shows the captured window, preserving aspect ratio, in the dark theme (rendered with `PrintWindow` of the GlyphLens window only) | PASS (visually inspected) |

## 3b. OCR verification (Phase 2b)

The built `GlyphLens.exe` was driven through UI Automation against a **controlled test window** (a separate process showing
two lines of known text), not against real user content. Environment as above; Windows OCR had **one language installed
(en-US)**. The script is not part of the repository; results below were observed.

| Check | Result |
| --- | --- |
| Read text is disabled before a capture and enabled while capturing | PASS |
| Read text on the captured window recognised "Hello GlyphLens OCR 2026" and "Second line of text" (plus the window title and the close button "x": 4 lines in total), status "4 lines · 47 ms · en-US" | PASS |
| Overlay boxes are drawn over the picture and line up with the recognised lines (visual inspection of a `PrintWindow` render of the GlyphLens window only) | PASS (visually inspected) |
| Live toggle on: lines are read, manual Read text is disabled while live; toggle off: Read text is enabled again | PASS |
| Stopping the capture clears the results list, the boxes and the status | PASS |
| Closing the application exits with code 0 | PASS |

Not tested: languages other than English, rotated text, small or low-contrast text, a language that is not installed (covered
by an automated test only), display capture of real application content, clipboard copy in the real app (covered by a view-model
test only), and behaviour on a machine with no OCR language installed.

## 4. Manual checklist (humans with the relevant hardware)

| # | Scenario | Steps | Expected | Status |
| --- | --- | --- | --- | --- |
| M1 | Primary display | Display → pick primary → Start | Live preview, size equals the display's pixel resolution, counter increases | PASS (section 3) |
| M2 | Secondary display | Needs 2+ displays. Choose the second display, Start | Preview shows that display; size equals its resolution | NOT TESTED (1 display available) |
| M3 | Application window | Window → pick Chrome/Edge/PDF reader/WhatsApp → Start | Window content shown; moving/resizing the window keeps the preview correct | PASS for a separate-process test window; real apps NOT TESTED |
| M4 | Repeated start/stop | Start/Stop 20 times | No errors; memory and handle count stable | PASS (automated, section 2); long manual soak NOT TESTED |
| M5 | DPI 100/125/150/200 % | Change Windows scale, restart app, capture | Frame size equals the physical resolution; preview aspect ratio correct | PASS at 150 % (this machine's scale, as listed by the app: "Display 1 (2560×1600, 150%, primary)"); 100/125/200 % NOT TESTED |
| M6 | Mixed DPI | Two displays with different scales | Each display's size/scale listed correctly; captures correct | NOT TESTED (1 display) |
| M7 | Portrait display | Rotate a display | Size listed as height > width; preview correct | NOT TESTED (math unit-tested) |
| M8 | Window closed during capture | Capture a window, close it | Error shown, controls recover | PASS (automated + UI Automation) |
| M9 | Display disconnected during capture | Unplug/disable the captured display | Error "display was disconnected", capture stops, app responsive | NOT TESTED (1 display; watchdog logic covers window closure only on hardware) |
| M10 | Shutdown while capturing | Close the app mid-capture | Exits promptly, no orphan process | PASS (section 3) |
| M11 | Elevated window | Capture a window of an app run as administrator | Clear "access denied" message | NOT TESTED |
| M12 | Minimised window | Minimise the captured window | No frames; app stays responsive | NOT TESTED |
| M13 | GPU reset | Trigger a driver reset | "graphics device was reset" error; can restart | NOT TESTED |
| M14 | Windows 10 | Run on Windows 10 2004+ | Works | NOT TESTED (Windows 11 only) |
