# Releasing

There is no installer yet (planned: Phase 8). A release is an unsigned zip made by `tools/make-release.ps1`, published as a GitHub
pre-release. Nothing here signs, uploads or publishes by itself.

## What a release contains

`GlyphLens-<version>-win-x64.zip`: a framework-dependent `win-x64` publish of the app (the user installs the .NET 10 Desktop Runtime),
the worker source (`worker/glyphlens_worker`, standard library only), `LICENSE`, `NOTICE`, `THIRD-PARTY-NOTICES.md` and
`README-PREVIEW.txt` (set-up steps). No Python packages and no model files are included. The checksum goes next to it as
`<zip>.sha256` and on the release page.

## Steps

1. Start from a clean, committed `main`: format check, Release build and all test suites green
   (`dotnet format GlyphLens.sln --verify-no-changes`, `dotnet test GlyphLens.sln -c Release`, the worker tests).
2. Update `CHANGELOG.md`, `THIRD-PARTY-NOTICES.md` (if a redistributed dependency changed) and the version in `Directory.Build.props`.
3. Build: `powershell -File tools\make-release.ps1 -Version <version>` (output in `artifacts\release`, ignored by Git). Open the zip and
   check the entries (`GlyphLens.exe`, the DLLs, `worker/`, the three licence files, no `.pdb` or `.xml`).
4. Smoke test the **extracted** zip in an empty folder: the app starts and responds, and `python -m glyphlens_worker --version` works
   from its `worker` folder.
5. Tag the commit that was built (`git tag -a v<version> -m ...`), push the tag and create the release as a **pre-release** with the zip
   and the `.sha256` file attached; put the SHA-256, the SmartScreen note (the build is unsigned) and the requirements in the notes.
6. Update the website's release configuration (`site/src/release.ts`: `published`, `version`, `url`, `sizeBytes`, `sha256`) and republish it.

## Not done yet

Code signing, an installer, a self-contained build, a signed update feed (`docs/UPDATES.md`), and a real run of the update path against
a published release. SmartScreen will warn about unsigned builds.
