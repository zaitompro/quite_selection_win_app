"""Offline Kokoro/VOICEVOX/Kitten worker. Models persist; text and audio do not."""
import gc
import hashlib
import json
import os
import sys
import time
from contextlib import closing
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from speech_runtime import APP_ROOT, Cancellation, Cancelled, memory, play_pcm, read_request, send, send_audio
from additional_text import speech_parts
from audio_prefetch import prefetch


class VoiceCache:
    def __init__(self, root, engine):
        self.root, self.engine = root, engine
        self.catalog = json.loads((APP_ROOT / 'speech' / (engine + '-models.json')).read_text(encoding='utf-8-sig'))
        self.adapter = None
        self.stamps = None

    def voice(self, voice_id):
        voice = next((v for v in self.catalog['Voices'] if v['Id'] == voice_id), None)
        if voice is None:
            raise ValueError('VOICE_MISSING')
        return voice

    def get(self, voice_id, cancellation):
        voice = self.voice(voice_id)
        paths = [self.root / f['Name'] for f in self.catalog['Files']]
        for path, item in zip(paths, self.catalog['Files']):
            if path.name != item['Name'] or not path.is_file() or path.stat().st_size != item['Bytes']:
                self.clear()
                raise ValueError('MODEL_MISSING')
        stamps = tuple((p.stat().st_mtime_ns, p.stat().st_ctime_ns, p.stat().st_size) for p in paths)
        hit = self.adapter is not None and stamps == self.stamps
        if not hit:
            self.clear()
            for path, item in zip(paths, self.catalog['Files']):
                cancellation.check()
                with path.open('rb') as source:
                    if hashlib.file_digest(source, 'sha256').hexdigest() != item['Sha256']:
                        raise ValueError('MODEL_CORRUPTED')
            if self.engine == 'kokoro':
                from kokoro_engine import KokoroEngine
                self.adapter = KokoroEngine(self.root)
            elif self.engine == 'kitten':
                from kitten_engine import KittenEngine
                self.adapter = KittenEngine(self.root)
            elif self.engine == 'pocket':
                from pocket_engine import PocketEngine
                self.adapter = PocketEngine(self.root)
            elif self.engine == 'voicevox':
                from voicevox_engine import VoicevoxEngine
                self.adapter = VoicevoxEngine(self.root)
            else:
                raise ValueError('INVALID_REQUEST')
            self.stamps = stamps
        loaded = self.adapter.load(voice, cancellation)
        return self.adapter, voice, hit and loaded

    def clear(self):
        if self.adapter is not None:
            self.adapter.close()
        self.adapter = None
        self.stamps = None
        gc.collect()

    def configure(self, ids, cancellation):
        if not isinstance(ids, list) or len(ids) > 2 or len(set(ids)) != len(ids):
            raise ValueError('INVALID_REQUEST')
        for voice_id in ids:
            self.voice(voice_id)
        if not ids:
            self.clear()
        else:
            for voice_id in ids:
                self.get(voice_id, cancellation)
            self.adapter.retain([self.voice(v) for v in ids])


def snapshot(cache):
    final = memory()
    return {'WorkingSetBytes': final.WorkingSetSize, 'PeakWorkingSetBytes': final.PeakWorkingSetSize,
            'PrivateBytes': final.PrivateUsage, 'PeakPrivateBytes': final.PeakPagefileUsage,
            'ProcessId': os.getpid(), 'RetainedModels': cache.adapter.count if cache.adapter else 0}


def synthesize(request, cache, cancellation):
    started = time.perf_counter()
    kind = request['Kind']
    if kind == cache.engine + '-status':
        return {'Success': True, **snapshot(cache)}
    if kind == cache.engine + '-configure':
        cache.configure(request.get('VoiceIds'), cancellation)
        return {'Success': True, 'LoadMilliseconds': (time.perf_counter() - started) * 1000, **snapshot(cache)}
    text, speed, voice_id = request['Text'], request['Speed'], request['VoiceId']
    if not isinstance(text, str) or not text.strip() or len(text.encode('utf-16-le')) // 2 > 60_000:
        raise ValueError('INVALID_TEXT')
    if type(speed) is not int or not 0 <= speed <= 100 or kind not in ('speak', 'synthesize-test', 'synthesize-stream'):
        raise ValueError('INVALID_REQUEST')
    adapter, voice, hit = cache.get(voice_id, cancellation)
    loaded = memory()
    load_ms = (time.perf_counter() - started) * 1000
    rate = 0.5 + speed / 100 if speed <= 50 else 1 + (speed - 50) / 50
    total_bytes, chunks, processed = 0, 0, 0
    total_seconds, first_audio_ms, synthesis_ms = 0.0, 0.0, 0.0
    def generate():
        nonlocal processed
        for part in speech_parts(text, cache.engine):
            cancellation.check()
            processed += len(part.encode('utf-16-le')) // 2
            if not part.strip():
                continue
            before = time.perf_counter()
            for audio, sample_rate in adapter.synthesize(part, voice, rate, cancellation):
                elapsed = (time.perf_counter() - before) * 1000
                cancellation.check()
                if not 0 < len(audio) <= 16 * 1024 * 1024:
                    raise ValueError('AUDIO_LIMIT')
                yield audio, sample_rate, elapsed
                before = time.perf_counter()

    source = prefetch(generate(), cancellation) if kind == 'speak' else generate()
    underrun_ms, maximum_underrun_ms, playback_ended = 0.0, 0.0, None
    with closing(source):
        for audio, sample_rate, elapsed in source:
            cancellation.check()
            synthesis_ms += elapsed
            if playback_ended is not None:
                wait_ms = (time.perf_counter() - playback_ended) * 1000
                underrun_ms += wait_ms
                maximum_underrun_ms = max(maximum_underrun_ms, wait_ms)
            if not chunks:
                first_audio_ms = (time.perf_counter() - started) * 1000
                send({'Stage': 'first-audio'})
            chunks += 1
            total_bytes += len(audio) + 44
            total_seconds += len(audio) / (sample_rate * 2)
            if kind == 'speak':
                play_pcm(audio, sample_rate, cancellation)
                playback_ended = time.perf_counter()
            elif kind == 'synthesize-stream':
                send_audio(audio, sample_rate, cancellation)
            del audio
    if not chunks:
        raise ValueError('EMPTY_AUDIO')
    return {'Success': True, 'AudioBytes': total_bytes, 'Chunks': chunks,
            'ProcessedCharacters': processed, 'AudioSeconds': total_seconds, 'CacheHit': hit,
            'LoadMilliseconds': load_ms, 'FirstAudioMilliseconds': first_audio_ms,
            'SynthesisMilliseconds': synthesis_ms, 'LoadWorkingSetBytes': loaded.WorkingSetSize,
            'PlaybackUnderrunMilliseconds': underrun_ms, 'MaxPlaybackUnderrunMilliseconds': maximum_underrun_ms,
            'LoadPrivateBytes': loaded.PrivateUsage, **snapshot(cache)}


def serve_one(cache, event_name):
    request = read_request()
    cancellation = Cancellation(event_name)
    try:
        result = synthesize(request, cache, cancellation)
    except Exception as error:
        allowed = {'VOICE_MISSING', 'MODEL_MISSING', 'MODEL_CORRUPTED', 'INVALID_TEXT', 'INVALID_REQUEST'}
        failure = error.args[0] if isinstance(error, ValueError) and error.args and isinstance(error.args[0], str) and error.args[0] in allowed else 'SPEECH_FAILURE'
        if cancellation.cancelled.is_set() or isinstance(error, Cancelled):
            failure = 'CANCELLED'
        result = {'Success': False, 'Failure': failure}
    finally:
        cancellation.close()
        request.clear()
    send(result)


if __name__ == '__main__':
    cache = VoiceCache(Path(sys.argv[1]), sys.argv[3])
    while True:
        try:
            serve_one(cache, sys.argv[2] or None)
        except EOFError:
            break
        except Exception:
            send({'Success': False, 'Failure': 'SPEECH_FAILURE'})
            break
    cache.clear()
