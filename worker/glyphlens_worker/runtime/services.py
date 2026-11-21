"""The engine services the dispatcher talks to, wired together for one worker process."""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field

from glyphlens_worker.config.settings import ModelsLocation, resolve_models_location
from glyphlens_worker.engines.ocr.service import OcrService
from glyphlens_worker.engines.language_detection.service import LanguageDetectionService
from glyphlens_worker.engines.translation.service import TranslationService
from glyphlens_worker.runtime.health import ComponentStates


@dataclass(frozen=True)
class Services:
    detection: LanguageDetectionService
    translation: TranslationService
    ocr: OcrService = field(default_factory=lambda: OcrService(ModelsLocation(None)))

    def component_states(self) -> ComponentStates:
        """Cheap states for ``worker.health`` and ``worker.initialize``: nothing is imported or loaded."""
        health = self.translation.health()
        return ComponentStates(self.detection.state(), health.translation, health.models, self.ocr.state())


def create_services(environ: Mapping[str, str] | None = None) -> Services:
    """The production services: model directory from the environment, real libraries loaded lazily."""
    location = resolve_models_location(environ)
    return Services(LanguageDetectionService(), TranslationService(location), OcrService(location))
