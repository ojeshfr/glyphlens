# ADR-0002: Modular architecture with independent Core, Windows, Infrastructure and App projects

- **Status:** Accepted (Phase 0)
- **Date:** 2026-11-19

## Context

The application combines OS-specific code (capture, UI Automation, hotkeys), external-process
integration (worker IPC), persistence and UI. Without boundaries these tend to tangle, making the
logic untestable and the engines hard to replace.

## Decision

Four projects with one-directional dependencies:

- `GlyphLens.Core` (`net10.0`): models and service interfaces. Depends on nothing.
- `GlyphLens.Windows` (`net10.0-windows`): Windows adapters. Depends on Core.
- `GlyphLens.Infrastructure` (`net10.0`): IPC, persistence, cache, logging. Depends on Core.
- `GlyphLens.App` (`net10.0-windows`, WPF): UI and composition root. Depends on all three.

Windows and Infrastructure never reference each other. Boundaries are enforced by project
references and by architecture tests that inspect assembly references.

## Alternatives considered

- **Single project:** simplest, but no enforced boundaries and Core logic would drag in Windows/WPF.
- **Full Clean/Onion architecture with many layers:** over-engineered for the current scope.
- **Plugin/MEF architecture:** unnecessary indirection; engines are replaced behind interfaces instead.

## Advantages

- Core logic (fusion, validation, protocol) is unit-testable without Windows or the worker.
- Engines (OCR, translation) can be swapped by implementing an interface.
- Clear ownership: where a new feature belongs is unambiguous.

## Disadvantages

- More projects and references to maintain.
- Cross-cutting features need interface design up front.
- Infrastructure is `net10.0`, so any future Windows-only need there (e.g. Job Objects, pipe ACLs)
  must be isolated or the project retargeted. This should be revisited in Phase 2.

## Consequences

- Interfaces are added only when a real implementation is planned (no speculative abstractions).
- A fusion/application-layer project may be added in Phase 3 if Core grows beyond contracts and models.
- Violating the dependency rules fails the architecture tests.

## Assumptions requiring validation

- Windows-only pipe security (ACLs, `CurrentUserOnly`, Job Objects) can live in Infrastructure
  without forcing it to a Windows TFM; otherwise that code moves to `GlyphLens.Windows` behind a Core interface.
