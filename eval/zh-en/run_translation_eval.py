"""Chinese-to-English translation benchmark for the GlyphLens worker engine.

Runs the real worker translation service (CTranslate2 + SentencePiece, the same code the app uses) on
``dataset.json`` with the official Argos ``zh->en`` / ``zt->en`` packages, and records per item: the translation, the
latency, and two automatic scores against the reference translation (sentence BLEU and chrF, own simple implementations,
so no dependency). It also records load time, memory (working set of this process) and batch latency.

The automatic scores are only a rough aid: they punish valid paraphrases. Meaning, fluency and correctness are judged
by reading the output (see ``results/``); that judgement is the assistant's, not an independent human's.

Usage (from the repository root; the dependencies are in ``worker/.venv``)::

    worker\\.venv\\Scripts\\python.exe eval\\zh-en\\run_translation_eval.py ^
        --zh D:\\...\\translate-zh_en-1_9.argosmodel --zt D:\\...\\translate-zt_en-1_9.argosmodel --out eval\\zh-en\\results

Nothing is downloaded: the package files must already exist. The network guard of the worker is installed first, and the
run reports any attempt (there should be none).
"""

from __future__ import annotations

import argparse
import ctypes
import json
import math
import re
import statistics
import sys
import tempfile
import time
from collections import Counter
from ctypes import wintypes
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "worker"))

from glyphlens_worker.config.settings import ModelsLocation  # noqa: E402
from glyphlens_worker.engines.translation.service import TranslationService  # noqa: E402
from glyphlens_worker.runtime import network_guard  # noqa: E402


def working_set_mb() -> float:
    class Counters(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD), ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t), ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
        ]

    kernel32, psapi = ctypes.WinDLL("kernel32"), ctypes.WinDLL("psapi")
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
    psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
    counters = Counters()
    counters.cb = ctypes.sizeof(Counters)
    if not psapi.GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
        raise OSError("GetProcessMemoryInfo failed")
    return counters.WorkingSetSize / (1024 * 1024)


def _ngrams(tokens: list[str], n: int) -> Counter[tuple[str, ...]]:
    return Counter(tuple(tokens[i : i + n]) for i in range(len(tokens) - n + 1))


def sentence_bleu(hypothesis: str, reference: str) -> float:
    """Smoothed sentence BLEU-4 (add-one smoothing on n > 1) in [0, 100]."""
    hyp = re.findall(r"\w+|[^\w\s]", hypothesis.lower())
    ref = re.findall(r"\w+|[^\w\s]", reference.lower())
    if not hyp or not ref:
        return 0.0
    log_precision = 0.0
    for n in range(1, 5):
        hyp_counts, ref_counts = _ngrams(hyp, n), _ngrams(ref, n)
        overlap = sum(min(count, ref_counts[gram]) for gram, count in hyp_counts.items())
        total = max(sum(hyp_counts.values()), 0)
        if n == 1:
            if overlap == 0 or total == 0:
                return 0.0
            log_precision += math.log(overlap / total)
        else:
            log_precision += math.log((overlap + 1) / (total + 1))
    brevity = 1.0 if len(hyp) > len(ref) else math.exp(1 - len(ref) / len(hyp))
    return 100.0 * brevity * math.exp(log_precision / 4)


def chrf(hypothesis: str, reference: str, beta: float = 2.0) -> float:
    """Character n-gram F-score (chrF, n = 1..6, whitespace removed) in [0, 100]."""
    hyp = re.sub(r"\s+", "", hypothesis.lower())
    ref = re.sub(r"\s+", "", reference.lower())
    if not hyp or not ref:
        return 0.0
    scores = []
    for n in range(1, 7):
        hyp_counts = Counter(hyp[i : i + n] for i in range(len(hyp) - n + 1))
        ref_counts = Counter(ref[i : i + n] for i in range(len(ref) - n + 1))
        overlap = sum(min(count, ref_counts[gram]) for gram, count in hyp_counts.items())
        precision = overlap / max(sum(hyp_counts.values()), 1)
        recall = overlap / max(sum(ref_counts.values()), 1)
        if precision + recall > 0:
            scores.append((1 + beta**2) * precision * recall / (beta**2 * precision + recall))
        else:
            scores.append(0.0)
    return 100.0 * sum(scores) / len(scores)


def translate_all(service: TranslationService, source: str, texts: list[tuple[str, str]]) -> tuple[dict[str, dict], float]:
    """One request per text (the app's worst case), returning per-id results and the total time."""
    results: dict[str, dict] = {}
    started = time.perf_counter()
    for item_id, text in texts:
        t0 = time.perf_counter()
        reply = service.translate({"targetLanguage": "en", "items": [{"id": item_id, "text": text, "sourceLanguage": source}]})
        elapsed = (time.perf_counter() - t0) * 1000
        item = reply["results"][0]
        results[item_id] = {
            "output": item.get("translatedText", ""),
            "status": item["status"],
            "ms": round(elapsed, 1),
            "error": item.get("error", {}).get("code"),
            "modelVersion": item.get("modelVersion"),
        }
    return results, (time.perf_counter() - started) * 1000


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--zh", required=True, type=Path, help="translate-zh_en .argosmodel")
    parser.add_argument("--zt", required=True, type=Path, help="translate-zt_en .argosmodel")
    parser.add_argument("--dataset", type=Path, default=Path(__file__).with_name("dataset.json"))
    parser.add_argument("--out", type=Path, default=Path(__file__).with_name("results"))
    args = parser.parse_args()

    network_guard.install()
    dataset = json.loads(args.dataset.read_text(encoding="utf-8"))["items"]
    args.out.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="glyphlens-eval-") as temp:
        models = Path(temp) / "models"
        service = TranslationService(ModelsLocation(models))
        rss_before = working_set_mb()
        for label, archive in (("zh", args.zh), ("zt", args.zt)):
            t0 = time.perf_counter()
            installed = service.install({"path": str(archive)})
            print(f"imported {label}: {installed['package']['packageName']} v{installed['package']['version']} in {time.perf_counter() - t0:.1f}s")

        report: dict = {"python": sys.version.split()[0], "memory_mb": {"before_models": round(rss_before, 1)}}
        sections = []
        for label, field, source in (("simplified", "zh", "zh"), ("traditional", "zt", "zt")):
            texts = [(item["id"], item[field]) for item in dataset if field in item]
            t0 = time.perf_counter()
            service.translate({"targetLanguage": "en", "items": [{"id": "warm", "text": texts[0][1], "sourceLanguage": source}]})
            report.setdefault("load_and_first_translation_ms", {})[label] = round((time.perf_counter() - t0) * 1000, 1)
            report["memory_mb"][f"after_loading_{label}"] = round(working_set_mb(), 1)
            results, total_ms = translate_all(service, source, texts)

            # The same texts as ONE request (what a screenful of OCR lines costs when batched).
            t0 = time.perf_counter()
            batch = service.translate(
                {"targetLanguage": "en", "items": [{"id": item_id, "text": text, "sourceLanguage": source} for item_id, text in texts[:32]]}
            )
            batch_ms = (time.perf_counter() - t0) * 1000
            same = sum(1 for r in batch["results"] if r.get("translatedText") == results[r["id"]]["output"])

            rows = []
            for item in dataset:
                if field not in item:
                    continue
                result = results[item["id"]]
                rows.append(
                    {
                        "id": item["id"], "category": item["category"], "source": item[field], "reference": item["en"],
                        "output": result["output"], "status": result["status"], "ms": result["ms"], "error": result["error"],
                        "bleu": round(sentence_bleu(result["output"], item["en"]), 1),
                        "chrf": round(chrf(result["output"], item["en"]), 1),
                    }
                )
            latencies = [r["ms"] for r in rows]
            by_category: dict[str, list[float]] = {}
            for r in rows:
                by_category.setdefault(r["category"], []).append(r["chrf"])
            sections.append(
                {
                    "label": label,
                    "items": len(rows),
                    "translated": sum(1 for r in rows if r["status"] == "ok"),
                    "latency_ms": {"median": round(statistics.median(latencies), 1), "p95": round(sorted(latencies)[int(len(latencies) * 0.95) - 1], 1), "max": max(latencies)},
                    "one_request_per_item_total_ms": round(total_ms, 1),
                    "one_batch_of_32_ms": round(batch_ms, 1),
                    "batch_equals_single_requests": f"{same}/{len(batch['results'])}",
                    "mean_bleu": round(statistics.mean(r["bleu"] for r in rows), 1),
                    "mean_chrf": round(statistics.mean(r["chrf"] for r in rows), 1),
                    "chrf_by_category": {k: round(statistics.mean(v), 1) for k, v in by_category.items()},
                    "rows": rows,
                }
            )
        report["memory_mb"]["after_both"] = round(working_set_mb(), 1)
        report["sections"] = sections
        report["network_attempts"] = list(network_guard.blocked_attempts())

    (args.out / "translation-eval.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    lines = ["# Chinese-to-English translation results (generated)", ""]
    for section in sections:
        lines += [f"## {section['label']} ({section['items']} items)", ""]
        lines += [f"median {section['latency_ms']['median']} ms, p95 {section['latency_ms']['p95']} ms per single request; 32 items in one batch: {section['one_batch_of_32_ms']} ms; mean BLEU {section['mean_bleu']}, mean chrF {section['mean_chrf']}", ""]
        lines += ["| id | source | output | reference | chrF | ms |", "|---|---|---|---|---|---|"]
        for r in section["rows"]:
            cells = [r["id"], r["source"], r["output"], r["reference"], str(r["chrf"]), str(r["ms"])]
            lines.append("| " + " | ".join(c.replace("|", "/").replace("\n", " ") for c in cells) + " |")
        lines.append("")
    (args.out / "translation-eval.md").write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "sections"}, indent=1))
    for section in sections:
        print(section["label"], {k: v for k, v in section.items() if k not in ("rows",)})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
