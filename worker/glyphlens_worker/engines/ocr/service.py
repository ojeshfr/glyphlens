"""The ``ocr.detect`` service: recognise the text lines of one screen capture, locally.

The application sends one PNG (a captured frame or window) and receives the recognised lines with their bounding boxes,
in reading order, and the recogniser's own confidence for each (a real probability, unlike Windows OCR). The recogniser
(PP-OCRv5 mobile through ONNX Runtime, see :mod:`.backend`) reads Simplified and Traditional Chinese, English, Japanese
and digits in one model, so there is no language argument that changes the result.

Like every message it makes no network request and never logs or echoes the image or the recognised text.
"""

from __future__ import annotations

import base64
import binascii
import statistics
import time
from pathlib import Path
from typing import Any

from glyphlens_worker.config.settings import ModelsLocation
from glyphlens_worker.engines.ocr.backend import (
    DETECTION_DIRECTORY,
    MODEL_FILES,
    RECOGNITION_DIRECTORY,
    OcrEngine,
    OcrOutput,
    OcrRuntimeLoader,
    OcrRuntimeUnavailableError,
    PaddleOnnxRuntimeLoader,
    RecognisedLine,
)
from glyphlens_worker.engines.payloads import invalid_request, require_object
from glyphlens_worker.errors import ServiceError

#: Pixels per image (a 4K frame is about 8.3 million).
MAX_PIXELS = 40_000_000
MAX_SIDE = 16_384
MAX_MODEL_FILE_BYTES = 512 * 1024 * 1024
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


class OcrService:
    def __init__(self, location: ModelsLocation, loader: OcrRuntimeLoader | None = None) -> None:
        self._location = location
        self._loader: OcrRuntimeLoader = loader if loader is not None else PaddleOnnxRuntimeLoader()
        self._engine: OcrEngine | None = None
        self._failure: str | None = None
        self._answered = False

    # ---- state -------------------------------------------------------------------------------------------------------

    def state(self) -> str:
        """``unavailable``, ``not_installed`` (runtime present, models missing), ``installed`` or ``ready``. Imports nothing."""
        if self._failure is not None or not self._loader.is_present():
            return "unavailable"
        if self._model_directories() is None:
            return "not_installed"
        return "ready" if self._answered else "installed"

    def problems(self) -> list[str]:
        """Why OCR cannot run, for the models panel: bounded strings without paths or content."""
        if self._failure is not None:
            return [self._failure]
        if not self._loader.is_present():
            return ["The OCR runtime is not installed. Install it with: pip install paddleocr==3.7.0 onnxruntime"]
        if self._model_directories() is None:
            return [f"The OCR models are not installed: place {DETECTION_DIRECTORY} and {RECOGNITION_DIRECTORY} in the 'ocr' folder of the model directory."]
        return []

    # ---- ocr.detect --------------------------------------------------------------------------------------------------

    def detect(self, payload: Any) -> dict[str, Any]:
        body = require_object(payload, "OCR")
        image = _read_image(body.get("image"))

        engine = self._load()
        started = time.perf_counter()
        try:
            output = engine.recognise(image)
        except Exception as exc:  # noqa: BLE001 - the recogniser is third-party code; report the type only, never the content
            raise ServiceError("ocr_failed", f"Text recognition failed ({type(exc).__name__}).") from exc
        self._answered = True
        elapsed_ms = round((time.perf_counter() - started) * 1000)
        return {
            "engine": engine.description,
            "width": output.width,
            "height": output.height,
            "elapsedMs": elapsed_ms,
            "regions": _regions(output),
        }

    # ---- loading -----------------------------------------------------------------------------------------------------

    def _model_directories(self) -> tuple[Path, Path] | None:
        root = self._location.path
        if root is None:
            return None
        detection, recognition = root / "ocr" / DETECTION_DIRECTORY, root / "ocr" / RECOGNITION_DIRECTORY
        for directory in (detection, recognition):
            for name in MODEL_FILES:
                file = directory / name
                try:
                    if file.is_symlink() or not file.is_file() or file.stat().st_size > MAX_MODEL_FILE_BYTES:
                        return None
                except OSError:
                    return None
        return detection, recognition

    def _load(self) -> OcrEngine:
        if self._engine is not None:
            return self._engine
        if self._failure is not None:
            raise ServiceError("engine_unavailable", self._failure)
        if not self._loader.is_present():
            raise ServiceError("engine_unavailable", self.problems()[0])
        directories = self._model_directories()
        if directories is None:
            raise ServiceError("engine_unavailable", self.problems()[0])
        assert self._location.path is not None
        try:
            self._engine = self._loader.load(directories[0], directories[1], self._location.path / "ocr" / ".cache")
        except OcrRuntimeUnavailableError as exc:
            self._failure = str(exc)
            raise ServiceError("engine_unavailable", self._failure) from exc
        return self._engine


# ---- helpers ------------------------------------------------------------------------------------------------------------


def _read_image(value: Any) -> bytes:
    if not isinstance(value, dict):
        raise invalid_request("'image' must be an object with 'format' and 'dataBase64'.")
    if value.get("format") != "png":
        raise invalid_request("'image.format' must be 'png'.")
    encoded = value.get("dataBase64")
    if not isinstance(encoded, str) or not encoded:
        raise invalid_request("'image.dataBase64' must be a non-empty base64 string.")
    try:
        data = base64.b64decode(encoded, validate=True)
    except (binascii.Error, ValueError):
        raise invalid_request("'image.dataBase64' is not valid base64.") from None
    if len(data) < 33 or not data.startswith(PNG_SIGNATURE) or data[12:16] != b"IHDR":
        raise invalid_request("'image' is not a PNG file.")
    width, height = int.from_bytes(data[16:20], "big"), int.from_bytes(data[20:24], "big")
    if width < 1 or height < 1 or width > MAX_SIDE or height > MAX_SIDE or width * height > MAX_PIXELS:
        raise invalid_request(f"'image' must be at most {MAX_PIXELS} pixels and {MAX_SIDE} pixels on a side.")
    return data


def _regions(output: OcrOutput) -> list[dict[str, Any]]:
    regions = []
    for line in _reading_order(output.lines):
        xs = [point[0] for point in line.polygon]
        ys = [point[1] for point in line.polygon]
        left = max(0, min(output.width - 1, int(min(xs))))
        top = max(0, min(output.height - 1, int(min(ys))))
        right = max(left + 1, min(output.width, int(max(xs) + 0.999)))
        bottom = max(top + 1, min(output.height, int(max(ys) + 0.999)))
        regions.append(
            {
                "text": line.text,
                "x": left,
                "y": top,
                "width": right - left,
                "height": bottom - top,
                "confidence": min(1.0, max(0.0, line.confidence)),
            }
        )
    return regions


def _reading_order(lines: tuple[RecognisedLine, ...]) -> list[RecognisedLine]:
    """Top to bottom; lines whose vertical centres lie within half a line height are one row, left to right."""

    def box(line: RecognisedLine) -> tuple[float, float, float]:
        ys = [p[1] for p in line.polygon]
        xs = [p[0] for p in line.polygon]
        return min(xs), min(ys), max(ys)

    ordered = sorted(lines, key=lambda line: (box(line)[1] + box(line)[2]) / 2)
    rows: list[list[RecognisedLine]] = []
    for line in ordered:
        _, top, bottom = box(line)
        if rows:
            first = rows[-1][0]
            _, first_top, first_bottom = box(first)
            height = statistics.mean((bottom - top, first_bottom - first_top))
            if abs((top + bottom) / 2 - (first_top + first_bottom) / 2) <= height / 2:
                rows[-1].append(line)
                continue
        rows.append([line])
    return [line for row in rows for line in sorted(row, key=lambda item: box(item)[0])]
