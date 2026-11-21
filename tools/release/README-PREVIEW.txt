GlyphLens preview (unsigned developer preview, Windows 11 x64)
================================================================

What this is
  The GlyphLens desktop app plus the source of its Python worker. It reads text on a window or a display with local OCR and shows an
  English translation in a small panel above your other apps. There is no installer and no bundled model: one click on the Overview
  page downloads and installs what Chinese translation needs (see "Set-up").
  It was tested on one machine (Windows 11 build 26200). It is Chinese-first (Simplified and Traditional).

Before you run it, check the download
  In PowerShell:   Get-FileHash -Algorithm SHA256 .\GlyphLens-<version>-win-x64.zip
  The value must equal the one on the release page (and in the .sha256 file next to the zip). If it differs, do not run it.

Windows SmartScreen
  This build is not code-signed, so Windows may say "Windows protected your PC". Only after the checksum matched and you trust the
  source, choose More info, then Run anyway. A signed installer is planned.

Set-up (once)
  1. Start GlyphLens.exe. On its first start it puts a "GlyphLens" shortcut on your desktop (once; delete it if you do not want it).
  2. Open the Overview page and click "Install everything". It lists what it will download (uv, a private Python 3.12, 70 OCR and
     translation packages, the PP-OCRv5 OCR models and the Chinese translation models; about 360 MB with Traditional Chinese), from
     which servers, and where it goes (%LOCALAPPDATA%\GlyphLens\runtime and ...\models). Click "Download and install". It takes a few
     minutes, needs no administrator rights and does not touch your own Python. Every file is checked against a pinned SHA-256.
     If a step fails, "Try again" continues where it stopped; "Show log" opens the technical log.
  3. When the checklist is green, turn translation on and choose a window on the Capture page. From then on everything runs offline.
  Each model has its own licence (Argos models CC-BY 4.0, PP-OCRv5 Apache-2.0); the consent panel names them.

Manual set-up (alternative, for people who prefer to do it themselves)
  - Python 3.12 environment in this folder's worker\ directory:
       py -3.12 -m venv .venv
       .venv\Scripts\python.exe -m pip install ctranslate2==4.9.0 sentencepiece==0.2.3 paddleocr==3.7.0 onnxruntime==1.32.0
  - Chinese OCR: the two official PP-OCRv5 ONNX folders (PP-OCRv5_mobile_det_onnx and PP-OCRv5_mobile_rec_onnx, each with
    inference.onnx and inference.yml) into %LOCALAPPDATA%\GlyphLens\models\ocr\
  - Translation: an Argos Translate zh-en (and zt-en) .argosmodel file, imported on the Models page.

Network
  The app does not send your screen or recognised text anywhere. It downloads only when you click "Install everything" and confirm.
  It contains an automatic update check that does nothing until a signed update feed is configured, and this preview ships with none. Read docs/SECURITY.md in the repository for the exact claims and limits.

Licence
  Apache License 2.0: see LICENSE, NOTICE and THIRD-PARTY-NOTICES.md in this folder. Models are not part of GlyphLens and keep their licences.

Source and issues
  https://github.com/ojeshfr/glyphlens
