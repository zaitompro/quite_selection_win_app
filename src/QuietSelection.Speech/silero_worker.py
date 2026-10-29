"""Silero CPU worker: shared runtime, one model per language, bounded offline audio."""
import contextlib
import ctypes
import gc
import hashlib
import html
import json
import os
from pathlib import Path
import sys
import time
sys.path.insert(0, str(Path(__file__).resolve().parent))
from speech_runtime import (APP_ROOT, Cancellation, Cancelled, memory, pieces, play_pcm,
                            read_request, send)
from silero_text import normalize
from silero_english import normalize as normalize_english
from silero_latin import EnglishPronunciation

sys.path.insert(0, str(APP_ROOT / "silero-runtime"))
SAMPLE_RATE = 24000


def ssml(text, speed):
    # Only our fixed prosody tag is executable. Source text cannot introduce SSML instructions.
    rate = "medium" if speed == 50 else ("x-slow" if speed < 25 else "slow") if speed < 50 else ("fast" if speed <= 75 else "x-fast")
    text = "".join(c if c in "\n\r\t" or ord(c) >= 32 else " " for c in text)
    # V5.6 scans raw SSML for * / ** emphasis before XML parsing. Preserve literal
    # characters through entities so ordinary Markdown cannot activate that syntax.
    escaped = html.escape(text, quote=True).replace("*", "&#42;")
    return '<speak><prosody rate="' + rate + '">' + escaped + "</prosody></speak>"


class VoiceCache:
    def __init__(self, root):
        self.root = root
        self.catalog = json.loads((APP_ROOT / "speech" / "silero-model.json").read_text(encoding="utf-8-sig"))
        self.catalogs = [self.catalog, json.loads((APP_ROOT / "speech" / "silero-english-model.json").read_text(encoding="utf-8-sig"))]
        self.loaded = {}
        self.stamps = {}
        self.latin = EnglishPronunciation(APP_ROOT / "piper-runtime" / "piper")

    def voice(self, voice_id):
        voice = next((v for catalog in self.catalogs for v in catalog["Voices"] if v["Id"] == voice_id), None)
        if voice is None:
            raise ValueError("VOICE_MISSING")
        return voice

    def catalog_for(self, voice_id):
        self.voice(voice_id)
        return next(c for c in self.catalogs if any(v["Id"] == voice_id for v in c["Voices"]))

    def get(self, voice_id, cancellation):
        item = self.catalog_for(voice_id)["File"]
        key = item["Name"]
        path = self.root / item["Name"]
        if path.name != item["Name"] or not path.is_file() or path.stat().st_size != item["Bytes"]:
            self.loaded.pop(key, None)
            self.stamps.pop(key, None)
            raise ValueError("MODEL_MISSING")
        info = path.stat()
        stamp = (info.st_mtime_ns, info.st_ctime_ns, info.st_size)
        if key in self.loaded and stamp == self.stamps.get(key):
            return self.loaded[key], True
        self.loaded.pop(key, None)
        self.stamps.pop(key, None)
        gc.collect()
        cancellation.check()
        with path.open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != item["Sha256"]:
                raise ValueError("MODEL_CORRUPTED")
            source.seek(0)
            with contextlib.redirect_stdout(sys.stderr):
                import torch
                torch.set_num_threads(2)
                if torch.get_num_interop_threads() != 1:
                    torch.set_num_interop_threads(1)
                model = torch.package.PackageImporter(source).load_pickle("tts_models", "model")
                model.to(torch.device("cpu"))
                # The pinned V5.6 SSML parser calls ext_alph.keys() unconditionally
                # for mixed alphabets, although Russian packages store None here.
                # An empty mapping preserves the intended no-transliteration mode.
                for package in getattr(model, "packages", (model,)):
                    if getattr(package, "ext_alph", None) is None:
                        package.ext_alph = {}
        cancellation.check()
        self.loaded[key], self.stamps[key] = model, stamp
        return model, False

    def configure(self, ids, cancellation):
        if not isinstance(ids, list) or len(ids) > 2:
            raise ValueError("INVALID_REQUEST")
        keys = [self.catalog_for(voice_id)["File"]["Name"] for voice_id in ids]
        if len(keys) != len(set(keys)):
            raise ValueError("INVALID_REQUEST")
        for key in list(self.loaded):
            if key not in keys:
                del self.loaded[key]
                self.stamps.pop(key, None)
        gc.collect()
        for voice_id in ids:
            model, hit = self.get(voice_id, cancellation)
            if not hit:
                english = self.catalog_for(voice_id).get("Language") == "en-US"
                render(model, "This is an English voice." if english else "Проверка русского голоса.",
                       self.voice(voice_id)["Speaker"], 50, cancellation, english)


def render(model, text, speaker, speed, cancellation, english=False):
    import torch
    cancellation.check()
    with torch.inference_mode(), contextlib.redirect_stdout(sys.stderr):
        options = {} if english else {"put_accent": True, "put_yo": True}
        audio = model.apply_tts(ssml_text=ssml(text, speed), speaker=speaker,
                                sample_rate=SAMPLE_RATE, **options)
    cancellation.check()
    if audio.ndim != 1 or not 0 < audio.numel() <= 8 * 1024 * 1024:
        raise ValueError("AUDIO_LIMIT")
    pcm = audio.clamp(-1, 1).mul(32767).to(torch.int16).contiguous()
    return ctypes.string_at(pcm.data_ptr(), pcm.numel() * 2)


def snapshot(cache):
    info = memory()
    return {"WorkingSetBytes": info.WorkingSetSize, "PeakWorkingSetBytes": info.PeakWorkingSetSize,
            "PrivateBytes": info.PrivateUsage, "PeakPrivateBytes": info.PeakPagefileUsage,
            "ProcessId": os.getpid(), "RetainedModels": len(cache.loaded)}


def synthesize(request, cache, cancellation):
    started = time.perf_counter()
    kind = request["Kind"]
    if kind == "silero-status":
        return {"Success": True, **snapshot(cache)}
    if kind == "silero-configure":
        cache.configure(request.get("VoiceIds"), cancellation)
        return {"Success": True, "LoadMilliseconds": (time.perf_counter() - started) * 1000, **snapshot(cache)}
    text, speed, voice_id = request["Text"], request["Speed"], request["VoiceId"]
    if not isinstance(text, str) or not text.strip() or len(text.encode("utf-16-le")) // 2 > 60_000:
        raise ValueError("INVALID_TEXT")
    if type(speed) is not int or not 0 <= speed <= 100 or kind not in ("speak", "synthesize-test", "synthesize-stream"):
        raise ValueError("INVALID_REQUEST")
    model, hit = cache.get(voice_id, cancellation)
    speaker = cache.voice(voice_id)["Speaker"]
    english = cache.catalog_for(voice_id).get("Language") == "en-US"
    loaded = memory()
    load_ms = (time.perf_counter() - started) * 1000
    total_bytes, chunks = 0, 0
    processed = len(text.encode("utf-16-le")) // 2
    total_seconds, first_audio_ms, synthesis_ms = 0.0, 0.0, 0.0
    # Expansion must happen before bounding inference pieces (numbers can become
    # much longer). Keep accounting in original UTF-16 characters for the caller.
    for part in pieces(normalize_english(text) if english else normalize(text, cache.latin)):
        cancellation.check()
        if not part.strip():
            continue
        before = time.perf_counter()
        audio = render(model, part, speaker, speed, cancellation, english)
        synthesis_ms += (time.perf_counter() - before) * 1000
        cancellation.check()
        if not chunks:
            first_audio_ms = (time.perf_counter() - started) * 1000
            send({"Stage": "first-audio"})
        chunks += 1
        total_bytes += len(audio) + 44
        total_seconds += len(audio) / (SAMPLE_RATE * 2)
        if kind == "speak":
            play_pcm(audio, SAMPLE_RATE, cancellation)
        elif kind == "synthesize-stream":
            from speech_runtime import send_audio
            send_audio(audio, SAMPLE_RATE, cancellation)
        del audio
    if not chunks:
        raise ValueError("EMPTY_AUDIO")
    return {"Success": True, "AudioBytes": total_bytes, "Chunks": chunks,
            "ProcessedCharacters": processed, "AudioSeconds": total_seconds, "CacheHit": hit,
            "LoadMilliseconds": load_ms, "FirstAudioMilliseconds": first_audio_ms,
            "SynthesisMilliseconds": synthesis_ms, "LoadWorkingSetBytes": loaded.WorkingSetSize,
            "LoadPrivateBytes": loaded.PrivateUsage, **snapshot(cache)}


def serve_one(cache, event_name):
    request = read_request()
    cancellation = Cancellation(event_name)
    try:
        result = synthesize(request, cache, cancellation)
    except Exception as error:
        allowed = {"VOICE_MISSING", "MODEL_MISSING", "MODEL_CORRUPTED", "INVALID_TEXT", "INVALID_REQUEST"}
        failure = error.args[0] if isinstance(error, ValueError) and error.args and isinstance(error.args[0], str) and error.args[0] in allowed else "SPEECH_FAILURE"
        if cancellation.cancelled.is_set() or isinstance(error, Cancelled):
            failure = "CANCELLED"
        result = {"Success": False, "Failure": failure}
    finally:
        cancellation.close()
        request.clear()
    send(result)


if __name__ == "__main__":
    cache = VoiceCache(Path(sys.argv[1]))
    while True:
        try:
            serve_one(cache, sys.argv[2] if len(sys.argv) > 2 else None)
        except EOFError:
            break
        except Exception:
            send({"Success": False, "Failure": "SPEECH_FAILURE"})
            break
