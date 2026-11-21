"""Finding which installed packages translate a language pair.

Every installed package is an edge ``from -> to``. A route is a direct edge, else the shortest path of at most two
hops (``de -> en -> fr`` when both packages are installed; the English text of the first hop is the input of the
second). Longer routes are not offered: each hop doubles the latency and compounds the quality loss.

A Portuguese request may use a Brazilian package and the reverse, and likewise Simplified and Traditional Chinese
(``VARIANT_ALIASES``). An exact package is preferred over a variant, a shorter route over a longer one.

When there is no route the answer names a package to install. The worker has no network, so the name is a suggestion
(``translate-<from>_<to>``) it cannot confirm exists. Official packages translate into or out of English, so for a
pair that involves English the suggestion is that pair, and otherwise the first missing hop through English.
"""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass
from itertools import product

from glyphlens_worker.languages import ARGOS_LANGUAGES, VARIANT_ALIASES, package_name
from glyphlens_worker.models.registry import InstalledPackage

#: Routes of two hops pass through any language that has both packages; English is tried first.
PIVOT_LANGUAGE = "en"


@dataclass(frozen=True)
class Route:
    """The packages to run, in order."""

    packages: tuple[InstalledPackage, ...]

    @property
    def languages(self) -> list[str]:
        """The languages the text passes through, for the wire ``route`` field."""
        return [self.packages[0].source, *(package.target for package in self.packages)]

    @property
    def version_tag(self) -> str:
        """The packages part of the model version string: one ``argos:...`` tag per hop, joined by ``>``."""
        return ">".join(package.version_tag for package in self.packages)


def is_supported_pair(source: str, target: str, installed: Iterable[InstalledPackage] = ()) -> bool:
    """Whether the pair names two different languages the engine knows (Argos' catalogue or an installed package)."""
    if source == target:
        return False
    known = set(ARGOS_LANGUAGES)
    for package in installed:
        known.update((package.source, package.target))
    return source in known and target in known


def find_route(source: str, target: str, installed: Iterable[InstalledPackage]) -> Route | None:
    """The best route from ``source`` to ``target`` over ``installed``, or ``None``."""
    edges: dict[tuple[str, str], InstalledPackage] = {(p.source, p.target): p for p in installed}
    pairs = list(product(_with_variant(source), _with_variant(target)))

    for start, end in pairs:  # one hop, exact pair first
        if (start, end) in edges:
            return Route((edges[(start, end)],))

    for start, end in pairs:  # two hops
        for middle in _middles(edges, start, end):
            return Route((edges[(start, middle)], edges[(middle, end)]))
    return None


def missing_package(source: str, target: str, installed: Iterable[InstalledPackage]) -> str:
    """The conventional name of the package to install so that ``source -> target`` works (see the module notes)."""
    if PIVOT_LANGUAGE in (source, target):
        return package_name(source, target)
    first_hop_installed = find_route(source, PIVOT_LANGUAGE, installed) is not None
    return package_name(PIVOT_LANGUAGE, target) if first_hop_installed else package_name(source, PIVOT_LANGUAGE)


def _with_variant(code: str) -> list[str]:
    variant = VARIANT_ALIASES.get(code)
    return [code] if variant is None else [code, variant]


def _middles(edges: dict[tuple[str, str], InstalledPackage], start: str, end: str) -> list[str]:
    """Intermediate languages of two-hop routes ``start -> middle -> end``, English first, then alphabetically."""
    candidates = {
        middle
        for (first, middle) in edges
        if first == start and middle not in (start, end) and (middle, end) in edges
    }
    return sorted(candidates, key=lambda code: (code != PIVOT_LANGUAGE, code))
