"""Same complete text, sequential chunk strategies; writes synthetic samples only."""
import base64
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import time
import wave

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'src/QuietSelection.App/bin/Release/net10.0-windows'
OUT = ROOT / 'artifacts/test-results/speech-tuning'
TEXT = {'kokoro': 'This is a QuietSelection test sentence. You can change the speaking speed.',
        'voicevox': 'これは読み上げのテストです。話す速度を変更できます。'}


def child(engine):
    sys.path.insert(0, str(APP / 'speech'))
    from speech_runtime import Cancellation, memory
    from speech_text import pieces, bounded
    from additional_text import speech_parts
    from additional_worker import VoiceCache
    model = ROOT / ('.tools/kokoro-downloads' if engine == 'kokoro' else '.tools/voicevox-downloads/0.16.4')
    cache = VoiceCache(model, engine)
    cancellation = Cancellation(None)
    adapter, voice, _ = cache.get('kokoro:af_heart' if engine == 'kokoro' else 'voicevox:metan:normal', cancellation)
    strategies = {'previous-140': list(pieces(TEXT[engine], 140)),
                  'natural': list(speech_parts(TEXT[engine], engine)),
                  'short-fixed': list(bounded(TEXT[engine], 24 if engine == 'kokoro' else 8))}
    rows = []
    for name, parts in strategies.items():
        assert ''.join(parts) == TEXT[engine]
        runs = []
        sample = []
        for iteration in range(4):
            start = time.perf_counter()
            first = None
            audio_seconds = 0
            blocks = []
            for part in parts:
                for audio, rate in adapter.synthesize(part, voice, 1, cancellation):
                    if first is None:
                        first = time.perf_counter() - start
                    audio_seconds += len(audio) / (rate * 2)
                    blocks.append(audio)
            elapsed = time.perf_counter() - start
            m = memory()
            runs.append({'firstSeconds': first, 'totalSeconds': elapsed, 'audioSeconds': audio_seconds,
                         'rssMiB': m.WorkingSetSize/1048576, 'peakRssMiB': m.PeakWorkingSetSize/1048576,
                         'privateMiB': m.PrivateUsage/1048576})
            sample = blocks
        rows.append({'strategy': name, 'parts': parts, 'runs': runs,
                     'firstMedianSeconds': statistics.median(r['firstSeconds'] for r in runs[1:]),
                     'totalMedianSeconds': statistics.median(r['totalSeconds'] for r in runs[1:]),
                     'samplePcmBase64': base64.b64encode(b''.join(sample)).decode('ascii')})
    print(json.dumps({'engine': engine, 'rows': rows}))


if __name__ == '__main__':
    if len(sys.argv) == 2:
        child(sys.argv[1])
    else:
        reports = []
        for engine in TEXT:
            result = subprocess.run([sys.executable, '-I', '-B', __file__, engine], capture_output=True,
                                    text=True, encoding='utf-8', timeout=240, check=True,
                                    env=dict(os.environ, OMP_NUM_THREADS='8', OPENBLAS_NUM_THREADS='1'))
            report = json.loads(result.stdout)
            for row in report['rows']:
                sample = OUT / (engine + '-' + row['strategy'] + '.wav')
                with wave.open(str(sample), 'wb') as target:
                    target.setparams((1, 2, 24000, 0, 'NONE', 'not compressed'))
                    target.writeframes(base64.b64decode(row.pop('samplePcmBase64')))
                row['sampleFile'] = str(sample)
            reports.append(report)
            (OUT / 'chunk-matrix.json').write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding='utf-8')
            print(json.dumps({'engine': engine, 'results': [{k: r[k] for k in ('strategy', 'firstMedianSeconds', 'totalMedianSeconds')} for r in report['rows']]}), flush=True)
