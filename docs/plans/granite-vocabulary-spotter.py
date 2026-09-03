"""Can the vocabulary spotter ride Granite's own CTC head instead of Parakeet's?

If it can, English users stop downloading a separate 132 MB checkpoint and the
post-stop spotting pass stops being a second forward pass over the whole recording.

This reproduces `CtcSpotterTests.M2_ThresholdSeparatesPlantedTermsFromDecoys`
against Granite: the same six planted terms, the same clip that contains all of
them, the same four decoy clips that contain none. The shipped threshold (-3.0,
mean log-prob per token) was DERIVED on parakeet-tdt_ctc-110m's 1025-token
SentencePiece head. Granite's head is 16,384 byte-level BPE pieces, so the score
scale cannot be assumed to transfer -- that is the question here.

Parakeet's measured bands, for comparison:
    planted  -0.038 -0.313 -0.363 -0.415 -0.830 -1.313   (median -0.363, 6/6 found)
    decoys   -5.679 ... -13.149                           (best false candidate -5.68)
    gap      4.35 nats

Two things differ from the Parakeet setup and both simplify the query set:
  * Granite emits LOWERCASE, UNPUNCTUATED text. Parakeet is a punctuation-and-
    capitalisation model whose ids change with casing, which is the entire reason
    CtcSpotForms searches five casings. Here only the lowercase form can ever match.
  * Byte-level BPE encodes a leading space into the token ("Ġnem"), so a word
    mid-sentence and the same word utterance-initial are DIFFERENT id sequences.
    Both are searched; that replaces the casing fan-out.
"""
import json
from pathlib import Path

import numpy as np
import onnxruntime as ort
import soundfile as sf
import torch
from transformers import AutoProcessor, AutoTokenizer

HERE = Path(__file__).parent
AUDIO = Path(r"D:\caches\jot-ctc-spike\audio")
BLANK = 0
FRAME_SECONDS = 0.08          # 12.5 Hz logits, as measured in the export parity run

PROC = AutoProcessor.from_pretrained(HERE / "models" / "granite", trust_remote_code=True)
TOK = AutoTokenizer.from_pretrained(HERE / "models" / "granite")

# The planted set from CtcSpotterTests, verbatim. tts-terms.wav says all six, three
# of them MIS-spelled ("Sri Ram", "Octa", "Claud code") -- the term list carries the
# right spelling and the audio does not, which is the whole point of the feature.
TERMS = {
    "Nemotron": [],
    "Parakeet": [],
    "Sriram": ["Sri Ram"],
    "Okta": ["Octa"],
    "Claude Code": ["Claud code"],
    "Thursday": [],
}
DECOYS = ["public-0.wav", "real-40s.wav", "real-90s.wav", "seg-22-60.wav"]


def log_softmax(x: np.ndarray) -> np.ndarray:
    m = x.max(axis=-1, keepdims=True)
    z = x - m
    return z - np.log(np.exp(z).sum(axis=-1, keepdims=True))


def logprobs(sess, audio: np.ndarray) -> np.ndarray:
    f = PROC([audio.astype(np.float32)], sampling_rate=16000)["input_features"]
    f = (f if torch.is_tensor(f) else torch.tensor(f)).numpy()
    return log_softmax(sess.run(None, {"input_features": f})[0][0])


def forms(surface: str):
    """The id sequences worth searching for one surface form.

    Lowercase only (the head never emits anything else), with and without the
    leading-space marker, deduplicated.
    """
    out, seen = [], set()
    for text in (" " + surface.lower(), surface.lower()):
        ids = tuple(TOK.encode(text, add_special_tokens=False))
        if ids and ids not in seen:
            seen.add(ids)
            out.append((text, list(ids)))
    return out


def spot(lp: np.ndarray, ids: list):
    """Best normalized score for this id sequence anywhere in the clip.

    Viterbi over the standard CTC extended state sequence y0 _ y1 _ ... y(L-1)
    (even = label, odd = blank), with a FREE START: state 0 may begin at any frame,
    so this scores the best occurrence rather than forcing the term to span the clip.
    Accepting state is the last LABEL, not the trailing blank -- reaching the blank
    would only add its cost to an already-complete match.

    Returns (score, start_frame, end_frame); score is raw path log-prob divided by
    token count, i.e. mean log-prob per token, which is the scale -3.0 lives on.
    """
    L = len(ids)
    ext = []
    for i, y in enumerate(ids):
        if i:
            ext.append(BLANK)
        ext.append(y)
    S = len(ext)

    NEG = -np.inf
    V = np.full(S, NEG)
    start = np.zeros(S, dtype=np.int64)
    best = (NEG, 0, 0)

    for t in range(lp.shape[0]):
        row = lp[t]
        prevV, prevStart = V.copy(), start.copy()
        for s in range(S):
            # stay in s
            cand, src = prevV[s], prevStart[s]
            if s > 0 and prevV[s - 1] > cand:
                cand, src = prevV[s - 1], prevStart[s - 1]
            # skip the blank between two DIFFERENT labels
            if s > 1 and ext[s] != BLANK and ext[s] != ext[s - 2] and prevV[s - 2] > cand:
                cand, src = prevV[s - 2], prevStart[s - 2]
            if s == 0 and cand < 0.0:          # free start: a match may begin at t
                cand, src = 0.0, t
            V[s] = cand + row[ext[s]] if cand != NEG else NEG
            start[s] = src
        if V[S - 1] != NEG:
            score = V[S - 1] / L
            if score > best[0]:
                best = (score, int(start[S - 1]), t)
    return best


def best_for_term(lp: np.ndarray, term: str, aliases: list):
    """Best score across the term and its aliases, and across both space forms."""
    best = (-np.inf, 0, 0, "")
    for surface in [term, *aliases]:
        for text, ids in forms(surface):
            score, a, b = spot(lp, ids)
            if score > best[0]:
                best = (score, a, b, f"{text!r} -> {len(ids)} tok")
    return best


def main() -> None:
    sess = ort.InferenceSession(str(HERE / "onnx" / "granite_ctc.int8pc.onnx"),
                                ort.SessionOptions(), providers=["CPUExecutionProvider"])

    planted_wav = AUDIO / "tts-terms.wav"
    audio, sr = sf.read(planted_wav, dtype="float32")
    assert sr == 16000
    if audio.ndim > 1:
        audio = audio.mean(axis=1)
    lp = logprobs(sess, audio)
    print(f"tts-terms.wav: {len(audio)/sr:.1f}s -> {lp.shape[0]} frames, "
          f"vocab {lp.shape[1]}\n")

    # Self-check on the DP before trusting any band: words the model actually emitted
    # must score near 0, because they lie on the greedy path by construction. If these
    # are not near 0 the search is broken and every number below is meaningless.
    ids = lp.argmax(-1)
    collapsed, prev = [], -1
    for i in ids:
        if i != prev and i != BLANK:
            collapsed.append(int(i))
        prev = int(i)
    hyp = TOK.decode(collapsed)
    print(f"greedy: {hyp!r}\n")
    print("--- DP self-check: words the model DID emit ---")
    for w in hyp.split()[:5]:
        s, a, b = spot(lp, TOK.encode(" " + w, add_special_tokens=False))
        print(f"  {w:<14} {s:8.3f}  {a*FRAME_SECONDS:5.2f}s-{b*FRAME_SECONDS:5.2f}s")

    print("\n--- planted (tts-terms.wav) ---")
    hits = []
    for term, aliases in TERMS.items():
        s, a, b, how = best_for_term(lp, term, aliases)
        hits.append(s)
        print(f"  {term:<14} {s:8.3f}  {a*FRAME_SECONDS:5.2f}s-{b*FRAME_SECONDS:5.2f}s  [{how}]")

    print("\n--- decoys (clips containing none of the terms) ---")
    decoys = []
    for name in DECOYS:
        wav = AUDIO / name
        if not wav.exists():
            print(f"  {name}: MISSING")
            continue
        a2, sr2 = sf.read(wav, dtype="float32")
        if a2.ndim > 1:
            a2 = a2.mean(axis=1)
        assert sr2 == 16000
        lp2 = logprobs(sess, a2)
        for term, aliases in TERMS.items():
            s, _, _, _ = best_for_term(lp2, term, aliases)
            decoys.append(s)
            print(f"  {name:<16} {term:<14} {s:8.3f}")

    print("\n--- planted at -12 dB (gain 0.25) ---")
    lpq = logprobs(sess, audio * 0.25)
    quiet = [best_for_term(lpq, t, a)[0] for t, a in TERMS.items()]
    print(f"  worst {min(quiet):.3f}   all: {' '.join(f'{q:.3f}' for q in quiet)}")

    worst_hit, best_decoy = min(hits), max(decoys)
    print(f"\nplanted: {len(hits)} terms, {worst_hit:.3f} … {max(hits):.3f} "
          f"(median {float(np.median(hits)):.3f})")
    print(f"decoys : {len(decoys)} scores, best {best_decoy:.3f}")
    print(f"gap    : {worst_hit - best_decoy:.3f} nats "
          f"(Parakeet measured 4.35, shipped threshold -3.0)")

    json.dump({"planted": hits, "decoys": decoys, "quiet": quiet},
              open(HERE / "spot_granite_results.json", "w"), indent=1)


if __name__ == "__main__":
    main()
