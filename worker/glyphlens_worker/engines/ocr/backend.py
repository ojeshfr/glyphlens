"""The OCR runtime: PaddleOCR's PP-OCRv5 text detection and recognition models, run with ONNX Runtime.

Why ONNX Runtime: on this project's reference machine (Windows 11, CPU only) PaddlePaddle's own CPU engine needed about
5.4 s for a 560x640 chat window and its oneDNN path crashed on these models, while the same official ``PP-OCRv5_*_onnx``
models under ONNX Runtime with 4 threads needed about 0.6-0.85 s (more threads were slower on the hybrid CPU). The
``paddlepaddle`` wheel is not needed at all: the ``paddleocr`` package only provides the pipeline and pre/post-processing.

The model files are read from local directories; nothing is downloaded (the worker's network guard would refuse it).
"""

from __future__ import annotations

import importlib.util
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol

#: ONNX Runtime threads. Measured: 1 -> 2.2 s, 2 -> 1.2 s, 3 -> 0.95 s, 4 -> 0.83 s, 8 -> 1.6 s, 12 -> 2.5 s (560x640 image).
INTRA_OP_THREADS = 4
#: Larger images are downscaled for detection (box coordinates still refer to the original image).
DETECTION_MAX_SIDE = 2048

DETECTION_MODEL = "PP-OCRv5_mobile_det"
RECOGNITION_MODEL = "PP-OCRv5_mobile_rec"
#: Directory names below ``<models>/ocr`` (the official ONNX exports published by PaddlePaddle).
DETECTION_DIRECTORY = "PP-OCRv5_mobile_det_onnx"
RECOGNITION_DIRECTORY = "PP-OCRv5_mobile_rec_onnx"
MODEL_FILES = ("inference.onnx", "inference.yml")

RUNTIME_MODULES = ("paddleocr", "onnxruntime", "cv2", "numpy")
RUNTIME_REQUIREMENTS = "paddleocr==3.7.0 onnxruntime"


class OcrRuntimeUnavailableError(Exception):
    """The OCR libraries are missing or failed to load. The message is safe to show (no paths, no content)."""


@dataclass(frozen=True)
class RecognisedLine:
    text: str
    confidence: float
    #: The polygon corners as (x, y) in image pixels.
    polygon: tuple[tuple[float, float], ...]


@dataclass(frozen=True)
class OcrOutput:
    width: int
    height: int
    lines: tuple[RecognisedLine, ...]


class OcrEngine(Protocol):
    """A loaded recogniser."""

    description: str

    def recognise(self, image: bytes) -> OcrOutput:
        """Recognise the text lines of an encoded image (PNG)."""


class OcrRuntimeLoader(Protocol):
    def is_present(self) -> bool:
        """Cheap check that the libraries are installed. Must not import them."""

    def load(self, detection: Path, recognition: Path, cache: Path) -> OcrEngine:
        """Import the libraries and load the two models. Raises :class:`OcrRuntimeUnavailableError`."""


class PaddleOnnxEngine:
    def __init__(self, pipeline: Any, numpy: Any, cv2: Any, description: str) -> None:
        self._pipeline = pipeline
        self._np = numpy
        self._cv2 = cv2
        self.description = description

    def recognise(self, image: bytes) -> OcrOutput:
        array = self._cv2.imdecode(self._np.frombuffer(image, dtype=self._np.uint8), self._cv2.IMREAD_COLOR)
        if array is None:
            raise ValueError("the image could not be decoded")
        height, width = int(array.shape[0]), int(array.shape[1])
        results = self._pipeline.predict(array)
        lines: list[RecognisedLine] = []
        for result in results:
            data = result.json["res"] if hasattr(result, "json") else dict(result)
            for text, score, polygon in zip(data["rec_texts"], data["rec_scores"], data["rec_polys"]):
                if not str(text).strip():
                    continue
                corners = tuple((float(point[0]), float(point[1])) for point in polygon)
                lines.append(RecognisedLine(str(text), float(score), corners))
        return OcrOutput(width, height, tuple(lines))


class PaddleOnnxRuntimeLoader:
    def is_present(self) -> bool:
        return not self._missing()

    def load(self, detection: Path, recognition: Path, cache: Path) -> PaddleOnnxEngine:
        missing = self._missing()
        if missing:
            raise OcrRuntimeUnavailableError(
                f"The OCR runtime is not installed (missing: {', '.join(missing)}). Install it with: pip install {RUNTIME_REQUIREMENTS}"
            )
        # Set before paddleocr is imported: no connectivity check, and anything it caches stays under our own directory.
        os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True"
        os.environ["PADDLE_PDX_CACHE_HOME"] = str(cache)
        try:
            import cv2
            import numpy
            import onnxruntime
            import paddleocr

            pipeline = paddleocr.PaddleOCR(
                text_detection_model_name=DETECTION_MODEL,
                text_detection_model_dir=str(detection),
                text_recognition_model_name=RECOGNITION_MODEL,
                text_recognition_model_dir=str(recognition),
                use_doc_orientation_classify=False,
                use_doc_unwarping=False,
                use_textline_orientation=False,
                text_det_limit_side_len=DETECTION_MAX_SIDE,
                text_det_limit_type="max",
                device="cpu",
                engine="onnxruntime",
                engine_config={"intra_op_num_threads": INTRA_OP_THREADS, "graph_optimization_level": 99, "log_severity_level": 3},
            )
        except Exception as exc:  # noqa: BLE001 - a native library can fail to load in many ways; report the type only
            raise OcrRuntimeUnavailableError(
                f"The OCR runtime failed to load ({type(exc).__name__}). Reinstall it with: pip install --force-reinstall {RUNTIME_REQUIREMENTS}"
            ) from exc
        description = f"paddleocr {paddleocr.__version__} PP-OCRv5 mobile, onnxruntime {onnxruntime.__version__}"
        return PaddleOnnxEngine(pipeline, numpy, cv2, description)

    @staticmethod
    def _missing() -> list[str]:
        missing = []
        for module in RUNTIME_MODULES:
            try:
                found = importlib.util.find_spec(module)
            except (ImportError, ValueError):
                found = None
            if found is None:
                missing.append(module)
        return missing
