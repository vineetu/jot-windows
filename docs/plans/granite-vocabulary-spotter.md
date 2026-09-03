# Riding Granite's CTC head for the vocabulary spotter

**2026-09-03. Measured: GO, and by a wider margin than the Parakeet spotter it would replace.**

Spike: `D:\granite-spike\spot_granite.py` (copy committed alongside this file).

## The idea

Acoustic vocabulary spotting is English-only (`VocabularyRunner.ModeFor` answers
`Acoustic` for `en` and `Textual` for everything else), and it currently costs a
separate 132 MB Parakeet-CTC download plus a second forward pass over the whole
recording after every dictation. Granite Speech 5.0 — already downloaded by anyone
using the English engine — is *also* a CTC model with per-frame log-probs. So for
those users the spotter could ride the model that is already there.

`CtcWordSpotter` is already model-agnostic: it takes a blank id, a frame duration, a
`[frames, vocab]` log-prob matrix and token-id queries. Granite is blank id 0 at
12.5 Hz — the same frame rate as Parakeet — so the DP itself needs no change.

## Measurement

Same protocol as `CtcSpotterTests.M2_ThresholdSeparatesPlantedTermsFromDecoys`: the
six planted terms, `tts-terms.wav` which says all six (three of them mis-spelled, so
the audio carries "Sri Ram" / "Octa" / "Claud code" while the term list carries the
right spelling), and four decoy clips containing none of them.

|  | Parakeet (shipped) | Granite (measured) |
|---|---|---|
| planted, worst → best | -1.313 → -0.038 | **-0.693 → -0.001** |
| planted median | -0.363 | **-0.121** |
| terms found | 6/6 | **6/6** |
| strongest false candidate | -5.679 | **-9.240** |
| **separation** | **4.35 nats** | **8.55 nats** |
| worst planted at -12 dB | -1.325 | **-0.665** |

**The shipped -3.0 threshold transfers unchanged**, with roughly twice the margin on
either side: 2.3 nats below the worst true hit, 6.2 nats above the strongest decoy.
It is also *more* robust to recording level, not less.

The DP was self-checked before any band was trusted: words the model demonstrably
emitted score -0.000, because they lie on the greedy path by construction. A search
that cannot reproduce that is broken and every other number would be meaningless.

Why it separates better: a 16,384-piece byte-level BPE head spells terms in far
fewer tokens than Parakeet's 1,025-piece SentencePiece head ("thursday" is a SINGLE
token), so a true hit accumulates less per-token penalty — while a sequence the model
did not say is penalised across 16× more competing classes.

## Two things get simpler, one gets harder

**Simpler — casing.** `CtcSpotForms` searches five casings because Parakeet is a
punctuation-and-capitalisation model whose ids change with case (measured: 13 of 13
English terms). Granite emits lowercase, unpunctuated text only, so just the
lowercase form can ever match. The fan-out is replaced by a smaller one: byte-level
BPE encodes a leading space into the token, so " nemotron" and "nemotron" are
different id sequences and both must be searched.

**Harder — encoding.** The DP needs terms as *token ids*, and Jot ships only
Granite's `vocab.json`: a flat id→piece list with **no merges**. Text cannot be
BPE-encoded from a vocabulary alone. Upstream `tokenizer.json` carries the 16,127
merges in 1.1 MB — trivial next to the 132 MB it saves, but it is a new asset on the
model release, not something already on disk. The numbers above were measured with
the real BPE encoder, so they describe the ship-`tokenizer.json` design specifically.

A merge-free alternative exists — search *every* valid segmentation of the term with
a DAG-shaped DP instead of one canonical tokenization — which would need no new asset
and would be strictly more robust to the model preferring an unusual split. It is
unmeasured, and a bigger change to `CtcWordSpotter`. Recorded as the fallback if
re-cutting the release turns out to be unattractive.

## Constraints for the implementation

- **The session must be shared, not created twice.** `OnnxSessionFactory` does not
  cache, so a spotter that opened its own session would put a second 536 MB graph in
  memory next to the transcriber's. The spotter has to obtain log-probs from the same
  Granite session the engine already holds.
- **Parakeet cannot be deleted.** The English engine is an optional download, so
  English users without it still need the 132 MB spotter. The win is that users who
  have the English engine stop paying twice (712 + 132 MB today).
- **Memory per pass is ~16× Parakeet's.** 120 s of logits is 1500 × 16,384 × 4 B ≈
  98 MB against Parakeet's ~6 MB. The DP reads frames strictly front-to-back, so
  log-softmax can be applied per row instead of materialising a second copy.
