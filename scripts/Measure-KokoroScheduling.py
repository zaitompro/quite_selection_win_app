"""Compare scheduling options sequentially, using only a fixed synthetic phrase."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'src/QuietSelection.App/bin/Release/net10.0-windows'
OUT = ROOT / 'artifacts/test-results/speech-tuning/kokoro-scheduling.json'


def child(mode):
    sys.path.insert(0, str(APP / 'speech'))
    from speech_runtime import Cancellation, memory
    from additional_worker import VoiceCache
    import kokoro_engine
    source = (ROOT / 'src/QuietSelection.Speech/kokoro_engine.py').read_text(encoding='utf-8')
    if 'dynamic' in mode:
        source = source.replace('options.log_severity_level = 4',
            "options.log_severity_level = 4\n        options.add_session_config_entry('session.dynamic_block_base', '4')")
    if 'denormal' in mode:
        source = source.replace('options.log_severity_level = 4',
            "options.log_severity_level = 4\n        options.add_session_config_entry('session.set_denormal_as_zero', '1')")
    if 'parallel' in mode:
        source = source.replace('options.inter_op_num_threads = 1',
            'options.inter_op_num_threads = 2\n        options.execution_mode = ort.ExecutionMode.ORT_PARALLEL')
    if 'qos' in mode:
        class PowerState(ctypes.Structure):
            _fields_ = [('Version', ctypes.c_ulong), ('ControlMask', ctypes.c_ulong),
                        ('StateMask', ctypes.c_ulong)]
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.GetCurrentProcess.restype = ctypes.c_void_p
        kernel.SetProcessInformation.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_ulong]
        state = PowerState(1, 1, 0)
        if not kernel.SetProcessInformation(kernel.GetCurrentProcess(), 4, ctypes.byref(state), ctypes.sizeof(state)):
            raise ctypes.WinError(ctypes.get_last_error())
    exec(compile(source, kokoro_engine.__file__, 'exec'), kokoro_engine.__dict__)
    cache = VoiceCache(ROOT / '.tools/kokoro-downloads', 'kokoro')
    cancel = Cancellation(None)
    engine, voice, _ = cache.get('kokoro:af_heart', cancel)
    rows = []
    for iteration in range(5):
        start = time.perf_counter()
        blocks = list(engine.synthesize('This is a QuietSelection test sentence.', voice, 1.0, cancel))
        elapsed = time.perf_counter() - start
        metrics = memory()
        rows.append(dict(seconds=elapsed, rssMiB=metrics.WorkingSetSize / 1048576,
                         privateMiB=metrics.PrivateUsage / 1048576,
                         pcmSha256=hashlib.sha256(b''.join(b for b, _ in blocks)).hexdigest()))
    cache.clear()
    cancel.close()
    print(json.dumps(dict(mode=mode, medianSeconds=statistics.median(r['seconds'] for r in rows[1:]), runs=rows)))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--child')
    parser.add_argument('--modes', nargs='+', default=['baseline', 'dynamic', 'qos', 'dynamic-qos', 'baseline'])
    parser.add_argument('--output', type=Path, default=OUT)
    args = parser.parse_args()
    if args.child:
        child(args.child)
    else:
        results = []
        # Repeat the baseline at the end to detect drift during this series.
        for mode in args.modes:
            run = subprocess.run([sys.executable, '-I', '-B', __file__, '--child', mode],
                                 env=dict(os.environ, OPENBLAS_NUM_THREADS='1', OMP_NUM_THREADS='8'),
                                 capture_output=True, text=True, encoding='utf-8', check=True, timeout=180)
            result = json.loads(run.stdout)
            results.append(result)
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(results, indent=2), encoding='utf-8')
            print(json.dumps({k: v for k, v in result.items() if k != 'runs'}), flush=True)
