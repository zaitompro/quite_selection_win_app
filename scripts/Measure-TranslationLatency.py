"""Translation latency: a new worker process per request versus a model kept loaded.

Fixed synthetic phrases only; nothing is saved except the timing report.
Usage: python -I -B scripts/Measure-TranslationLatency.py <model-root>
"""
import json
import struct
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'src/QuietSelection.App/bin/Release/net10.0-windows'
PYTHON = ROOT / '.tools/translation-runtime/python.exe'
OUT = ROOT / 'artifacts/test-results/translation-latency'
SAMPLES = [('en', 'Please send the report by Friday. Thank you for your help.'),
           ('ja', '会議は明日の午後三時に始まります。資料を準備してください。')]


def frame(payload):
    data = json.dumps(payload, ensure_ascii=False).encode('utf-8')
    return struct.pack('<i', len(data)) + data


def cold(model_root, source, text):
    started = time.perf_counter()
    process = subprocess.run([str(PYTHON), '-I', '-B', str(APP / 'translation/worker.py'), str(model_root)],
                             input=frame({'Segments': [{'Text': text, 'Source': source}], 'Target': 'ru'}),
                             capture_output=True, timeout=300, env={'OMP_NUM_THREADS': '2', 'OPENBLAS_NUM_THREADS': '1',
                                                                    'SYSTEMROOT': 'C:\\Windows'})
    elapsed = time.perf_counter() - started
    size = struct.unpack('<i', process.stdout[:4])[0]
    response = json.loads(process.stdout[4:4 + size])
    return elapsed, response['Success']


def warm(model_root):
    sys.path.insert(0, str(APP / 'translation'))
    import worker  # noqa: E402  (installs the worker's read-only audit hook)
    engine = worker.Engine(model_root)
    rows = []
    for source, text in SAMPLES:
        started = time.perf_counter()
        engine.run({'Segments': [{'Text': text, 'Source': source}], 'Target': 'ru'})
        first = time.perf_counter() - started
        started = time.perf_counter()
        engine.run({'Segments': [{'Text': text, 'Source': source}], 'Target': 'ru'})
        rows.append(dict(source=source, loadAndTranslate=first, loadedTranslate=time.perf_counter() - started))
    return rows


def main(model_root):
    OUT.mkdir(parents=True, exist_ok=True)
    report = {'cold': [], 'warm': None}
    for source, text in SAMPLES:
        for _ in range(2):
            elapsed, ok = cold(model_root, source, text)
            report['cold'].append(dict(source=source, seconds=elapsed, success=ok))
            print(json.dumps(dict(mode='new process', source=source, seconds=round(elapsed, 2), success=ok)), flush=True)
    # The warm run imports the worker last: its audit hook forbids writes, so save the report first.
    (OUT / 'latency.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    result = subprocess.run([str(PYTHON), '-I', '-B', __file__, '--warm', str(model_root)], capture_output=True, text=True, timeout=600)
    report['warm'] = json.loads(result.stdout.strip().splitlines()[-1])
    for row in report['warm']:
        print(json.dumps(dict(mode='kept loaded', source=row['source'], first=round(row['loadAndTranslate'], 2),
                              next=round(row['loadedTranslate'], 2))), flush=True)
    (OUT / 'latency.json').write_text(json.dumps(report, indent=2), encoding='utf-8')


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--warm':
        print(json.dumps(warm(sys.argv[2])))
    else:
        main(sys.argv[1])
