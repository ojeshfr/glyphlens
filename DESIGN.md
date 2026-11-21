# GlyphLens — Product and UI Design Specification

- **Status:** product design target (design intent, not proof of implementation).
- **Product:** GlyphLens. **Platform:** Windows 10/11 x64.
- **Primary workflow:** Simplified Chinese on-screen text → English in a floating overlay.
- **Secondary workflow:** Traditional Chinese → English.
- **Design principle:** overlay-first, with a lightweight management window.

This document describes the intended product experience. It does not claim that every described feature is implemented. What actually
works, and how it was tested, is recorded in [docs/IMPLEMENTATION-STATUS.md](docs/IMPLEMENTATION-STATUS.md) and
[docs/TESTING.md](docs/TESTING.md); those documents are authoritative for status. Architecture decisions are in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and `docs/decisions/`. Keep design intent (here) and implementation status (there) separate.

## 1. Product experience

GlyphLens is a native Windows desktop companion that translates text visible in other applications. The user keeps using WhatsApp,
WeChat, Instagram, Google Meet, a browser, a PDF reader or another ordinary application while GlyphLens shows translations in a compact
floating window.

Two surfaces:

1. **Floating translation overlay — the primary experience.** Where users read live translations. It runs independently of the main
   management window.
2. **Main application — the management experience.** Where users configure capture targets, language preferences, local models, overlay
   appearance, history, privacy and diagnostics.

The main application is not a large analytics dashboard, and users should not have to return to it to read each translation.

### Priorities

- **P0:** Simplified Chinese (`zh`) → English.
- **P1:** Traditional Chinese (`zt`) → English.
- **Future:** other source languages, without compromising the Chinese-first experience.
- Offline inference is the default. Never silently fall back to a cloud service.
- Preserve the source text and the relationship between source and translation.

## 2. Visual direction

**Apple-inspired** (changed on the user's request on 2026-11-21; it replaces the earlier "native Windows utility" direction): the calm,
light look of macOS System Settings and Apple's own apps, built with WPF on Windows. Light grey window, white rounded sections, a
grey sidebar with icons, filled capsule buttons, iOS-style switches, segmented controls, a thin slider with a round knob, and a
floating overlay that reads like an Apple panel: near-white, generously rounded, soft shadow, translations in grey bubbles.

**Within limits:** no Apple logos, no imitation of macOS window buttons ("traffic lights"), and no SF Pro font (its licence allows
Apple platforms only): Segoe UI Variable is used instead. The window keeps the standard Windows title bar and behaviour.

**Avoid:** neon colours, purple "AI" gradients or glow; blur effects that cost readability; hero sections, oversized branding or
dashboard charts; heavy shadows or deeply nested rounded containers; decorative animation that competes with reading; emojis as
navigation icons; fake activity indicators, fake download progress, or placeholder translations presented as real results.

### Colour tokens

Use semantic WPF resource keys, defined centrally in `Themes/Tokens.xaml`; do not scatter literal hex values through XAML (a test
enforces it). Values follow Apple's light palette; where Apple's bright system colours would not reach WCAG AA as text (blue
`#007AFF`, green `#34C759`), its accessible variants are used.

| Token | Value | Use |
|---|---|---|
| `WindowBackground` | `#F5F5F7` | Main application background (Apple's light grey) |
| `Surface` | `#FFFFFF` | Sections and content surfaces |
| `SurfaceSecondary` | `#F9F9FB` | Subtle inner surfaces |
| `TextPrimary` | `#1D1D1F` | Main text (Apple's label colour) |
| `TextSecondary` | `#636366` | Supporting labels (AA on every background used, the sidebar included) |
| `TextMuted` | `#86868B` | Tertiary, non-essential information only (below AA on white) |
| `BorderSubtle` | `#E5E5EA` | Hairlines and section edges |
| `Accent` | `#0066CC` | Primary actions, selection, switches, links (Apple's link blue) |
| `AccentHover` | `#0055B3` | Hover / pressed |
| `Success` | `#248A3D` | Ready / connected (Apple's accessible green) |
| `Warning` | `#C93400` | Attention required (Apple's accessible orange) |
| `Error` | `#D70015` | Errors and failures (Apple's accessible red) |

Further tokens: `SidebarBackground` `#EDEDF0`, `ControlFill` `#E8E8ED` (grey buttons and the segmented track), `SwitchOff`
`#D9D9DE`, `OverlaySurface` (white at 97 %), `OverlayBubble` `#F2F2F7`. The Fluent theme's accent keys are pointed at `Accent`, so
the user's Windows accent colour never changes GlyphLens' look.

The overlay uses a near-white surface, a hairline edge and a soft shadow. Opacity is user-adjustable but defaults to comfortable
readability, not a transparency that harms legibility.

### Dark appearance

Settings offers System, Light and Dark; System follows Windows and switches live. The dark palette (`Themes/Palette.Dark.xaml`) uses Apple's dark
greys (window `#1C1C1E`, surface `#2C2C2E`, text `#F5F5F7`, secondary `#AEAEB2`), the same blue as a fill (`#0A64D8`, white text on it) and a lighter
blue for accent text and the focus ring (`#64B5FF`), and Apple's brighter status colours (green `#30D158`, orange `#FF9F0A`, red `#FF6961`). Light and
dark palettes define the same keys, every text pair meets the same contrast targets in both (tested), and views never use a literal colour.

### Typography

- Segoe UI Variable (Text for body, Display for titles), the Windows counterpart of Apple's system font; fall back to a CJK-capable
  system font such as Microsoft YaHei for Chinese. Icons: Segoe Fluent Icons (outline glyphs similar to SF Symbols).
- Large bold page titles 26 DIP; section headings 15 DIP semibold; body 13 DIP; supporting labels 11–12 DIP; sentence case, no
  all-caps labels.
- Chinese source and English translation text have comfortable line spacing and are selectable.
- Do not bundle fonts without reviewing their licences.

### Shape, spacing, density

- A 4-DIP rhythm: 4, 8, 12, 16, 20, 24.
- Main controls 32 DIP high; small icon buttons about 28 DIP, round.
- Radius 12 DIP for sections, 14 DIP for the overlay, 8 DIP for fields and sidebar entries, capsules (half the height) for buttons.
- Hairline separators between rows of a section (inset past the status badge), no hard outlines on notices.
- Controls: grey capsule = secondary action, accent capsule = primary action, accent text = minor action; on/off settings are
  switches (label left, switch right); a choice of two or three is a segmented control.
- Status is never shown by colour alone: badges carry a symbol (check, "!", "x", "-") and the state is written out.
- Keyboard focus: an accent ring outside the control (keyboard focus only); the selected sidebar entry turns from grey to accent
  while the list has the keyboard focus, as in macOS.

## 3. Floating translation overlay

The overlay is the primary surface: a separate native WPF `Window`, not a panel embedded in the main window.

### Default geometry

- Initial size about 400 × 560 DIP; minimum width about 320 DIP; width and height resizable.
- Stay within the usable desktop work area. Persist position and size safely; recover gracefully if the saved monitor no longer exists.
- Do not assume physical pixels and WPF device-independent units are interchangeable.

These are starting values. Validate the layout at different resolutions and DPI settings.

### Header (priority order)

1. GlyphLens mark and name.
2. Source → target indicator (for example `中文 → English`).
3. A compact live / paused / needs-attention status.
4. Collapse / expand.
5. Pin / unpin (topmost preference).
6. A small menu for settings and secondary actions.
7. Hide / close-overlay control.

The header is the drag region; buttons stay individually clickable and never start a drag. Do not put every shortcut or option in the
header; secondary actions go in the small menu.

### Body

A vertically scrolling stream of translation cards. New results appear without unexpectedly moving the user's scroll position: if the
user is at the newest item keep it visible; if they scrolled up, preserve their reading position and show a subtle new-results indicator.

Each card (a soft grey bubble) presents:

- the original Chinese text, small and secondary, first;
- the English translation below it, larger and bold (the two are told apart by order, size and weight rather than by "ORIGINAL" /
  "TRANSLATION" captions; the copy actions say which text they copy);
- an optional source-script indicator (Simplified / Traditional Chinese) when known;
- copy-original and copy-translation actions;
- confidence only when a real, meaningful value exists;
- a subtle timestamp only if it helps tell repeated messages apart.

The English translation is the most immediately readable part; the original stays available and selectable. Colour alone never
distinguishes original from translation.

### Overlay states (explicit and truthful)

Ready · Live · Paused · Starting · Model missing (say which model, with a real import action if supported) · Worker unavailable (explain,
with a real retry) · Capture stopped (clear or keep cards per a documented setting; never imply live translation) · No text found (quiet
empty state) · Uncertain text/language (keep the original, say translation was skipped) · Error (concise, with a recovery action).

Never show a green "ready" merely because a process exists: distinguish worker process, IPC, OCR engine and translation-model readiness
where that information is available.

### Controls

Where implemented and tested: drag and resize; show/hide; collapse/expand; pin above ordinary windows; **interactive mode** (the overlay
receives the mouse, text can be selected and copied); **pass-through mode** (mouse events reach the application underneath) with an
obvious, discoverable way back (for example a small unlock handle); adjustable opacity and font size; clear current session results
(with confirmation if destructive). Click-through styles are never kept on while the user is expected to interact with the overlay, and
new translations never steal keyboard focus.

### Self-capture prevention

The overlay must not create an OCR feedback loop. Use a supported capture-exclusion mechanism and verify it on the supported Windows
versions. If a fallback is needed, implement and test region masking or an equivalent. Do not claim it works on a build where it has not
been verified.

## 4. Main application

A compact manager: default about 1000 × 700 DIP, usable minimum about 820 × 580 DIP, conventional Windows title bar unless there is a
demonstrated reason otherwise. Expose only pages with real functionality.

- **Overview** — capture target and status, worker process / IPC status, Chinese OCR readiness, `zh→en` and `zt→en` model readiness, a
  clear *Show overlay* action, and start/pause/stop where genuinely wired. No charts, fake metrics or generic "AI assistant" copy.
- **Capture** — reuse the existing Windows Graphics Capture engine (do not rewrite it for visual reasons): display/window selector,
  target name and size, aspect-correct live preview, start/stop status, frame size and a frame rate only when actually measured, capture
  errors. No region-selection UI until it works.
- **Models** — Simplified Chinese → English (OCR model status, translation model status, version and size only when known, real
  import/install/remove actions only when implemented) and Traditional Chinese → English (same; show that both scripts share weights only
  if verified from the installed package metadata). Distinguish OCR models from translation models; never infer "installed" from a
  catalogue entry; no invented download progress or quality scores.
- **History** — only if persistence is implemented; otherwise omit it or mark it unavailable.
- **Settings** — only settings backed by real configuration: preferred source script or automatic detection, target language (English
  initially), default OCR engine for Chinese, overlay size / opacity / font size / interactive-vs-pass-through preference, hotkeys and
  conflict status, capture behaviour, privacy and local-processing information, diagnostics and version. No inert switches.

The main window need not stay visible while the overlay continues a session. Closing it follows an explicit, predictable lifecycle
policy and never accidentally ends a session the user expects to continue.

## 5. Chinese-first language experience

- Simplified Chinese → English is the default priority; Traditional Chinese → English is supported when its engine/model is ready.
- Auto-detection distinguishes Simplified and Traditional script only when evidence is sufficient.
- Mixed Chinese/English messages preserve Latin words, URLs, usernames, numbers and emojis.
- A coherent Chinese sentence is not split into arbitrary OCR lines for separate translation when grouping can safely reconstruct the
  message or paragraph.
- If detection is uncertain, show the original and an honest status rather than inventing a translation.
- Do not imply that all languages are supported; keep other languages out of the main user path until validated.

## 6. Interaction and keyboard behaviour

Configurable global shortcuts. Existing shortcuts are preserved and tested before they are changed. Proposed defaults:

| Shortcut | Action |
|---|---|
| Ctrl+Alt+T | Toggle translation |
| Ctrl+Alt+O | Show / hide overlay |
| Ctrl+Alt+P | Pause / resume processing |
| Ctrl+Alt+I | Switch interactive / pass-through |
| Ctrl+Alt+C | Clear current session results |

Register safely, detect conflicts, report failures; never claim a shortcut is registered when it is not. The user can always recover
from pass-through mode without guessing or restarting.

## 7. Accessibility and responsiveness

Keyboard navigation and visible focus states; accessible names and tooltips on icon-only buttons; readable contrast; correct Chinese
glyph rendering with installed fonts; no clipped text at narrow sizes; stack translation columns when side-by-side becomes too narrow;
check 100 %, 125 %, 150 % and 200 % scaling and multiple monitor resolutions / mixed DPI where hardware allows; keep text selectable
and copyable.

## 8. Motion and feedback

Sparingly: short subtle expand/collapse transitions; no animation per incoming message; no pulsing live indicator; progress indicators
only for real work with a known start and end; no fake percentages for OCR or model loading.

## 9. WPF implementation principles

- Reuse the existing WPF / MVVM architecture; keep business logic out of code-behind.
- Capture, OCR and translation stay off the UI thread.
- Incremental collection updates (never rebuild every card per result); virtualise long lists.
- Shared ResourceDictionaries and semantic styles; no new UI framework without a demonstrated need.
- Preserve existing functionality and contracts; the overlay has an independent, explicitly managed lifecycle.
- Native window styles, topmost state, click-through, capture exclusion and DPI conversion are platform-specific behaviour that needs
  tests.

## 10. Design acceptance criteria

The design is ready for release review when:

1. The overlay is the obvious primary translation surface.
2. A user can read Chinese originals and English translations without opening the main window.
3. The overlay stays usable while working in another ordinary Windows application.
4. Interactive and pass-through modes are understandable and recoverable.
5. The overlay does not translate itself on supported configurations.
6. The main app is a compact manager, not a translation dashboard.
7. Model readiness and errors are truthful.
8. Simplified and Traditional Chinese are clearly represented.
9. The UI stays readable at supported DPI and window sizes.
10. There is no fake data, inert control or unverified capability claim.

## 11. Implementation status rule

This document is a design target, not proof of implementation. For every design item, the status documentation reports whether it is
implemented, tested, verified on real Windows hardware, or not yet implemented. See
[docs/IMPLEMENTATION-STATUS.md](docs/IMPLEMENTATION-STATUS.md) ("Design conformance").
