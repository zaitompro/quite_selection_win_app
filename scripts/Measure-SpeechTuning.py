"""Sequential, isolated CPU tuning experiments using synthetic text only.

Baseline adapter snapshots are retained with the report for reproducibility.
The child keeps the production read-only/network audit hook enabled.
"""
import argparse
import json
import os
import re
from pathlib import Path
import statistics
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'src/QuietSelection.App/bin/Release/net10.0-windows'
OUT = ROOT / 'artifacts/test-results/speech-tuning'
PHRASES = {
    'kokoro': ['This is a QuietSelection test sentence. You can change the speaking speed.',
               'This is a QuietSelection test sentence.', 'This is a test.',
               'You can change the speaking speed.'],
    'voicevox': ['これは読み上げのテストです。話す速度を変更できます。',
                 'これは読み上げのテストです。', 'これはテストです。', '話す速度を変更できます。'],
}


def child(args):
    sys.path.insert(0, str(APP / 'speech'))
    from speech_runtime import Cancellation, memory
    from additional_worker import VoiceCache
    import importlib
    module = importlib.import_module(args.engine + '_engine')
    source = (OUT / (args.engine + '-baseline.py')).read_text(encoding='utf-8')
    source = re.sub(r'options.intra_op_num_threads = [^\n]+', f'options.intra_op_num_threads = {args.threads}', source)
    source = source.replace('InitializeOptions(1, 2)', f'InitializeOptions(1, {args.threads})')
    source = source.replace('InitializeOptions(1, min(8, os.cpu_count() or 1))', f'InitializeOptions(1, {args.threads})')
    source = re.sub(r'options.enable_cpu_mem_arena = (?:True|False)', f'options.enable_cpu_mem_arena = {args.arena}', source)
    source = re.sub(r'options.enable_mem_pattern = (?:True|False)', f'options.enable_mem_pattern = {args.arena}', source)
    if args.spin:
        source = source.replace("'session.intra_op.allow_spinning', '0'", "'session.intra_op.allow_spinning', '1'")
        source = source.replace('options.log_severity_level = 4', "options.log_severity_level = 4\n        options.add_session_config_entry('session.force_spinning_stop', '1')")
    exec(compile(source, str(OUT / (args.engine + '-baseline.py')), 'exec'), module.__dict__)
    model = ROOT / ('.tools/kokoro-downloads' if args.engine == 'kokoro' else '.tools/voicevox-downloads/0.16.4')
    cache = VoiceCache(model, args.engine)
    cancel = Cancellation(None)
    adapter, voice, _ = cache.get('kokoro:af_heart' if args.engine == 'kokoro' else 'voicevox:metan:normal', cancel)
    rows = []
    for index, phrase in enumerate(PHRASES[args.engine][1:2] if args.quick else PHRASES[args.engine]):
        runs = []
        for iteration in range(4):
            start = time.perf_counter()
            blocks = list(adapter.synthesize(phrase, voice, 1.0, cancel))
            seconds = time.perf_counter() - start
            metrics = memory()
            runs.append({'seconds': seconds, 'audioSeconds': sum(len(a)/(r*2) for a,r in blocks),
                         'rssMiB': metrics.WorkingSetSize/1048576, 'privateMiB': metrics.PrivateUsage/1048576,
                         'peakRssMiB': metrics.PeakWorkingSetSize/1048576})
        rows.append({'phrase': phrase, 'medianSeconds': statistics.median(r['seconds'] for r in runs[1:]), 'runs': runs})
    cpu_before = time.process_time()
    time.sleep(1.5)
    idle_cpu_ms = (time.process_time() - cpu_before) * 1000
    cache.clear()
    cancel.close()
    print(json.dumps({'engine': args.engine, 'threads': args.threads, 'arena': args.arena, 'spin': args.spin, 'idleCpuMs': idle_cpu_ms, 'rows': rows}))


def main(args):
    OUT.mkdir(parents=True, exist_ok=True)
    for engine in PHRASES:
        snapshot = OUT / (engine + '-baseline.py')
        if not snapshot.exists():
            snapshot.write_bytes((APP / 'speech' / (engine + '_engine.py')).read_bytes())
    results = []
    configurations = [(engine, threads, False) for engine in PHRASES for threads in (2, 1, 4, 8)]
    configurations += [('kokoro', 2, True), ('kokoro', 4, True)]
    if args.spin:
        configurations = [('kokoro', 4, False), ('kokoro', 8, False), ('kokoro', 4, True)]
    for engine, threads, arena in configurations:
        command = [sys.executable, '-I', '-B', __file__, '--child', '--engine', engine, '--threads', str(threads)]
        if arena:
            command.append('--arena')
        if args.spin:
            command.append('--spin')
        environment = dict(os.environ, OMP_NUM_THREADS=str(threads), OPENBLAS_NUM_THREADS='1')
        completed = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', env=environment, timeout=360, check=True)
        result = json.loads(completed.stdout)
        results.append(result)
        (OUT / ('spin-matrix.json' if args.spin else 'thread-matrix.json')).write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps({'engine': engine, 'threads': threads, 'arena': arena,
                          'seconds': [round(r['medianSeconds'], 3) for r in result['rows']],
                          'rssMiB': round(result['rows'][-1]['runs'][-1]['rssMiB'], 1)}), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--engine', choices=PHRASES)
    parser.add_argument('--threads', type=int, default=2)
    parser.add_argument('--arena', action='store_true')
    parser.add_argument('--spin', action='store_true')
    parser.add_argument('--quick', action='store_true')
    arguments = parser.parse_args()
    (child if arguments.child else main)(arguments)
