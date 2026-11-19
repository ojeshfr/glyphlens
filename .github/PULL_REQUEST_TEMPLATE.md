## Summary

<!-- What does this change do, and why? Link the roadmap phase or issue. -->

## Type of change

- [ ] Feature (roadmap phase: )
- [ ] Bug fix
- [ ] Refactor / maintenance
- [ ] Documentation
- [ ] Build / CI

## Checklist

- [ ] `dotnet format GlyphLens.sln --verify-no-changes` passes
- [ ] `dotnet build GlyphLens.sln -c Release` passes with no warnings
- [ ] `dotnet test GlyphLens.sln -c Release` passes
- [ ] `python -m unittest discover -s tests -t .` passes (in `worker/`)
- [ ] New behaviour is covered by tests
- [ ] Dependency rules in `docs/ARCHITECTURE.md` are respected
- [ ] No screen content, model weights, secrets or personal data are committed
- [ ] No network calls added to the capture/OCR/translation path (see `docs/SECURITY.md`)
- [ ] Docs / ADRs / CHANGELOG updated where relevant

## Testing notes

<!-- How was this verified? Include manual steps for UI or capture changes. -->
