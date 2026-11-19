# ADR-0003: Local OCR and local translation are fundamental requirements

- **Status:** Accepted (Phase 0)
- **Date:** 2026-11-19

## Context

GlyphLens processes whatever is on the user's screen: private chats, documents, video calls,
banking pages. Sending that content to a third party would be a severe privacy risk, and users
may be offline or in restricted environments.

## Decision

- All OCR, language identification and translation run **locally**.
- **No cloud API** is used for processing screen content, and there is **no silent cloud fallback**:
  a missing model yields an explicit "unavailable" state.
- The app must function with no internet connection once models are installed.
- Model downloads are explicit, user-initiated, integrity-verified actions, separate from inference.
- The worker makes no network requests at inference time.

## Alternatives considered

| Option | Why not chosen |
| --- | --- |
| Cloud OCR/translation (Google, Azure, DeepL) | Higher quality and simpler, but uploads screen content; unacceptable as a default. |
| Hybrid with cloud fallback | Silent fallbacks leak data; trust is lost if it ever happens unexpectedly. |
| Opt-in cloud mode | Possible later via a new ADR and explicit consent; not part of the foundation. |

## Advantages

- Strong privacy guarantee that is easy to explain and verify.
- Works offline; no per-request cost or rate limits.
- Predictable latency independent of network conditions.

## Disadvantages

- Local models are large (hundreds of MB per language pair) and need storage and download management.
- Translation and OCR quality may trail leading cloud services, especially for low-resource languages.
- CPU/GPU load on the user's machine; requires performance work (Phase 7).
- Packaging and licensing of models is the project's responsibility.

## Consequences

- Model management, provenance and verification are first-class features (see SECURITY.md).
- CI and tests must never depend on the network or on models.
- Any feature that introduces network traffic in the processing path needs a new ADR.

## Assumptions requiring validation

- Argos Translate quality is acceptable for the target languages → English (evaluate in Phase 4).
- PaddleOCR CPU inference latency is acceptable on mid-range hardware (measure in Phase 2/7).
- Model licenses permit redistribution if models are bundled (review before Phase 8).
