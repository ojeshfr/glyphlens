# Reading of the Simplified Chinese to English outputs (assistant's judgement, not a human review)

Who judged: the assistant that built GlyphLens read each of the 52 outputs of `translation-eval.md` against the Chinese source and the
reference translation it had written. **This is not an independent human evaluation**, the reference translations themselves were
written by the same assistant, and a single reader's categories are coarse. Treat the counts as a description of what the output looks
like, not as a quality score. The automatic scores in `translation-eval.json` (sentence BLEU, chrF) are a separate, mechanical aid and
punish valid paraphrases.

Categories: **correct** (meaning and wording fine), **acceptable** (the gist is right, minor wording or detail issues),
**partial** (part of the meaning is lost or the English is broken), **wrong** (the meaning is wrong), **rejected** (the worker refused a
hallucinated output; the user sees "could not be translated", never a made-up sentence).

| Verdict | Count | Items |
|---|---|---|
| correct | 16 | conv-01, conv-03, conv-04, chat-01, chat-04, chat-05, chat-07, chat-08, q-03, mix-01, mix-03, num-01, long-01, ui-01, ui-02, ui-05 |
| acceptable | 19 | conv-02, conv-06, conv-07, conv-08, chat-02, chat-03, chat-06, inf-01, inf-04, inf-05, slang-04, idiom-02, idiom-05, q-04, sar-02, num-03, num-04, ui-03, ui-06 |
| partial | 11 | conv-05 (lost "but"), inf-02 (lost "dying of laughter"), inf-03 (lost "overtime"), slang-02 (lost "emo"), idiom-04, q-01 (lost "how do I get"), q-02, sar-01 (broken sentence), mix-02 (ungrammatical), num-02 (name "Zhang Wai", odd clause), long-02 (lost "with a friend") |
| wrong | 5 | slang-01 ("anchor ... rolling"), slang-03, idiom-01 (literal fishing), idiom-03, mix-04 ("Wireless payments" for WeChat Pay) |
| rejected (hallucination) | 1 | ui-04 |

## What this says

* Ordinary conversation, short chat, questions, numbers, mixed Chinese-English text and interface words are mostly understandable;
  Latin words, numbers, usernames, URLs and currency signs survive.
* Idioms, slang, sarcasm and informal speech are weak: the model translates literally or drops what it does not know.
* **A hallucination was observed.** For the interface sentence `网络连接失败，请检查您的网络设置。` the model answered
  "Could not close temporary folder: %s" (a software-localisation string from its training data). The worker now rejects any output that
  contains a placeholder (`%s`, `%d`, `{0}`, ...) that the source does not contain, so such a sentence shows as "could not be
  translated". This guard catches this one family only; other hallucinations cannot be detected this way.
* The Traditional Chinese package contains the **same weights** as the Simplified one (byte-identical `model.bin`, tokenizer and
  vocabulary), so Traditional behaves the same way; its automatic scores are about 2-4 points lower on this set, which is within what a
  different set of sentences could change. Two Traditional items were rejected by the guard (ui-03, ui-04).
* No alternative model was evaluated, so nothing here says Argos is better or worse than another offline model.
