# Offline Chinese to English translation: alternatives to the current Argos model

- **Date:** 2026-11-21. **Status:** desk research only. **No alternative was run on GlyphLens' evaluation set and the production model was not changed.**
- **How this was produced:** a read-only research agent read the official model cards, licence files and papers listed below and reported
  the facts. The lead (the assistant that built GlyphLens) did **not** re-open every page; items the agent could not confirm are marked
  "not verified". Every judgement is **provisional** and is not an independent human evaluation. Vendor-reported scores are the vendors' own.
- **Requirement:** CPU only (reference machine: i7-12700H, 16 GB RAM, no usable discrete GPU), fully offline in the local worker, and the licence
  must allow shipping or installing the weights with a commercial desktop app.

## Current model (for comparison)

Argos Translate `zh->en` and `zt->en` packages: Marian/Transformer from the OPUS-MT family, SentencePiece, about 75 MB (int8) run with CTranslate2,
CC-BY 4.0. Measured locally (52 sentences written by the assistant, not by an independent reviewer): gist-level quality, 0.2-1 s per request on CPU,
see [CHINESE-FIRST-EVALUATION.md](CHINESE-FIRST-EVALUATION.md). Whether the Argos weights are derived from `Helsinki-NLP/opus-mt-zh-en` is **not verified**.

## Candidates

| Model | Licence (as stated on its card / licence file) | CPU form and size | How it runs on Windows CPU | zh to en evidence found | Commercial redistribution |
|---|---|---|---|---|---|
| **Tencent Hy-MT2-1.8B** (card says released 2026-05-21), `huggingface.co/tencent/Hy-MT2-1.8B` | Apache-2.0 | official GGUF: Q4_K_M 1.13 GB, Q6_K 1.47 GB, Q8_0 1.91 GB; a 1.25-bit build about 440 MB | llama.cpp / llama-cpp-python (MIT; builds from source on Windows). The GGUF card says the files "depend on our STQ kernel" (a llama.cpp PR): **whether stock llama.cpp loads the Q4_K_M file is not verified**. CTranslate2 has no Hunyuan-dense converter (not verified) | Tencent's own FLORES-200 ZH<->XX average (both directions combined, not zh to en alone): XCOMET-XXL 84.26 vs Microsoft Translator 82.95 on the same table; Q4_K_M 82.22 vs BF16 83.49. The paper says Q4_K_M hurts instruction following | Yes (Apache-2.0) |
| **Qwen3-1.7B / Qwen3-4B** (general instruct LLM, not translation-tuned) | Apache-2.0 | official GGUF: 1.7B Q8_0 1.83 GB; 4B Q4_K_M 2.5 GB, Q8_0 4.28 GB | llama.cpp; CTranslate2 lists Qwen 2.5/3 dense models as supported (`ct2-transformers-converter`); needs the chat template, thinking mode off (`enable_thinking=False`) | **none verified** (the report gives no MT metrics in the parts read). Risk: commentary or invented text on very short UI fragments | Yes |
| **Helsinki-NLP/opus-mt-zh-en** (same family as the current model) | CC-BY-4.0 | `pytorch_model.bin` 312 MB fp32; int8 about 80 MB (agent's arithmetic) | CTranslate2 `ct2-opus-mt-converter` (documented) | Tatoeba-test BLEU 36.1, chrF 0.548 (model card); short sentences only | Yes, with attribution |
| **MADLAD-400 3B-mt** (Google) | Apache-2.0 | original 11.8 GB; a Q4K GGUF of 1.65 GB exists but for the Candle runtime | CTranslate2 README lists MADLAD-400 as supported; quantised CTranslate2 files not checked | **none verified**; the card says not assessed for production use | Yes |
| Tencent HY-MT1.5-1.8B (earlier release) | "Tencent HY Community Licence": territory excludes the EU, UK and South Korea; licensees above 100M monthly users need a separate licence; the model or its outputs may not be used "to improve any other AI model" | GGUF Q4_K_M 1.13 GB | llama.cpp | vendor: FLORES ZH<->XX XCOMET-XXL 0.8361; human ZH to EN 3.01 / 4 (no annotator details) | **Restricted**: do not ship without legal review |
| NLLB-200-distilled-600M | **CC-BY-NC-4.0**; the card says it is "not released for production deployment" | not checked | CTranslate2 | not checked | **No** (non-commercial) |
| Qwen2.5-3B-Instruct | `qwen-research` licence (not Apache-2.0) | not checked | llama.cpp | not checked | Excluded; the 1.5B sibling is Apache-2.0 (GGUF Q4_K_M 1.12 GB) |
| M2M100 418M | MIT | not checked | CTranslate2 | card gives no quality evidence | Yes, but not researched further |

Primary sources used by the agent: the Hugging Face model cards and licence files of the models above (`tencent/Hy-MT2-1.8B`, `tencent/Hy-MT2-1.8B-GGUF`,
`tencent/HY-MT1.5-1.8B` and its `License.txt`, `Qwen/Qwen3-1.7B`, `Qwen/Qwen3-4B` and their GGUF repositories, `Helsinki-NLP/opus-mt-zh-en`,
`google/madlad400-3b-mt`, `facebook/nllb-200-distilled-600M`, `facebook/m2m100_418M`), the Tatoeba-Challenge `zho-eng` results page, the CTranslate2
documentation (supported models, OPUS-MT converter) and the papers arXiv 2605.22064 (Hy-MT2), 2512.24092 (HY-MT1.5), 2505.09388 (Qwen3) and 2309.04662 (MADLAD-400).

## What is **not** known (do not infer it)

- No CPU speed or memory figures exist in primary sources for the LLM candidates on this class of machine; the only vendor speed numbers are for
  other hardware (an Apple A15, and an unspecified machine). They must be measured locally.
- No candidate was evaluated here. The vendor scores are on FLORES-200 with their own metrics and say little about chat bubbles and UI strings.
- How any of the LLM candidates behaves on one- or two-word fragments (the common case in a chat window) is not documented.

## Provisional recommendation (to be replaced by local evidence)

1. **First to try: Hy-MT2-1.8B (Q4_K_M or Q6_K)** - translation-specific, Apache-2.0, small enough for 16 GB. Prerequisite: confirm the GGUF loads in a stock
   llama.cpp or llama-cpp-python build (the "STQ kernel" note) and measure latency on this machine.
2. **Then Qwen3-1.7B and Qwen3-4B** in non-thinking mode (and Qwen2.5-1.5B-Instruct as a cheap extra point), watching verbosity and invented text on short fragments.
3. **A plain `opus-mt-zh-en` conversion** only as a sanity check against Argos (probably the same quality class).
4. MADLAD-400 3B only if time permits (no zh to en evidence found, heaviest).
5. Do not ship HY-MT1.5 or NLLB without legal review.

## Evaluation plan (not done)

Run each candidate through `eval/zh-en/run_translation_eval.py`-style harness on `eval/zh-en/dataset.json` (52 sentences, plus the OCR-style
fragments of `ocr-scenes.json`), recording per-request latency, peak memory and the outputs side by side with Argos; have a **human** who reads
Chinese judge them (the existing reference translations were written by the assistant). Only replace or complement the Argos engine with such evidence.
The runtime would also change (llama.cpp instead of CTranslate2 for the LLMs), which needs its own review under ADR-0006 (no network at run time,
no automatic downloads, models outside the repository).
