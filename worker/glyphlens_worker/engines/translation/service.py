"""The translation service behind ``translation.translate``, ``models.list``, ``models.status`` and ``models.install``.

It owns the model directory, the (lazily loaded) runtime and a small cache of loaded translators. One request is
served at a time, so nothing here is thread-safe or needs to be.

Honest state: the runtime is imported only when a request needs it. :meth:`TranslationService.health` answers from a
cheap check that the libraries are installed, a scan of the model directory and what earlier requests learned; it never
imports them.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Any

from glyphlens_worker.config.settings import ModelsLocation
from glyphlens_worker.engines.payloads import MAX_TEXT_LENGTH, require_object
from glyphlens_worker.engines.translation.backend import (
    Ct2RuntimeLoader,
    RuntimeLoader,
    RuntimeUnavailableError,
    SegmentTranslator,
    TranslationRuntime,
)
from glyphlens_worker.engines.translation.cache import TranslatorCache
from glyphlens_worker.engines.translation.parsing import (
    TranslateItem,
    parse_route_request,
    parse_translate_request,
    require_empty_payload,
)
from glyphlens_worker.engines.translation.routes import Route, find_route, is_supported_pair, missing_package
from glyphlens_worker.engines.translation.segmentation import SEGMENTER_REVISION, TextPlan, assemble, plan_text
from glyphlens_worker.errors import ServiceError
from glyphlens_worker.models.importer import check_archive_path, install_package
from glyphlens_worker.models.registry import InstalledPackage, RegistryScan, scan_models

ENGINE_NAME = "ct2-argos"
MAX_PROBLEMS = 50
MAX_PROBLEM_LENGTH = 300


@dataclass(frozen=True)
class TranslationHealth:
    """The ``engines.translation`` and ``models`` values of ``worker.health``."""

    translation: str
    models: str


@dataclass(frozen=True)
class _Failure:
    code: str
    message: str
    required_package: str | None = None


@dataclass
class _Group:
    """Items that share one route and are translated together."""

    route: Route
    indices: list[int] = field(default_factory=list)


class _ModelLoadError(Exception):
    """A package of the route could not be loaded by the engine."""


class TranslationService:
    def __init__(
        self,
        location: ModelsLocation,
        loader: RuntimeLoader | None = None,
        max_loaded_translators: int = 2,
    ) -> None:
        self._location = location
        self._loader: RuntimeLoader = loader if loader is not None else Ct2RuntimeLoader()
        self._runtime: TranslationRuntime | None = None
        self._runtime_failure: str | None = None
        self._translators = TranslatorCache(max_loaded_translators)
        self._load_failures: dict[tuple[str, str], str] = {}

    # ---- health ------------------------------------------------------------------------------------------------------

    def health(self) -> TranslationHealth:
        """Cheap component states: no import, no model load."""
        scan = scan_models(self._location.path)
        if not scan.readable:
            models = "unavailable"
        else:
            models = "installed" if scan.packages else "not_installed"

        if self._runtime_failure is not None or not self._loader.is_present():
            translation = "unavailable"
        elif not scan.packages:
            translation = "not_installed"
        else:
            translation = "ready" if self._runtime is not None else "installed"
        return TranslationHealth(translation, models)

    # ---- models.list -------------------------------------------------------------------------------------------------

    def list_models(self, payload: Any) -> dict[str, Any]:
        require_empty_payload(payload)
        scan = scan_models(self._location.path)
        problems = [*self._location.problems, *scan.problems, *self._load_failure_problems(scan)]

        runtime: TranslationRuntime | None = None
        try:
            runtime = self._load_runtime()
        except RuntimeUnavailableError as exc:
            problems.append(str(exc))

        if runtime is None or not scan.readable:
            state = "unavailable"
        else:
            state = "installed" if scan.packages else "not_installed"
        return {
            "engine": ENGINE_NAME,
            "engineVersion": runtime.version if runtime is not None else None,
            "state": state,
            "storagePath": str(self._location.path) if self._location.path is not None else None,
            "packages": [package.to_wire() for package in scan.packages],
            "problems": [problem[:MAX_PROBLEM_LENGTH] for problem in problems[:MAX_PROBLEMS]],
        }

    # ---- models.status -----------------------------------------------------------------------------------------------

    def route_status(self, payload: Any) -> dict[str, Any]:
        source, target = parse_route_request(payload)
        scan = scan_models(self._location.path)

        if not is_supported_pair(source, target, scan.packages):
            return _status(source, target, issue="unsupported_pair")
        try:
            runtime = self._load_runtime()
        except RuntimeUnavailableError:
            return _status(source, target, issue="engine_unavailable")
        if not scan.readable:
            return _status(source, target, issue="engine_unavailable")

        route = find_route(source, target, scan.packages)
        if route is None:
            return _status(source, target, issue="model_missing", missing=missing_package(source, target, scan.packages))
        return _status(source, target, route=route, model_version=_model_version(runtime, route))

    # ---- models.install ----------------------------------------------------------------------------------------------

    def install(self, payload: Any) -> dict[str, Any]:
        archive_path = check_archive_path(require_object(payload, "install").get("path"))
        root = self._location.path
        if root is None:
            raise ServiceError("engine_unavailable", "The model directory is not available, so no package can be installed.")

        result = install_package(archive_path, root, before_replace=self._release)
        return {"package": result.package.to_wire(), "replaced": result.replaced}

    def _release(self, source: str, target: str) -> None:
        """Let go of a pair's loaded files before the package is replaced (Windows will not replace files in use)."""
        self._translators.drop_pair(source, target)
        self._load_failures.pop((source, target), None)

    # ---- translation.translate ---------------------------------------------------------------------------------------

    def translate(self, payload: Any) -> dict[str, Any]:
        request = parse_translate_request(payload)
        runtime = self._require_runtime()
        scan = scan_models(self._location.path)
        if not scan.readable:
            raise ServiceError("engine_unavailable", "The model directory is not available.")

        results: list[dict[str, Any] | None] = [None] * len(request.items)
        groups: dict[str, _Group] = {}
        for index, item in enumerate(request.items):
            planned = _plan_item(item, request.target, scan.packages)
            if isinstance(planned, _Failure):
                results[index] = _error_item(item.id, planned)
            else:
                groups.setdefault(item.source, _Group(planned)).indices.append(index)

        for source, group in groups.items():
            texts = [request.items[index].text for index in group.indices]
            try:
                translated = self._translate_group(runtime, group.route, texts)
                failure = _Failure("translation_failed", "The translation failed.")
            except _ModelLoadError:
                translated = [None] * len(texts)
                failure = _Failure("translation_failed", "The model for this language pair could not be loaded.")
            model_version = _model_version(runtime, group.route)
            for index, text in zip(group.indices, translated, strict=True):
                item = request.items[index]
                results[index] = (
                    _error_item(item.id, failure)
                    if text is None
                    else {
                        "id": item.id,
                        "status": "ok",
                        "translatedText": text,
                        "sourceLanguage": source,
                        "targetLanguage": request.target,
                        "modelVersion": model_version,
                        "route": group.route.languages,
                    }
                )

        return {"engine": f"ct2-{runtime.version}", "results": results}

    def _translate_group(self, runtime: TranslationRuntime, route: Route, texts: list[str]) -> list[str | None]:
        """Run ``texts`` through every hop of ``route``: one batched call per hop. ``None`` marks an item that failed."""
        current: list[str | None] = list(texts)
        for package in route.packages:
            current = _run_hop(self._translator(runtime, package), current)
        return current

    def _translator(self, runtime: TranslationRuntime, package: InstalledPackage) -> SegmentTranslator:
        key = (package.source, package.target, package.manifest.model_sha256)
        cached = self._translators.get(key)
        if cached is not None:
            return cached
        try:
            translator = runtime.load(package.directory, package.manifest.target_prefix)
        except Exception as exc:  # noqa: BLE001 - native loading can fail in many ways; remember the type, never the message
            self._load_failures[(package.source, package.target)] = type(exc).__name__
            raise _ModelLoadError from exc
        self._load_failures.pop((package.source, package.target), None)
        self._translators.put(key, translator)
        return translator

    # ---- runtime -----------------------------------------------------------------------------------------------------

    def _load_runtime(self) -> TranslationRuntime:
        """The runtime, imported on first use. A missing library is retried (it may be installed later); a failed import is not."""
        if self._runtime is not None:
            return self._runtime
        if self._runtime_failure is not None:
            raise RuntimeUnavailableError(self._runtime_failure)
        try:
            self._runtime = self._loader.load()
        except RuntimeUnavailableError as exc:
            if self._loader.is_present():
                self._runtime_failure = str(exc)
            raise
        return self._runtime

    def _require_runtime(self) -> TranslationRuntime:
        try:
            return self._load_runtime()
        except RuntimeUnavailableError as exc:
            raise ServiceError("engine_unavailable", str(exc)) from exc

    def _load_failure_problems(self, scan: RegistryScan) -> list[str]:
        installed = {(package.source, package.target) for package in scan.packages}
        return [
            f"The engine could not load package '{source}_{target}' ({error}); install the package again."
            for (source, target), error in sorted(self._load_failures.items())
            if (source, target) in installed
        ]


# ---- helpers ------------------------------------------------------------------------------------------------------------


def _model_version(runtime: TranslationRuntime, route: Route) -> str:
    """``ct2-<version>/seg<N>|argos:<from>-<to>:<package version>:<model hash>``, one ``argos:`` tag per hop, joined by ``>``."""
    return f"ct2-{runtime.version}/seg{SEGMENTER_REVISION}|{route.version_tag}"


def _status(
    source: str,
    target: str,
    *,
    route: Route | None = None,
    model_version: str | None = None,
    missing: str | None = None,
    issue: str = "none",
) -> dict[str, Any]:
    return {
        "source": source,
        "target": target,
        "available": route is not None,
        "route": route.languages if route is not None else [],
        "modelVersion": model_version,
        "missingPackage": missing,
        "issue": issue,
    }


def _plan_item(item: TranslateItem, target: str, packages: tuple[InstalledPackage, ...]) -> Route | _Failure:
    """The route for one item, or why it cannot be translated (checked in this order)."""
    if not item.text.strip():
        return _Failure("empty_text", "There is no text to translate.")
    if len(item.text) > MAX_TEXT_LENGTH:
        return _Failure("text_too_long", f"The text is longer than {MAX_TEXT_LENGTH} characters.")
    if item.source == target:
        return _Failure("same_language", f"The text is already in {target}; there is nothing to translate.")
    if not is_supported_pair(item.source, target, packages):
        return _Failure("unsupported_language_pair", f"The translation engine does not support {item.source} to {target}.")
    route = find_route(item.source, target, packages)
    if route is None:
        return _Failure(
            "model_missing",
            f"No installed model translates {item.source} to {target}.",
            missing_package(item.source, target, packages),
        )
    return route


def _error_item(item_id: str, failure: _Failure) -> dict[str, Any]:
    error: dict[str, Any] = {"code": failure.code, "message": failure.message}
    if failure.required_package is not None:
        error["requiredPackage"] = failure.required_package
    return {"id": item_id, "status": "error", "error": error}


def _run_hop(translator: SegmentTranslator, texts: list[str | None]) -> list[str | None]:
    """Translate every text of one hop in a single batched call. A text that fails alone comes back as ``None``.

    The call is retried per text only when the batch fails, so one bad text cannot fail the others.
    """
    outputs: list[str | None] = [None] * len(texts)
    plans: dict[int, TextPlan] = {}
    for index, text in enumerate(texts):
        if text is None:
            continue
        try:
            plans[index] = plan_text(text, translator.count_tokens)
        except Exception:  # noqa: BLE001 - the tokenizer is native code; this text fails, the others go on
            continue

    segments = [segment for plan in plans.values() for segment in plan.segments]
    try:
        translated = translator.translate(segments)
        if len(translated) != len(segments):
            raise ValueError("the engine returned a different number of segments")
    except Exception:  # noqa: BLE001 - native engine errors are not a closed set; fall back to one call per text
        for index, plan in plans.items():
            outputs[index] = _translate_alone(translator, plan)
        return outputs

    offset = 0
    for index, plan in plans.items():
        pieces = translated[offset : offset + len(plan.segments)]
        sources = plan.segments
        offset += len(plan.segments)
        if any(has_unexpected_placeholder(source, piece) for source, piece in zip(sources, pieces, strict=True)):
            continue  # a made-up string from the model's training data: better no translation than an invented one
        outputs[index] = assemble(plan, pieces)
    return outputs


def _translate_alone(translator: SegmentTranslator, plan: TextPlan) -> str | None:
    try:
        translated = translator.translate(plan.segments)
        if len(translated) != len(plan.segments):
            return None
        if any(has_unexpected_placeholder(source, piece) for source, piece in zip(plan.segments, translated, strict=True)):
            return None
        return assemble(plan, translated)
    except Exception:  # noqa: BLE001 - see _run_hop
        return None


# Printf-style and brace placeholders. The Chinese models were trained on software-localisation data and, for some interface
# sentences, answer with an unrelated string such as "Could not close temporary folder: %s". A placeholder that the source
# text does not contain proves the output was not translated from it.
_PLACEHOLDER = re.compile(r"%(?:\d+\$)?[sdfxXoeEgGcu@]|\{\d+\}|\{[A-Za-z_]+\}")


def has_unexpected_placeholder(source: str, output: str) -> bool:
    """Whether ``output`` contains a placeholder that ``source`` does not (a hallucinated string, never a faithful translation)."""
    return bool(_PLACEHOLDER.search(output)) and not _PLACEHOLDER.search(source)
