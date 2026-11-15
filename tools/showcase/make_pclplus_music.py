# -*- coding: utf-8 -*-
"""32 s music-only bed for the PCL_Plus showcase."""
from pathlib import Path
import math
import wave

import numpy as np

BUILD = Path(__file__).resolve().parent / "build32"
SR = 48000
DUR = 55.5
N = int(SR * DUR)


def adsr(n, a=0.02, d=0.12, s=0.65, r=0.25):
    t = np.arange(n) / SR
    env = np.ones(n)
    ai = max(1, int(a * SR)); di = max(1, int(d * SR)); ri = max(1, int(r * SR))
    env[:ai] = np.linspace(0, 1, ai)
    env[ai:ai + di] = np.linspace(1, s, min(di, n - ai))
    env[-ri:] *= np.linspace(1, 0, min(ri, n))
    return env


def main():
    t = np.arange(N) / SR
    out = np.zeros(N, np.float32)

    # Warm pad: Fm - Db - Ab - Eb, four 8 s sections.
    chords = [(174.61, 220.00, 261.63), (138.59, 174.61, 220.00),
              (207.65, 261.63, 311.13), (155.56, 196.00, 233.08)]
    seg = DUR / 4
    for i, (a, b, c) in enumerate(chords):
        start = int(i * seg * SR)
        end = min(N, int((i + 1) * seg * SR))
        tt = t[start:end] - t[start]
        env = np.minimum(1, tt / 1.6) * np.minimum(1, (seg - tt) / 1.6)
        env = np.clip(env, 0, 1) ** 1.3
        pad = (np.sin(2 * math.pi * a * tt) * 0.46 +
               np.sin(2 * math.pi * b * tt) * 0.26 +
               np.sin(2 * math.pi * c * tt) * 0.17 +
               np.sin(2 * math.pi * (a / 2) * tt) * 0.10)
        out[start:end] += (pad * env * 0.22).astype(np.float32)

    # Soft pulse / kick, 100 BPM.
    bpm = 100.0
    beat = 60.0 / bpm
    for k in range(int(DUR / beat)):
        start = int(k * beat * SR)
        n = int(0.24 * SR)
        if start + n > N:
            break
        tt = np.arange(n) / SR
        freq = 92 * np.exp(-tt * 18) + 42
        kick = np.sin(2 * math.pi * freq * tt) * np.exp(-tt * 13) * 0.25
        out[start:start + n] += kick.astype(np.float32)

    # Light hi-hat ticks on the offbeat.
    for k in range(int(DUR / beat)):
        start = int((k + 0.5) * beat * SR)
        n = int(0.045 * SR)
        if start + n > N:
            break
        tt = np.arange(n) / SR
        noise = np.random.default_rng(1000 + k).normal(0, 1, n)
        hat = noise * np.exp(-tt * 95) * 0.022
        out[start:start + n] += hat.astype(np.float32)

    # Airy shimmer.
    out += (np.sin(2 * math.pi * 880 * t) * 0.0045).astype(np.float32)
    out += (np.sin(2 * math.pi * 1320 * t + 0.6) * 0.0025).astype(np.float32)

    # Intro/outro fades and final soft limiter.
    fade = np.minimum(1, t / 1.4) * np.minimum(1, (DUR - t) / 2.0)
    out *= np.clip(fade, 0, 1)
    peak = float(np.max(np.abs(out)))
    if peak > 0.9:
        out *= 0.9 / peak

    stereo = np.stack([out, out], axis=1)
    pcm = np.clip(stereo * 32767, -32768, 32767).astype("<i2")
    path = BUILD / "music-only.wav"
    with wave.open(str(path), "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    print(path, path.stat().st_size)


if __name__ == "__main__":
    main()
