"""Verify the pinned model's real alphabet and every advertised speaker offline."""
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'src/QuietSelection.Speech'))
import silero_worker as worker
worker.APP_ROOT = ROOT / 'src/QuietSelection.App/bin/Release/net10.0-windows'
sys.path.insert(0, str(worker.APP_ROOT / 'silero-runtime'))
cache = worker.VoiceCache(ROOT / '.tools/silero-downloads')
stop = worker.Cancellation(None)
try:
    model, hit = cache.get('silero:v3_en:en_0', stop)
    assert not hit
    expected = [v['Speaker'] for v in cache.catalogs[1]['Voices']]
    assert expected == [s for s in model.speakers if s != 'random']
    for source in ('731', '0.16–0.21 s', 'Silero takes 57.2 MB.', '1,234,567'):
        normalized = worker.normalize_english(source).lower()
        actual, _, has_text = model.prepare_text_input(normalized)
        assert has_text
        assert actual.split() == normalized.split(), (source, actual)
        assert len(worker.render(model, normalized, 'en_0', 50, stop, True)) > 44
    for speed in (0, 50, 100):
        assert len(worker.render(model, 'Hello, world!', 'en_117', speed, stop, True)) > 44
    print('SILERO_ENGLISH_FRONTEND_PASS 8; 118 speaker IDs match pinned model')
finally:
    stop.close()
