"""Persistent Piper CPU voice cache; request text/audio are released after each operation."""
import gc
import hashlib
import json
import os
import sys
import time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from speech_runtime import (APP_ROOT, Cancellation, Cancelled, memory, pieces, play_pcm,
                            read_request, send, deny_side_effects)
sys.path.insert(0, str(APP_ROOT / "piper-runtime"))


class InterruptibleSession:
    def __init__(self, session):
        self.session = session
        self.options = None

    def run(self, names, inputs):
        return self.session.run(names, inputs, self.options)


class VoiceCache:
    def __init__(self, model_root):
        self.root = model_root
        self.voices = {}
        self.catalog = json.loads((APP_ROOT / "speech" / "piper-models.json").read_text(encoding="utf-8-sig"))

    def model(self, voice_id):
        model = next((m for m in self.catalog if m["Id"] == voice_id), None)
        if model is None:
            raise ValueError("VOICE_MISSING")
        return model

    def get(self, voice_id, cancellation):
        model = self.model(voice_id)
        paths = [self.root / item["Name"] for item in model["Files"]]
        for path, item in zip(paths, model["Files"]):
            if path.name != item["Name"] or not path.is_file() or path.stat().st_size != item["Bytes"]:
                self.voices.pop(voice_id, None)
                raise ValueError("MODEL_MISSING")
        stamps = tuple((p.stat().st_mtime_ns, p.stat().st_ctime_ns, p.stat().st_size) for p in paths)
        cached = self.voices.get(voice_id)
        if cached and cached[1] == stamps:
            return cached[0], True
        self.voices.pop(voice_id, None)
        # A voice is hashed before loading. Changed files invalidate a resident entry.
        for path, item in zip(paths, model["Files"]):
            cancellation.check()
            with path.open("rb") as source:
                if hashlib.file_digest(source, "sha256").hexdigest() != item["Sha256"]:
                    raise ValueError("MODEL_CORRUPTED")
        import onnxruntime as ort
        from piper import PiperVoice
        from piper.config import PiperConfig
        ort.disable_telemetry_events()
        config = json.loads(paths[1].read_text(encoding="utf-8"))
        # Piper 1.0 voices such as Irina omit this field; upstream PiperConfig defaults to eSpeak.
        if config.get("phoneme_type", "espeak") != "espeak" or config["espeak"]["voice"] not in ("ru", "en", "en-us"):
            raise ValueError("UNSUPPORTED_PHONEMIZER")
        options = ort.SessionOptions()
        options.intra_op_num_threads = 2
        options.inter_op_num_threads = 1
        options.enable_cpu_mem_arena = False
        options.enable_mem_pattern = False
        options.log_severity_level = 4
        options.add_session_config_entry("session.intra_op.allow_spinning", "0")
        options.add_session_config_entry("session.inter_op.allow_spinning", "0")
        session = InterruptibleSession(ort.InferenceSession(str(paths[0]), sess_options=options, providers=["CPUExecutionProvider"]))
        cancellation.check()
        voice = PiperVoice(config=PiperConfig.from_dict(config), session=session, use_tashkeel=False)
        if len(self.voices) >= 2:
            self.voices.pop(next(iter(self.voices)))
        self.voices[voice_id] = (voice, stamps)
        return voice, False

    def configure(self, ids, cancellation):
        if not isinstance(ids, list) or len(ids) > 2 or len(set(ids)) != len(ids):
            raise ValueError("INVALID_REQUEST")
        for voice_id in ids:
            self.model(voice_id)
        for voice_id in list(self.voices):
            if voice_id not in ids:
                del self.voices[voice_id]
        gc.collect()
        for voice_id in ids:
            self.get(voice_id, cancellation)


def snapshot(cache):
    final = memory()
    return {"WorkingSetBytes": final.WorkingSetSize, "PeakWorkingSetBytes": final.PeakWorkingSetSize,
            "PrivateBytes": final.PrivateUsage, "PeakPrivateBytes": final.PeakPagefileUsage,
            "ProcessId": os.getpid(), "RetainedModels": len(cache.voices)}


def synthesize(request, cache, cancellation):
    started = time.perf_counter()
    kind = request["Kind"]
    if kind == "piper-status":
        return {"Success": True, **snapshot(cache)}
    if kind == "piper-configure":
        cache.configure(request.get("VoiceIds"), cancellation)
        return {"Success": True, "LoadMilliseconds": (time.perf_counter() - started) * 1000, **snapshot(cache)}
    text, speed, voice_id = request["Text"], request["Speed"], request["VoiceId"]
    if not isinstance(text, str) or not text.strip() or len(text.encode("utf-16-le")) // 2 > 60_000:
        raise ValueError("INVALID_TEXT")
    if type(speed) is not int or not 0 <= speed <= 100 or kind not in ("speak", "synthesize-test", "synthesize-stream"):
        raise ValueError("INVALID_REQUEST")
    voice, hit = cache.get(voice_id, cancellation)
    import onnxruntime as ort
    from piper.config import SynthesisConfig
    run_options = ort.RunOptions()
    run_options.log_severity_level = 4
    cancellation.set_options(run_options)
    voice.session.options = run_options
    loaded = memory()
    load_ms = (time.perf_counter() - started) * 1000
    rate = 0.5 + speed / 100 if speed <= 50 else 1 + (speed - 50) / 50
    synthesis = SynthesisConfig(length_scale=1 / rate)
    total_bytes, chunks, processed = 0, 0, 0
    total_seconds, first_audio_ms, synthesis_ms = 0.0, 0.0, 0.0
    try:
        for part in pieces(text):
            cancellation.check()
            processed += len(part.encode("utf-16-le")) // 2
            if not part.strip():
                continue
            part = part.replace("[[", "[ [").replace("]]", "] ]")
            generator = iter(voice.synthesize(part, syn_config=synthesis))
            while True:
                cancellation.check()
                before = time.perf_counter()
                try:
                    chunk = next(generator)
                except StopIteration:
                    break
                synthesis_ms += (time.perf_counter() - before) * 1000
                cancellation.check()
                if chunk.sample_width != 2 or chunk.sample_channels != 1:
                    raise ValueError("AUDIO_FORMAT")
                audio = chunk.audio_int16_bytes
                if not 0 < len(audio) <= 16 * 1024 * 1024:
                    raise ValueError("AUDIO_LIMIT")
                if not chunks:
                    first_audio_ms = (time.perf_counter() - started) * 1000
                    send({"Stage": "first-audio"})
                chunks += 1
                total_bytes += len(audio) + 44
                total_seconds += len(audio) / (chunk.sample_rate * 2)
                if kind == "speak":
                    play_pcm(audio, chunk.sample_rate, cancellation)
                elif kind == "synthesize-stream":
                    from speech_runtime import send_audio
                    send_audio(audio, chunk.sample_rate, cancellation)
                del audio, chunk
        if not chunks:
            raise ValueError("EMPTY_AUDIO")
        return {"Success": True, "AudioBytes": total_bytes, "Chunks": chunks,
                "ProcessedCharacters": processed, "AudioSeconds": total_seconds, "CacheHit": hit,
                "LoadMilliseconds": load_ms, "FirstAudioMilliseconds": first_audio_ms,
                "SynthesisMilliseconds": synthesis_ms, "LoadWorkingSetBytes": loaded.WorkingSetSize,
                "LoadPrivateBytes": loaded.PrivateUsage, **snapshot(cache)}
    finally:
        voice.session.options = None


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
        request.clear()  # A resident worker retains models, never request text or audio.
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


