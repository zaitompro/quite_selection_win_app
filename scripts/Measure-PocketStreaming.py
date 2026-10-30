"""Pocket TTS streaming speed per CPU thread count; fixed synthetic phrase, no saved or played audio.

For each thread count it records when every PCM block becomes available and simulates
playback with a given pre-roll to count stalls (the audible breaks).
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'src/QuietSelection.App/bin/Release/net10.0-windows'
OUT = ROOT / 'artifacts/test-results/pocket-streaming'
PHRASE = ('This is a longer QuietSelection reading test. It contains several sentences, '
          'so that the streaming speed can be measured after the first block. '
          'Numbers like 2026 and 42 are included, and the voice should keep a steady pace.')


def simulate(blocks, preroll):
    """blocks: (ready_seconds, duration_seconds). Returns (stalls, stalled_seconds)."""
    if not blocks:
        return 0, 0.0
    clock = None
    queued_until = 0.0
    stalls, stalled = 0, 0.0
    for ready, duration in blocks:
        if clock is None:
            clock = ready + preroll
            queued_until = clock
        if ready > queued_until:  # Device ran dry before this block arrived.
            stalls += 1
            stalled += ready - queued_until
            queued_until = ready
        queued_until += duration
    return stalls, stalled


def child(threads, rate):
    sys.path.insert(0, str(APP / 'speech'))
    from speech_runtime import Cancellation
    import pocket_engine
    source = (APP / 'speech/pocket_engine.py').read_text(encoding='utf-8')
    source = re.sub(r'torch\.set_num_threads\([^)]*\)', f'torch.set_num_threads({threads})', source)
    exec(compile(source, 'pocket_engine.py', 'exec'), pocket_engine.__dict__)
    engine = pocket_engine.PocketEngine(ROOT / '.tools/pocket-tts-downloads')
    cancel = Cancellation(None)
    voice = {'Speaker': 'alba'}
    engine.load(voice, cancel)
    list(engine.synthesize('Warm up.', voice, rate, cancel))
    runs = []
    for _ in range(2):
        started = time.perf_counter()
        blocks = []
        for audio, sample_rate in engine.synthesize(PHRASE, voice, rate, cancel):
            blocks.append((time.perf_counter() - started, len(audio) / (sample_rate * 2)))
        elapsed = time.perf_counter() - started
        audio_seconds = sum(d for _, d in blocks)
        runs.append(dict(
            seconds=elapsed, audioSeconds=audio_seconds, realTimeFactor=elapsed / audio_seconds,
            firstBlockSeconds=blocks[0][0], blocks=len(blocks),
            stalls={str(p): simulate(blocks, p) for p in (0.0, 0.16, 0.4, 0.8, 1.2)}))
    engine.close()
    cancel.close()
    print(json.dumps(dict(threads=threads, rate=rate, runs=runs)))


def main(rate):
    OUT.mkdir(parents=True, exist_ok=True)
    python = ROOT / '.tools/translation-runtime/python.exe'
    rows = []
    for threads in (2, 4, 6, 8):
        environment = dict(os.environ, OMP_NUM_THREADS=str(threads), OPENBLAS_NUM_THREADS='1')
        result = subprocess.run([str(python), '-I', '-B', __file__, '--threads', str(threads), '--rate', str(rate)],
                                env=environment, capture_output=True, text=True, timeout=900)
        if result.returncode != 0:
            print(json.dumps(dict(threads=threads, error=result.stderr[-400:])), flush=True)
            continue
        row = json.loads(result.stdout.strip().splitlines()[-1])
        rows.append(row)
        (OUT / f'threads-rate-{rate}.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
        last = row['runs'][-1]
        print(json.dumps(dict(threads=threads, rtf=round(last['realTimeFactor'], 3), first=round(last['firstBlockSeconds'], 2),
                              stalls={k: [v[0], round(v[1], 2)] for k, v in last['stalls'].items()})), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--threads', type=int)
    parser.add_argument('--rate', type=float, default=1.0)
    args = parser.parse_args()
    child(args.threads, args.rate) if args.threads else main(args.rate)
