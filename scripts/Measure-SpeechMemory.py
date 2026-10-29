"""Observe retained arenas after longer synthetic fragments, isolated per engine."""
import json
import os
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'src/QuietSelection.App/bin/Release/net10.0-windows'
OUT = ROOT / 'artifacts/test-results/speech-tuning'


def child(engine):
    sys.path.insert(0, str(APP / 'speech'))
    from speech_runtime import Cancellation
    import additional_worker as worker
    english = engine == 'kokoro'
    model = ROOT / ('.tools/kokoro-downloads' if english else '.tools/voicevox-downloads/0.16.4')
    voice = 'kokoro:af_heart' if english else 'voicevox:metan:normal'
    short = 'This is a QuietSelection test sentence.' if english else 'これは読み上げのテストです。'
    long = ('The application can read a longer selection of text with the chosen voice and prepare the next part while the previous part is still playing.'
            if english else 'このアプリケーションでは選択した文章を自然な声で読み上げることができて次の文章を準備しながら現在の文章を再生するため長い文章でも待ち時間を減らすことができます。')
    worker.send = lambda stage: None
    cache = worker.VoiceCache(model, engine)
    cancellation = Cancellation(None)
    rows = []
    for scenario, text in [('short', short), ('long', long), ('long-repeat', long), ('short-after-long', short)]:
        result = worker.synthesize({'Kind': 'synthesize-test', 'Text': text, 'VoiceId': voice, 'Speed': 50}, cache, cancellation)
        assert result['ProcessedCharacters'] == len(text)
        rows.append({'scenario': scenario, 'characters': len(text), 'result': result})
    time.sleep(1.5)
    rows.append({'scenario': 'idle-after-long', 'result': worker.snapshot(cache)})
    print(json.dumps({'engine': engine, 'rows': rows}))
    cache.clear()
    cancellation.close()


if __name__ == '__main__':
    if len(sys.argv) == 2:
        child(sys.argv[1])
    else:
        rows = []
        for engine in ('kokoro', 'voicevox'):
            result = subprocess.run([sys.executable, '-I', '-B', __file__, engine], capture_output=True,
                                    text=True, encoding='utf-8', timeout=180, check=True,
                                    env=dict(os.environ, OMP_NUM_THREADS='8', OPENBLAS_NUM_THREADS='1'))
            row = json.loads(result.stdout)
            rows.append(row)
            (OUT / 'long-fragment-memory.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
            print(json.dumps({'engine': engine, 'rows': [{'scenario': r['scenario'],
                  'rssMiB': round(r['result']['WorkingSetBytes']/1048576, 1),
                  'privateMiB': round(r['result']['PrivateBytes']/1048576, 1),
                  'peakMiB': round(r['result']['PeakWorkingSetBytes']/1048576, 1)} for r in row['rows']]}), flush=True)
