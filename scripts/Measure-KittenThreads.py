"""Sequential CPU thread comparison; fixed synthetic phrases, no saved audio."""
import argparse
import json
import os
from pathlib import Path
import re
import statistics
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'src/QuietSelection.App/bin/Release/net10.0-windows'
OUT = ROOT / 'artifacts/test-results/kitten-mini-threads'


def child(threads):
    sys.path.insert(0, str(APP / 'speech'))
    from speech_runtime import Cancellation, memory
    from additional_worker import VoiceCache
    import kitten_engine
    source = (OUT / 'baseline.py').read_text(encoding='utf-8')
    source = re.sub(r'options.intra_op_num_threads = [^\n]+',
                    f'options.intra_op_num_threads = {threads}', source)
    exec(compile(source, str(OUT / 'baseline.py'), 'exec'), kitten_engine.__dict__)
    cache = VoiceCache(ROOT / '.tools/kitten-mini-downloads', 'kitten')
    cancel = Cancellation(None)
    adapter, voice, _ = cache.get('kitten:mini-0.8:bella', cancel)
    rows = []
    for phrase in ('This is a test.', 'This is a QuietSelection test sentence.'):
        runs = []
        for _ in range(4):
            started = time.perf_counter()
            blocks = list(adapter.synthesize(phrase, voice, 1.0, cancel))
            elapsed = time.perf_counter() - started
            metrics = memory()
            runs.append(dict(seconds=elapsed, audioSeconds=sum(len(a)/(r*2) for a, r in blocks),
                             rssMiB=metrics.WorkingSetSize/1048576,
                             peakRssMiB=metrics.PeakWorkingSetSize/1048576,
                             privateMiB=metrics.PrivateUsage/1048576))
        rows.append(dict(phrase=phrase, medianSeconds=statistics.median(r['seconds'] for r in runs[1:]), runs=runs))
    cache.clear()
    cancel.close()
    print(json.dumps(dict(threads=threads, rows=rows)))


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / 'baseline.py').write_bytes((APP / 'speech/kitten_engine.py').read_bytes())
    rows = []
    for threads in (1, 2, 4, 8):
        environment = dict(os.environ, OMP_NUM_THREADS=str(threads), OPENBLAS_NUM_THREADS='1')
        result = subprocess.run([sys.executable, '-I', '-B', __file__, '--threads', str(threads)],
                                env=environment, capture_output=True, text=True, check=True, timeout=180)
        row = json.loads(result.stdout)
        rows.append(row)
        (OUT / 'threads.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
        print(json.dumps(dict(threads=threads, medians=[r['medianSeconds'] for r in row['rows']])), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--threads', type=int)
    args = parser.parse_args()
    child(args.threads) if args.threads else main()
