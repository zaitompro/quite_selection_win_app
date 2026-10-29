"""Pre-package playback smoke test: the real worker, synthetic phrases only."""
import json
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[1]
app = root / 'src/QuietSelection.App/bin/Release/net10.0-windows'
sys.path.insert(0, str(app / 'speech'))
from speech_runtime import Cancellation
import additional_worker as worker

for engine, voice, phrase, directory in (
    ('kokoro', 'kokoro:af_heart', 'This is a QuietSelection test sentence. You can change the speaking speed.', '.tools/kokoro-downloads'),
    ('voicevox', 'voicevox:metan:normal', 'これは読み上げのテストです。話す速度を変更できます。', '.tools/voicevox-downloads/0.16.4'),
):
    cache = worker.VoiceCache(root / directory, engine)
    cancellation = Cancellation(None)
    stages = []
    worker.send = stages.append
    cache.get(voice, cancellation)
    result = worker.synthesize({'Kind': 'speak', 'VoiceId': voice, 'Text': phrase, 'Speed': 50}, cache, cancellation)
    assert result['Success'] and result['Chunks'] == 2
    assert result['ProcessedCharacters'] == len(phrase) and stages == [{'Stage': 'first-audio'}]
    assert not cancellation.cancelled.is_set()
    print(json.dumps({'engine': engine, 'result': result}), flush=True)
    cache.clear()
    cancellation.close()
