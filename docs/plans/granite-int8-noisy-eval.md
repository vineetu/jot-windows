# Granite int8 under noise and reverberation

**2026-09-03. Verdict: the int8 per-channel export ships. Confirmed on hard audio, not just clean.**

## Why this was run

The decision to ship a quantized English engine rested on 30 clean, close-mic,
read-speech utterances, where int8-per-channel drifted from fp32 by 0.18% — one
differing sentence. That is the easiest audio a dictation app ever sees.
Quantization damage is known to surface first at low SNR and under reverberation,
so the clean number could not settle the question it was being used to settle.

The rig is `D:\granite-spike\eval_noisy.py` (not in this repo — it needs the fp32
graph, 1.9 GB).

## Method

All 73 LibriSpeech-dummy validation utterances (481 s), degraded seven ways, with
the reference text shared across conditions. Babble is built from the corpus's own
speakers (8 summed at random offsets) rather than synthetic hiss, because ASR
front-ends fail differently against speech-shaped interference. Reverberation is a
simulated 6×5×3 m room (image-source, `pyroomacoustics`), talker ~2 m from the mic;
in the combined condition the babble is convolved with a RIR from a *different*
source position, since interfering talkers are in the room too.

The number that decides is **drift** — int8 against fp32 *on the same degraded
audio*. Absolute WER rises in noise for both builds because the task got harder;
that says nothing about quantization. Drift isolates the damage the swap itself does.

`int8 naive` is carried as a **negative control**. It is independently known-bad, so
a rig that reports it as harmless is broken.

## Results

| condition | fp32 WER | int8pc WER | int8pc drift | naive WER | naive drift |
|---|---|---|---|---|---|
| clean | 6.17% | 6.26% | 0.34% | 6.35% | 1.37% |
| babble 20 dB | 6.61% | 6.70% | 0.43% | 7.91% | 2.06% |
| babble 10 dB | 8.00% | 8.09% | 0.69% | 9.48% | 3.71% |
| babble 5 dB | 15.30% | 15.83% | 1.12% | 21.22% | 14.75% |
| pink 10 dB | 6.17% | 6.35% | 0.34% | 8.61% | 3.25% |
| far, T60 0.4 s | 7.04% | 7.13% | 0.86% | 8.17% | 3.00% |
| far 0.7 s + babble 15 dB | 12.00% | 12.09% | 1.63% | 17.39% | 11.30% |

Speed was stable throughout: int8pc ~25× RT, fp32 ~21×, naive ~15×.

## What it means

**int8pc holds.** Its WER cost over fp32 is +0.09 to +0.53 points in every
condition, including the two hardest. Drift does grow with difficulty (0.34% →
1.63%), so quantization damage is real and does scale with acoustic stress — but at
this magnitude it is well under the run-to-run variation a user would notice, and it
never turns into word loss the way the naive export does.

**The clean-only eval understated the negative control by an order of magnitude.**
Naive int8 measured 1.44% drift on clean speech; at 5 dB babble it is 14.75%, and it
loses 5.9 WER points to fp32. Had the naive export been shipped on the strength of a
clean-set measurement, it would have degraded hardest exactly where dictation is
already hardest — a busy room. This is the finding that generalizes: **a clean-set
drift number is not evidence about noisy behaviour, for any future quantization.**
Re-run this eval, not just `eval_variants.py`, before changing the export.

## Limits of this evidence

Still LibriSpeech read speech with *simulated* degradation — real far-field capture
adds mic response, AGC, and codec artifacts none of this models. SNR is defined on
full-clip RMS rather than active speech, so the dB labels are nominal. And nothing
here is spontaneous dictation, which is the actual workload.
