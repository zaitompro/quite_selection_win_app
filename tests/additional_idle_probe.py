"""Distinguish native thread-pool settling from sustained idle CPU consumption."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]


def child(args):
    sys.path.insert(0, str(args.app_root / 'speech'))
    from speech_runtime import Cancellation
    from additional_worker import VoiceCache, snapshot
    cache = VoiceCache(ROOT / '.tools/voicevox-downloads/0.16.4', 'voicevox')
    cancellation = Cancellation(None)
    adapter, voice, _ = cache.get(args.voice, cancellation)
    rows = []
    for iteration in range(2):
        list(adapter.synthesize('これは読み上げのテストです。話す速度を変更できます。', voice, 1, cancellation))
        samples = []
        for phase, duration in [('settling', 0.5), ('steady-1', 1.5), ('steady-2', 1.5)]:
            cpu, wall = time.process_time(), time.perf_counter()
            time.sleep(duration)
            samples.append({'phase': phase, 'cpuMs': (time.process_time()-cpu)*1000,
                            'wallMs': (time.perf_counter()-wall)*1000})
        rows.append({'iteration': iteration, 'samples': samples, 'memory': snapshot(cache)})
    passed = all(s['cpuMs'] < 100 for r in rows for s in r['samples'] if s['phase'].startswith('steady'))
    print(json.dumps({'voice': args.voice, 'passed': passed, 'rows': rows}))
    cache.clear()
    cancellation.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--app-root', type=Path, required=True)
    parser.add_argument('--voice')
    args = parser.parse_args()
    if args.voice:
        child(args)
    else:
        checks = []
        for voice in ('voicevox:metan:normal', 'voicevox:takehiro:normal'):
            process = subprocess.run([str(args.app_root / 'translation-runtime/python.exe'), '-I', '-B', __file__,
                                      '--app-root', str(args.app_root), '--voice', voice], capture_output=True,
                                     text=True, encoding='utf-8', timeout=90, check=True,
                                     env=dict(os.environ, OMP_NUM_THREADS='8', OPENBLAS_NUM_THREADS='1'))
            check = json.loads(process.stdout)
            checks.append(check)
            print(json.dumps(check), flush=True)
        report = {'appRoot': str(args.app_root), 'scope': 'Worker files and runtime from the release build, direct process without package activation. Initial 500ms settling and subsequent two 1500ms idle windows are measured separately. Two readings per voice.', 'checks': checks}
        path = ROOT / 'artifacts/test-results/additional-speech-installed/idle-followup-0.1.0.17.json'
        path.write_text(json.dumps(report, indent=2), encoding='utf-8')
        assert all(c['passed'] for c in checks), 'SUSTAINED_IDLE_CPU'
