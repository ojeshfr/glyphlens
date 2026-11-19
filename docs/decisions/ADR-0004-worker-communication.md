# ADR-0004: Separate Python worker and versioned JSON protocol over Named Pipes

- **Status:** Proposed — architecture accepted; engine choices pending validation
- **Date:** 2026-11-19

## Context

The best available local OCR and offline translation engines (PaddleOCR, Argos Translate) are
Python ecosystems. The desktop app is .NET. Inference code is heavy, can crash, and has native
dependencies, so it must not destabilise the UI process.

## Decision

- Run OCR, language identification and translation in a **separate Python worker process**
  (packaged as `GlyphLens.Worker.exe`), supervised by the .NET app.
- Communicate over a **Windows Named Pipe** using **versioned UTF-8 JSON** messages with a fixed
  envelope (`protocolVersion`, `requestId`, `messageType`, `payload`, `timestamp`).
- Compatibility rule: matching major version; minor versions are additive.
- Specification: [IPC-PROTOCOL.md](../IPC-PROTOCOL.md).
- Initial Python target: 3.11. Engine dependencies are added only after PaddleOCR and Argos
  Translate are **validated together** (wheels, conflicts) on Windows.

## Alternatives considered

| Option | Why not chosen |
| --- | --- |
| In-process Python (pythonnet) | Shared crashes, GIL and native-DLL conflicts, hard packaging. |
| ONNX Runtime directly in .NET | Good for OCR models, but Argos/CTranslate2 translation has no mature .NET equivalent; possible future optimisation. |
| Localhost HTTP/gRPC | Network listener enlarges attack surface; port conflicts; firewall prompts. |
| stdin/stdout pipes | Simple, but interleaves with logging and is harder to multiplex requests. |
| Protobuf/MessagePack | More efficient, but less debuggable; can be adopted in a future major version. |

## Advantages

- Crash isolation and independent restart.
- Engines and the UI evolve and are tested independently (the worker has its own tests).
- Named Pipes are local-only and ACL-restrictable to the current user; no open ports.
- JSON is easy to inspect, test and version.

## Disadvantages

- IPC overhead and serialization cost (notably for image frames; shared memory may be needed later).
- Two runtimes to package, version and keep protocol-compatible.
- A frozen Python runtime makes the installer large.
- Process lifecycle management (startup, crashes, Job Objects) adds complexity.

## Consequences

- The protocol is a public contract between two codebases; changes need parity updates and tests on both sides.
- Phase 2 must implement process supervision, timeouts and cancellation as documented.
- Packaging (Phase 8) must freeze the worker (e.g. PyInstaller/Nuitka) and bundle notices.

## Assumptions requiring validation

- **PaddleOCR and Argos Translate resolve together** in one Python environment on Windows
  (PaddlePaddle and CTranslate2/PyTorch dependencies may conflict) for the chosen Python version.
  If not, fall back to two worker processes or alternative engines.
- Python 3.11 remains supported by both engines' current wheels; re-evaluate the version at Phase 2.
- Base64 JSON is fast enough for frame transfer; otherwise move pixels to shared memory.
- Frozen worker size and start-up time are acceptable.
- Engine choices themselves remain **proposals** until validated.
