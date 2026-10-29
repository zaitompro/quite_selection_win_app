"""Real phonemizer and model-integrity regression checks, synthetic text only."""
import importlib.util
import json
import shutil
import sys
import tempfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
engine = 'pocket' if '--pocket' in sys.argv else 'kitten' if '--kitten' in sys.argv else 'kokoro'
app = root / 'src/QuietSelection.App/bin/Release/net10.0-windows'
temporary = tempfile.TemporaryDirectory(dir=root / 'artifacts/test-results', prefix='speech-integrity-')
fixture = Path(temporary.name)
catalog = json.loads((app / 'speech' / (engine + '-models.json')).read_text(encoding='utf-8-sig'))
for item in catalog['Files']:
    shutil.copyfile(root / '.tools' / ('pocket-tts-downloads' if engine == 'pocket' else 'kitten-mini-downloads' if engine == 'kitten' else engine + '-downloads') / item['Name'], fixture / item['Name'])
# Corrupt a copy without changing its size: size-only validation must not load it.
with (fixture / ('tokenizer.json' if engine == 'pocket' else 'voices.npz' if engine == 'kitten' else 'voices-v1.0.bin')).open('r+b') as data:
    value = data.read(1)
    data.seek(0)
    data.write(bytes([value[0] ^ 0xff]))
sys.path.insert(0, str(app / 'speech'))
from speech_runtime import Cancellation
from additional_worker import VoiceCache
if engine == 'kitten':
    from kitten_engine import KittenEngine, normalize, VOCAB
    voice = KittenEngine.__new__(KittenEngine)
elif engine == 'kokoro':
    from kokoro_engine import KokoroEngine
    voice = KokoroEngine.__new__(KokoroEngine)
    voice.vocab = json.loads((app / 'speech/kokoro-config.json').read_text(encoding='utf-8'))['vocab']
if engine != 'pocket':
    directory = app / 'piper-runtime/piper'
    spec = importlib.util.spec_from_file_location('espeakbridge', directory / 'espeakbridge.pyd')
    voice.bridge = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(voice.bridge)
    voice.bridge.initialize(str(directory / 'espeak-ng-data'))
us, gb = {'Language': 'en-US'}, {'Language': 'en-GB'}
if engine == 'kitten':
    voice.bridge.set_voice('en-us')
    for text in ('731', '44.7 MB', '0.16 seconds', 'Kitten and Piper', 'Hello, world!'):
        cleaned = normalize(text)
        assert not any(c.isdigit() for c in cleaned), 'NUMBERS_NOT_NORMALIZED'
        ipa = voice.phonemes(cleaned)
        assert len([c for c in ipa if c in VOCAB]) > 3, 'TEXT_DISAPPEARED'
    assert ',' in voice.phonemes('Hello, world!'), 'PUNCTUATION_LOST'
elif engine == 'kokoro':
    for text in ('731', '120.6 MB', '0.16 seconds', 'Kokoro and Piper', 'Hello, world!'):
        assert len(voice.phonemes(text, us)) > 3, 'TEXT_DISAPPEARED'
    assert voice.phonemes('tomato', us) != voice.phonemes('tomato', gb), 'BRITISH_FRONTEND_MISSING'
    assert ',' in voice.phonemes('Hello, world!', us), 'PUNCTUATION_LOST'
cache = VoiceCache(fixture, engine)
try:
    cache.get(catalog['Voices'][0]['Id'], Cancellation(None))
    raise AssertionError('CORRUPT_MODEL_ACCEPTED')
except ValueError as error:
    assert str(error) == 'MODEL_CORRUPTED'
assert cache.adapter is None, 'CORRUPT_MODEL_LOADED'
try:
    cache.voice(engine + ':unknown')
    raise AssertionError('UNKNOWN_VOICE_FALLBACK')
except ValueError as error:
    assert str(error) == 'VOICE_MISSING'
try:
    import socket
    socket.socket()
    raise AssertionError('NETWORK_ALLOWED')
except PermissionError:
    pass
try:
    open(fixture / 'forbidden.txt', 'w')
    raise AssertionError('DISK_WRITE_ALLOWED')
except PermissionError:
    pass
# Removal is performed by TemporaryDirectory; its audit events do not write files.
assert fixture.resolve().is_relative_to((root / 'artifacts/test-results').resolve())
temporary.cleanup()
print('ADDITIONAL_SPEECH_GUARDS_PASS ' + engine)
