"""Does int8 quantization degrade FASTER than fp32 when the audio gets hard?

The ship/no-ship verdict for the English engine rests on `eval_variants.py`, which
measured 30 clean, close-mic, read-speech utterances. That is the easiest audio a
dictation app will ever see, and quantization damage is known to surface first at
low SNR and under reverberation — exactly the conditions the clean set cannot
show. A 0.18% drift on clean speech is not evidence of a 0.18% drift in a kitchen.

So: the same utterances, degraded seven ways, fp32 vs int8-per-channel measured on
each.

The number that decides is DRIFT — int8 against fp32 on the SAME degraded audio —
not WER. Absolute WER rises in noise for both builds because the task got harder;
that says nothing about quantization. Drift isolates the damage the swap itself
does. `int8 naive` is carried as a NEGATIVE CONTROL: it is known-bad (4.74% vs
3.83% on clean), so if this rig reports it as harmless the rig is broken, not the
quantizer.

Conditions, all built from the same 73 clean clips so references are shared:
  clean                  the eval_variants baseline, re-run here for comparability
  babble 20/10/5 dB      multi-talker babble, the dictation-relevant noise; built
                         from the corpus's own speakers (8 summed, offset) rather
                         than a synthetic hiss, because ASR front-ends fail
                         differently against speech-shaped interference
  pink 10 dB             one stationary noise (fan / HVAC / room tone)
  far 0.4s               reverberation only: a small office, mic ~2 m away
  far 0.7s + babble 15dB the realistic bad case — a live room with other people in
                         it. Babble is convolved with a RIR from a DIFFERENT source
                         position, because interfering talkers are in the room too;
                         adding dry babble to wet speech is a mismatch no mic hears.

SNR is defined on full-clip RMS, not active speech, so a clip with long pauses is
slightly quieter in speech terms than its label says. Consistent across variants,
which is what drift needs; treat the dB labels as nominal.
"""
import json
import time
from pathlib import Path

import numpy as np
import onnxruntime as ort
import pyroomacoustics as pra
import torch
from jiwer import wer
from scipy.signal import fftconvolve
from transformers import AutoProcessor

HERE = Path(__file__).parent
ONNX = HERE / "onnx"
SR = 16000
SEED = 20260903

PROC = AutoProcessor.from_pretrained(HERE / "models" / "granite", trust_remote_code=True)

# fp32 FIRST: every drift number is measured against it, so it must be the baseline
# the loop below captures. int8 naive is the negative control (see module docstring).
VARIANTS = [
    ("fp32", "granite_ctc.onnx"),
    ("int8 per-chan", "granite_ctc.int8pc.onnx"),
    ("int8 naive", "granite_ctc.int8.onnx"),
]


def norm(s: str) -> str:
    keep = "".join(c for c in s.lower() if c.isalnum() or c.isspace())
    return " ".join(keep.split())


def decode(logits: np.ndarray) -> list:
    ids, out, prev = logits.argmax(-1)[0], [], -1
    for i in ids:
        if i != prev and i != 0:
            out.append(int(i))
        prev = int(i)
    return out


def rms(x: np.ndarray) -> float:
    return float(np.sqrt(np.mean(x.astype(np.float64) ** 2)) + 1e-12)


def at_snr(speech: np.ndarray, noise: np.ndarray, snr_db: float) -> np.ndarray:
    """Speech + noise scaled to the requested SNR, rescaled if it would clip.

    Rescaling rather than hard-clipping on purpose: clipping is its own distortion
    and would contaminate the comparison with an artifact neither build causes.
    """
    noise = noise[: len(speech)]
    if len(noise) < len(speech):
        noise = np.tile(noise, int(np.ceil(len(speech) / len(noise))))[: len(speech)]
    scale = rms(speech) / (rms(noise) * (10 ** (snr_db / 20)))
    out = speech + noise * scale
    peak = np.max(np.abs(out))
    return (out / peak * 0.99) if peak > 0.99 else out


def pink(n: int, rng) -> np.ndarray:
    """1/f noise via spectral shaping — fans and room tone are pink, not white."""
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.arange(len(spec))
    f[0] = 1
    return np.fft.irfft(spec / np.sqrt(f), n).astype(np.float32)


def babble(clips, n: int, rng, n_talkers: int = 8) -> np.ndarray:
    """Multi-talker babble from the corpus's own speakers, summed at random offsets."""
    out = np.zeros(n, dtype=np.float32)
    for _ in range(n_talkers):
        c = clips[rng.integers(len(clips))]
        c = c / rms(c)
        reps = int(np.ceil((n + len(c)) / len(c)))
        tiled = np.tile(c, reps)
        start = int(rng.integers(len(c)))
        out += tiled[start : start + n]
    return out / n_talkers


def rirs(t60: float):
    """(speech RIR, interferer RIR) for one room — two source positions, one mic.

    The interferer gets its own path because babble in a real room arrives
    reverberated too; convolving only the speech would make the noise sound
    impossibly close and understate the difficulty.
    """
    room_dim = [6.0, 5.0, 3.0]
    e_abs, max_order = pra.inverse_sabine(t60, room_dim)
    room = pra.ShoeBox(room_dim, fs=SR, materials=pra.Material(e_abs),
                       max_order=max_order)
    room.add_source([1.5, 2.5, 1.6])           # talker, ~2 m from the mic
    room.add_source([5.0, 1.0, 1.6])           # interferer, across the room
    room.add_microphone([3.5, 2.5, 1.2])
    room.compute_rir()
    return room.rir[0][0], room.rir[0][1]


def reverb(x: np.ndarray, rir: np.ndarray) -> np.ndarray:
    """Convolve, trim back to length, and restore the original RMS.

    Level is held constant so a condition never changes the mel front-end's
    operating point — the log-mel floor is absolute, so a quieter signal would
    degrade for a reason that has nothing to do with the room.
    """
    y = fftconvolve(x, rir)[: len(x)]
    return (y / rms(y) * rms(x)).astype(np.float32)


def build_conditions(clips):
    rng = np.random.default_rng(SEED)
    rir_short, _ = rirs(0.4)
    rir_long, rir_interf = rirs(0.7)

    conds = {"clean": list(clips)}
    for snr in (20, 10, 5):
        conds[f"babble {snr}dB"] = [
            at_snr(c, babble(clips, len(c), rng), snr) for c in clips
        ]
    conds["pink 10dB"] = [at_snr(c, pink(len(c), rng), 10) for c in clips]
    conds["far 0.4s"] = [reverb(c, rir_short) for c in clips]
    conds["far 0.7s + babble 15dB"] = [
        at_snr(reverb(c, rir_long), reverb(babble(clips, len(c), rng), rir_interf), 15)
        for c in clips
    ]
    return conds


def features(clips):
    out = []
    for c in clips:
        f = PROC([c.astype(np.float32)], sampling_rate=SR)["input_features"]
        out.append((f if torch.is_tensor(f) else torch.tensor(f)).numpy())
    return out


def main() -> None:
    from corpus import librispeech

    clips, raw_refs = librispeech(1000)
    refs = [norm(r) for r in raw_refs]
    audio_s = sum(len(c) for c in clips) / SR
    print(f"corpus: {len(clips)} utterances, {audio_s:.1f}s\n")

    conds = build_conditions(clips)
    print(f"conditions: {', '.join(conds)}\n")
    feats = {name: features(cs) for name, cs in conds.items()}

    sessions = {}
    for label, fname in VARIANTS:
        path = ONNX / fname
        if not path.exists():
            print(f"{label}: MISSING ({fname})")
            continue
        sessions[label] = ort.InferenceSession(
            str(path), ort.SessionOptions(), providers=["CPUExecutionProvider"])
        sessions[label].run(None, {"input_features": feats["clean"][0]})

    results, table = {}, []
    for cond in conds:
        base = None
        for label in sessions:
            hyps, t = [], 0.0
            for x in feats[cond]:
                t0 = time.perf_counter()
                out = sessions[label].run(None, {"input_features": x})[0]
                t += time.perf_counter() - t0
                hyps.append(norm(PROC.batch_decode([decode(out)],
                                                   skip_special_tokens=True)[0]))
            if base is None:
                base = hyps
            w = wer(refs, hyps) * 100
            # Against fp32 ON THIS CONDITION — the whole point. Comparing to clean
            # fp32 would fold the room's difficulty into the quantization number.
            d = wer(base, hyps) * 100
            same = sum(a == b for a, b in zip(base, hyps))
            table.append((cond, label, w, d, same, len(hyps), audio_s / t))
            results.setdefault(cond, {})[label] = {"wer": w, "drift": d,
                                                   "same": same, "hyps": hyps}
            print(f"{cond:24s} {label:14s} WER {w:6.2f}%  drift {d:5.2f}%  "
                  f"same {same}/{len(hyps)}  {audio_s/t:5.1f}x RT")
        print()

    (HERE / "noisy_results.json").write_text(json.dumps(results, indent=1))

    print("\n| condition | variant | WER | drift vs fp32 (same condition) | identical |")
    print("|---|---|---|---|---|")
    for cond, label, w, d, same, n, x in table:
        print(f"| {cond} | {label} | {w:.2f}% | {d:.2f}% | {same}/{n} |")


if __name__ == "__main__":
    main()
