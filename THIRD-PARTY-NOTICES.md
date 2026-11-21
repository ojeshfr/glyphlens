# Third-party notices

GlyphLens itself is licensed under the Apache License 2.0 (see [LICENSE](LICENSE) and [NOTICE](NOTICE)). This file lists the third-party
software that a release **contains**, the software the user installs **separately**, and the licences that apply. Checked on 2026-11-21 from
the package metadata and the upstream repositories' licence files; re-check whenever a dependency changes.

## Contained in the release zip (`win-x64`, self-contained publish)

| Component | Version | Licence | Copyright notice |
| --- | --- | --- | --- |
| .NET 10 runtime and Windows Desktop runtime (WPF) | 10.0.x | MIT (Microsoft; the licence and the third-party notices are in `licenses/dotnet/`) | Copyright (c) .NET Foundation and Contributors / Microsoft Corporation |
| Vortice.Direct3D11, Vortice.DirectX, Vortice.DXGI | 3.8.3 | MIT | Copyright (c) Amer Koleci and Contributors |
| Vortice.Mathematics | 2.1.0 | MIT | Copyright (c) Amer Koleci and Contributors |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT | (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci |
| C#/WinRT (`WinRT.Runtime.dll`) | 2.2.0 | MIT (upstream repository `microsoft/CsWinRT`) | Copyright (c) Microsoft Corporation. All rights reserved. |
| Windows SDK for .NET projection (`Microsoft.Windows.SDK.NET.dll`) | 10.0.19041.55 | Microsoft Windows SDK licence terms (<https://aka.ms/WinSDKLicenseURL>); added by the standard .NET publish for Windows-targeting apps | Copyright (c) Microsoft Corporation. All rights reserved. |

The worker (`worker/glyphlens_worker`) is GlyphLens's own code and uses only the Python standard library.

### MIT License (applies to the MIT components above, each with its own copyright notice from the table)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Required but not included (the user installs these)

| Component | Where it is needed | Licence (from package metadata) |
| --- | --- | --- |
| Python 3.11 or newer | to run the worker (Install everything provides one) | Python Software Foundation License |
| CTranslate2 4.9.0 | offline translation | MIT |
| SentencePiece 0.2.3 | offline translation | Apache-2.0 |
| PaddleOCR 3.7.0 | Chinese OCR | Apache-2.0 |
| ONNX Runtime | Chinese OCR | MIT |
| lingua-language-detector 2.2.0, py3langid 0.4.0 (optional) | language identification | not re-checked here (not installed on the build machine) |

Their own dependencies (for example NumPy, OpenCV, Pillow, pyclipper, Shapely for PaddleOCR) carry their own permissive licences; they are
installed by the user with `pip` and are not redistributed by GlyphLens.

## Downloaded on the user's request by "Install everything" (not redistributed by GlyphLens)

The one-click setup fetches these from their publishers when the user clicks Install everything and confirms; they are not part of the
repository or of a release zip. Checked on 2026-11-21 from the sources named.

| Component | From | Licence (source) |
| --- | --- | --- |
| uv 0.13.7 (Astral), only `uv.exe` is kept | github.com/astral-sh/uv release | MIT OR Apache-2.0 (`license` in the tagged `Cargo.toml`; `LICENSE-MIT`, `LICENSE-APACHE` in the repository) |
| CPython 3.12.16, python-build-standalone build `20261026` (fetched by uv) | releases.astral.sh (Astral's mirror of the python-build-standalone releases) | Python Software Foundation License for CPython (`LICENSE.txt` in the installed build, which also lists the licences of the bundled libraries such as OpenSSL); the python-build-standalone build scripts are MPL-2.0 (repository `LICENSE`) |
| 70 Python packages of `worker/requirements/engines.lock.txt` (CTranslate2, SentencePiece, PaddleOCR, PaddleX, ONNX Runtime, NumPy, OpenCV, Pillow and their dependencies; fetched by uv) | pypi.org | each package's own licence (package metadata); the direct engines are listed above |
| PP-OCRv5 mobile det/rec ONNX models | huggingface.co/PaddlePaddle (pinned commits) | Apache-2.0 (model cards) |
| Argos Translate zh-en and zt-en 1.9 packages | argos-net.com | CC-BY 4.0 (the README inside each package; kept next to the installed model) |

## Models (never included)

No model weights are in the repository or in a release. The PP-OCRv5 files and the Argos Translate `.argosmodel` packages are downloaded (by the user, or by Install everything on the user's request) and
imported by the user; each package has its own licence and the user must check it before use or redistribution.

## Test-only and build-time dependencies

xUnit, `System.Drawing.Common` and the other packages used only by the test projects, the .NET SDK and the Python test tooling are not part
of a release.
