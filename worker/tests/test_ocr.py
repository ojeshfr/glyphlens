"""Tests for the ocr.detect service with a fake recogniser, plus one opt-in test with the real PP-OCRv5 models."""

from __future__ import annotations

import base64
import os
import struct
import tempfile
import unittest
import zlib
from pathlib import Path

from glyphlens_worker.config.settings import ModelsLocation
from glyphlens_worker.engines.ocr.backend import (
    DETECTION_DIRECTORY,
    RECOGNITION_DIRECTORY,
    OcrOutput,
    OcrRuntimeUnavailableError,
    RecognisedLine,
)
from glyphlens_worker.engines.ocr.service import OcrService
from glyphlens_worker.errors import ServiceError


def make_png(width: int = 4, height: int = 3) -> bytes:
    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + b"\x80\x80\x80" * width for _ in range(height))
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")


def payload_for(png: bytes) -> dict[str, object]:
    return {"image": {"format": "png", "dataBase64": base64.b64encode(png).decode("ascii")}}


def line(text: str, left: float, top: float, right: float, bottom: float, confidence: float = 0.97) -> RecognisedLine:
    return RecognisedLine(text, confidence, ((left, top), (right, top), (right, bottom), (left, bottom)))


class FakeEngine:
    description = "fake-ocr 1.0"

    def __init__(self, lines: tuple[RecognisedLine, ...] = (), width: int = 400, height: int = 300, failure: Exception | None = None) -> None:
        self.lines, self.width, self.height, self.failure = lines, width, height, failure
        self.images: list[bytes] = []

    def recognise(self, image: bytes) -> OcrOutput:
        self.images.append(image)
        if self.failure is not None:
            raise self.failure
        return OcrOutput(self.width, self.height, self.lines)


class FakeLoader:
    def __init__(self, engine: FakeEngine | None = None, present: bool = True, failure: str | None = None) -> None:
        self.engine, self.present, self.failure = engine or FakeEngine(), present, failure
        self.loads = 0
        self.arguments: tuple[Path, Path, Path] | None = None

    def is_present(self) -> bool:
        return self.present

    def load(self, detection: Path, recognition: Path, cache: Path) -> FakeEngine:
        self.loads += 1
        self.arguments = (detection, recognition, cache)
        if self.failure is not None:
            raise OcrRuntimeUnavailableError(self.failure)
        return self.engine


class OcrTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        self.models = Path(self._temp.name) / "models"

    def install_models(self) -> None:
        for name in (DETECTION_DIRECTORY, RECOGNITION_DIRECTORY):
            directory = self.models / "ocr" / name
            directory.mkdir(parents=True)
            (directory / "inference.onnx").write_bytes(b"onnx")
            (directory / "inference.yml").write_bytes(b"yml")

    def service(self, loader: FakeLoader | None = None, installed: bool = True) -> tuple[OcrService, FakeLoader]:
        loader = loader or FakeLoader()
        if installed:
            self.install_models()
        return OcrService(ModelsLocation(self.models), loader), loader

    def rejected(self, service: OcrService, payload: object, code: str = "invalid_request") -> ServiceError:
        with self.assertRaises(ServiceError) as raised:
            service.detect(payload)
        self.assertEqual(raised.exception.code, code)
        return raised.exception


class StateTests(OcrTestCase):
    def test_without_the_runtime_ocr_is_unavailable_and_says_how_to_fix_it(self) -> None:
        service, _ = self.service(FakeLoader(present=False))

        self.assertEqual(service.state(), "unavailable")
        self.assertIn("pip install", service.problems()[0])
        self.assertEqual(self.rejected(service, payload_for(make_png()), "engine_unavailable").message, service.problems()[0])

    def test_with_the_runtime_but_no_models_ocr_is_not_installed(self) -> None:
        service, loader = self.service(installed=False)

        self.assertEqual(service.state(), "not_installed")
        self.assertIn(DETECTION_DIRECTORY, service.problems()[0])
        self.rejected(service, payload_for(make_png()), "engine_unavailable")
        self.assertEqual(loader.loads, 0)

    def test_models_with_a_missing_file_or_a_symlink_do_not_count(self) -> None:
        service, _ = self.service()
        (self.models / "ocr" / DETECTION_DIRECTORY / "inference.yml").unlink()

        self.assertEqual(service.state(), "not_installed")

    def test_the_state_is_installed_until_a_request_succeeded_then_ready_and_never_loads_anything(self) -> None:
        service, loader = self.service()

        self.assertEqual(service.state(), "installed")
        self.assertEqual(loader.loads, 0)
        service.detect(payload_for(make_png()))
        self.assertEqual(service.state(), "ready")

    def test_the_models_are_loaded_once_from_the_models_directory_and_reused(self) -> None:
        service, loader = self.service()

        service.detect(payload_for(make_png()))
        service.detect(payload_for(make_png()))

        self.assertEqual(loader.loads, 1)
        assert loader.arguments is not None
        self.assertEqual(loader.arguments[0], self.models / "ocr" / DETECTION_DIRECTORY)
        self.assertEqual(loader.arguments[1], self.models / "ocr" / RECOGNITION_DIRECTORY)
        self.assertEqual(loader.arguments[2], self.models / "ocr" / ".cache")

    def test_a_load_failure_is_reported_and_remembered_without_retrying(self) -> None:
        service, loader = self.service(FakeLoader(failure="The OCR runtime failed to load (ImportError)."))

        first = self.rejected(service, payload_for(make_png()), "engine_unavailable")
        second = self.rejected(service, payload_for(make_png()), "engine_unavailable")

        self.assertIn("failed to load", first.message)
        self.assertEqual(first.message, second.message)
        self.assertEqual(loader.loads, 1)
        self.assertEqual(service.state(), "unavailable")

    def test_without_a_model_directory_nothing_is_installed(self) -> None:
        service = OcrService(ModelsLocation(None), FakeLoader())

        self.assertEqual(service.state(), "not_installed")


class RequestValidationTests(OcrTestCase):
    def test_the_payload_must_be_an_object_with_a_png_image(self) -> None:
        service, loader = self.service()
        for bad in (None, [], "x", {}, {"image": None}, {"image": "abc"}, {"image": {}}, {"image": {"format": "jpeg", "dataBase64": "AAAA"}},
                    {"image": {"format": "png"}}, {"image": {"format": "png", "dataBase64": ""}}, {"image": {"format": "png", "dataBase64": 5}}):
            with self.subTest(bad=str(bad)[:40]):
                self.rejected(service, bad)
        self.assertEqual(loader.loads, 0)  # refused before anything is loaded

    def test_invalid_base64_and_non_png_data_are_refused(self) -> None:
        service, _ = self.service()

        self.rejected(service, {"image": {"format": "png", "dataBase64": "not base64!!"}})
        self.rejected(service, payload_for(b"GIF89a" + b"\x00" * 60))
        self.rejected(service, payload_for(b"\x89PNG\r\n\x1a\n" + b"\x00" * 10))

    def test_the_declared_size_is_checked_before_the_image_is_decoded(self) -> None:
        service, loader = self.service()

        self.rejected(service, payload_for(make_png(20000, 10)))
        self.rejected(service, payload_for(make_png(8000, 6000)))
        self.rejected(service, payload_for(make_png(0, 5)))
        self.assertEqual(loader.engine.images, [])

    def test_a_normal_4k_frame_is_within_the_limits(self) -> None:
        service, _ = self.service()

        service.detect(payload_for(make_png(3840, 2160)))

    def test_messages_never_repeat_the_image_data(self) -> None:
        service, _ = self.service()
        marker = base64.b64encode(b"TOP-SECRET-PIXELS" * 4).decode("ascii")

        error = self.rejected(service, {"image": {"format": "png", "dataBase64": marker}})

        self.assertNotIn(marker, error.message)
        self.assertNotIn("TOP-SECRET", error.message)


class ResultShapingTests(OcrTestCase):
    def detect(self, lines: tuple[RecognisedLine, ...], width: int = 400, height: int = 300) -> dict[str, object]:
        service, _ = self.service(FakeLoader(FakeEngine(lines, width, height)))
        return service.detect(payload_for(make_png(width, height)))

    def test_the_result_has_the_protocol_shape(self) -> None:
        result = self.detect((line("你好", 10.2, 20.4, 90.6, 44.1, 0.987),))

        self.assertEqual(result["engine"], "fake-ocr 1.0")
        self.assertEqual((result["width"], result["height"]), (400, 300))
        self.assertIsInstance(result["elapsedMs"], int)
        self.assertEqual(result["regions"], [{"text": "你好", "x": 10, "y": 20, "width": 81, "height": 25, "confidence": 0.987}])

    def test_regions_are_in_reading_order_top_to_bottom_and_left_to_right_within_a_row(self) -> None:
        lines = (
            line("third", 10, 200, 100, 224),
            line("second right", 220, 102, 300, 126),
            line("first", 10, 20, 100, 44),
            line("second left", 10, 100, 200, 124),
        )

        order = [r["text"] for r in self.detect(lines)["regions"]]

        self.assertEqual(order, ["first", "second left", "second right", "third"])

    def test_boxes_are_clamped_to_the_image_and_never_empty(self) -> None:
        result = self.detect((line("edge", -5, -3, 420, 310), line("dot", 399.9, 299.9, 400.2, 300.4)))
        regions = result["regions"]

        for region in regions:
            self.assertGreaterEqual(region["x"], 0)
            self.assertGreaterEqual(region["y"], 0)
            self.assertLessEqual(region["x"] + region["width"], 400)
            self.assertLessEqual(region["y"] + region["height"], 300)
            self.assertGreaterEqual(region["width"], 1)
            self.assertGreaterEqual(region["height"], 1)

    def test_confidence_is_the_recognisers_own_score_kept_within_zero_and_one(self) -> None:
        result = self.detect((line("a", 0, 0, 50, 20, 1.0000001), line("b", 0, 40, 50, 60, 0.42)))

        self.assertEqual([r["confidence"] for r in result["regions"]], [1.0, 0.42])

    def test_no_text_is_an_empty_list_not_an_error(self) -> None:
        self.assertEqual(self.detect(())["regions"], [])


class FailureTests(OcrTestCase):
    def test_a_recogniser_failure_reports_only_the_exception_type(self) -> None:
        engine = FakeEngine(failure=RuntimeError("secret text from the screen"))
        service, _ = self.service(FakeLoader(engine))

        error = self.rejected(service, payload_for(make_png()), "ocr_failed")

        self.assertIn("RuntimeError", error.message)
        self.assertNotIn("secret", error.message)
        self.assertNotEqual(service.state(), "ready")


REAL_MODELS = os.environ.get("GLYPHLENS_TEST_OCR_MODELS_DIR")
REAL_IMAGE = os.environ.get("GLYPHLENS_TEST_OCR_IMAGE")


@unittest.skipUnless(REAL_MODELS and REAL_IMAGE, "set GLYPHLENS_TEST_OCR_MODELS_DIR (containing ocr/PP-OCRv5_*_onnx) and GLYPHLENS_TEST_OCR_IMAGE (a PNG with Chinese text) to run this test")
class RealEngineTests(unittest.TestCase):
    def test_a_real_screenshot_is_recognised_offline(self) -> None:
        from glyphlens_worker.runtime import network_guard

        network_guard.install()
        service = OcrService(ModelsLocation(Path(REAL_MODELS)))  # type: ignore[arg-type]
        if service.state() == "unavailable":
            self.skipTest("the OCR runtime (paddleocr, onnxruntime) is not installed")
        data = Path(REAL_IMAGE).read_bytes()  # type: ignore[arg-type]

        result = service.detect(payload_for(data))

        text = "".join(r["text"] for r in result["regions"])
        self.assertTrue(any("一" <= ch <= "鿿" for ch in text), "no Chinese character was recognised")
        self.assertEqual(network_guard.blocked_attempts(), ())
        self.assertEqual(service.state(), "ready")


if __name__ == "__main__":
    unittest.main()
