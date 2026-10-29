"""Streaming WSOLA tempo adjustment at unchanged sample rate and pitch.

Only a small input window is retained. At normal speed samples pass untouched.
Windows align by normalized correlation before a linear overlap crossfade.
"""
import numpy as np


def change_tempo(source, rate, sample_rate=24000):
    if rate == 1:
        yield from source
        return
    hop = sample_rate // 50  # 20 ms overlap, 40 ms window.
    search = sample_rate // 100
    fade = np.arange(hop, dtype=np.float32) / hop
    buffer = np.empty(0, dtype=np.float32)
    origin = 0
    position = 0.0
    previous = None
    total_input = 0
    total_output = 0

    def ready(final=False):
        nonlocal buffer, origin, position, previous, total_output
        while True:
            if final and total_output >= round(total_input / rate):
                break
            expected = round(position)
            low = max(origin, expected - search)
            high = expected + search
            if not final and high + 2 * hop > origin + len(buffer):
                break
            high = min(high, origin + len(buffer) - 2 * hop)
            if high < low:
                break
            if previous is None:
                start = low
            else:
                region = buffer[low-origin:high-origin+hop]
                scores = np.correlate(region, previous, mode="valid")
                energies = np.convolve(region * region, np.ones(hop, dtype=np.float32), mode="valid")
                scores /= np.sqrt(energies + 1e-10)
                start = low + int(np.argmax(scores))
            window = buffer[start-origin:start-origin+2*hop]
            audio = window[:hop].copy() if previous is None else previous * (1 - fade) + window[:hop] * fade
            previous = window[hop:].copy()
            if final:
                audio = audio[:round(total_input / rate) - total_output]
            total_output += len(audio)
            yield audio
            position += hop * rate
            keep = max(origin, round(position) - search)
            discard = min(len(buffer), keep - origin)
            buffer = buffer[discard:].copy()
            origin += discard

    for audio in source:
        total_input += len(audio)
        buffer = np.concatenate((buffer, audio))
        yield from ready()
    # Continue the same overlap process through a zero-padded tail, never
    # concatenate a duplicated raw input tail across an unaligned boundary.
    buffer = np.pad(buffer, (0, 2 * hop + 2 * search))
    yield from ready(final=True)
