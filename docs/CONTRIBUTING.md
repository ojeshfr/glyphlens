# Contributing to GlyphLens

## Branch naming

Branch from `main` using `<type>/<short-description>`:

| Prefix | Use |
| --- | --- |
| `feature/` | New capability, e.g. `feature/wgc-monitor-capture` |
| `fix/` | Bug fix, e.g. `fix/dpi-offset-secondary-monitor` |
| `docs/` | Documentation only |
| `refactor/` | Behaviour-preserving restructuring |
| `chore/` | Build, CI, dependencies |
| `test/` | Tests only |

`main` is always releasable-quality: green CI, no broken references.

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<optional scope>): <imperative summary, ≤ 72 chars>

<optional body: what and why>
```

Types: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `ci`, `perf`, `build`.
Scopes: `core`, `windows`, `infra`, `app`, `worker`, `ipc`, `docs`, `ci`.

Examples: `feat(windows): capture a single monitor with Windows Graphics Capture`,
`fix(ipc): reject envelopes with naive timestamps`.

## Pull request expectations

- One focused change per PR, linked to its roadmap phase or issue.
- Fill in the PR template checklist.
- CI must pass (format, Release build with zero warnings, .NET and Python tests).
- Update docs, ADRs and `CHANGELOG.md` when behaviour, architecture or the protocol changes.
- Protocol changes update **both** the .NET and Python implementations and `docs/IPC-PROTOCOL.md`.
- No screenshots of private content, model weights, secrets or generated placeholder assets.

## Test requirements

- New logic comes with tests that exercise real behaviour (no `Assert.True(true)`).
- Tests are deterministic and must not need internet access or downloaded models.
- Tests that need an interactive desktop (real capture, UI Automation against live apps) must be
  clearly marked and skipped in headless CI.
- See [TESTING.md](TESTING.md).

## Coding conventions

- C#: `.editorconfig` rules (file-scoped namespaces, `_camelCase` private fields, braces always,
  nullable enabled). `dotnet format` must report no changes.
- Respect the dependency rules in [ARCHITECTURE.md](ARCHITECTURE.md).
- MVVM in the App: no business logic in code-behind.
- Validate inputs at model boundaries; prefer immutable models.
- Python: PEP 8, type hints, standard library unless a dependency is justified; 100-column lines.
- Never introduce network calls into the capture/OCR/translation path.
- Do not copy third-party code without checking and recording its license.
