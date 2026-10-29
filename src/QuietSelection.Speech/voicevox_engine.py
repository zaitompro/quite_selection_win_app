"""VOICEVOX Core 0.17 C ABI, CPU only. No HTTP server or disk audio."""
import ctypes as c
import io
import json
import os
import wave
from speech_runtime import APP_ROOT


class LoadOptions(c.Structure):
    _fields_ = [('filename', c.c_char_p)]


class InitializeOptions(c.Structure):
    _fields_ = [('acceleration_mode', c.c_int32), ('cpu_num_threads', c.c_uint16)]


class ModelOptions(c.Structure):
    _fields_ = [('on_existing', c.c_int32)]


class SynthesisOptions(c.Structure):
    _fields_ = [('enable_interrogative_upspeak', c.c_bool)]


class VoicevoxEngine:
    def __init__(self, root):
        self.root, self.models = root, {}
        self.synth = c.c_void_p()
        self.jtalk = c.c_void_p()
        directory = APP_ROOT / 'voicevox-runtime'
        self.dll_directory = os.add_dll_directory(str(directory))
        self.dll = c.CDLL(str(directory / 'voicevox_core.dll'))
        ptr, out = c.c_void_p, c.POINTER(c.c_void_p)
        self.bind('onnxruntime_load_once', [LoadOptions, out])
        self.bind('open_jtalk_rc_new', [c.c_char_p, out])
        self.bind('open_jtalk_rc_delete', [ptr], None)
        self.bind('synthesizer_new', [ptr, ptr, InitializeOptions, out])
        self.bind('synthesizer_delete', [ptr], None)
        self.bind('voice_model_file_open', [c.c_char_p, out])
        self.bind('voice_model_file_delete', [ptr], None)
        self.bind('voice_model_file_id', [ptr, ptr], None)
        self.bind('synthesizer_load_voice_model', [ptr, ptr, ModelOptions])
        self.bind('synthesizer_unload_voice_model', [ptr, ptr])
        self.bind('synthesizer_create_audio_query', [ptr, c.c_char_p, c.c_uint32, out])
        self.bind('synthesizer_synthesis', [ptr, c.c_char_p, c.c_uint32, SynthesisOptions, c.POINTER(c.c_size_t), out])
        self.bind('json_free', [ptr], None)
        self.bind('wav_free', [ptr], None)
        try:
            runtime = ptr()
            self.check(self.onnxruntime_load_once(LoadOptions(str(directory / 'voicevox_onnxruntime.dll').encode('utf-8')), c.byref(runtime)))
            self.check(self.open_jtalk_rc_new(str(directory / 'dictionary').encode('utf-8'), c.byref(self.jtalk)))
            # Measured 1/2/4/8 on the target CPU; cap to available logical CPUs.
            self.check(self.synthesizer_new(runtime, self.jtalk, InitializeOptions(1, min(8, os.cpu_count() or 1)), c.byref(self.synth)))
        except Exception:
            self.close()
            raise

    def bind(self, name, arguments, result=c.c_int32):
        function = getattr(self.dll, 'voicevox_' + name)
        function.argtypes, function.restype = arguments, result
        setattr(self, name, function)

    @staticmethod
    def check(code):
        if code != 0:
            raise RuntimeError('VOICEVOX_NATIVE_FAILURE', code)

    @property
    def count(self):
        return len(self.models)

    def load(self, voice, cancellation):
        cancellation.check()
        name = voice['Model']
        if name in self.models:
            return True
        model = c.c_void_p()
        self.check(self.voice_model_file_open(str(self.root / name).encode('utf-8'), c.byref(model)))
        try:
            identity = (c.c_uint8 * 16)()
            self.voice_model_file_id(model, identity)
            self.check(self.synthesizer_load_voice_model(self.synth, model, ModelOptions(0)))
            self.models[name] = identity
        finally:
            self.voice_model_file_delete(model)
        cancellation.check()
        return False

    def retain(self, voices):
        keep = {v['Model'] for v in voices}
        for name in list(self.models):
            if name not in keep:
                self.check(self.synthesizer_unload_voice_model(self.synth, self.models[name]))
                del self.models[name]

    def synthesize(self, text, voice, rate, cancellation):
        cancellation.check()
        query = c.c_void_p()
        self.check(self.synthesizer_create_audio_query(self.synth, text.encode('utf-8'), voice['Style'], c.byref(query)))
        try:
            data = json.loads(c.string_at(query).decode('utf-8'))
        finally:
            self.json_free(query)
        data['speedScale'] = rate
        data['outputSamplingRate'] = 24000
        data['outputStereo'] = False
        cancellation.check()
        wav, size = c.c_void_p(), c.c_size_t()
        self.check(self.synthesizer_synthesis(self.synth, json.dumps(data).encode('utf-8'), voice['Style'], SynthesisOptions(True), c.byref(size), c.byref(wav)))
        try:
            cancellation.check()
            if not 44 < size.value <= 16 * 1024 * 1024:
                raise ValueError('AUDIO_LIMIT')
            with wave.open(io.BytesIO(c.string_at(wav, size.value)), 'rb') as source:
                if source.getnchannels() != 1 or source.getsampwidth() != 2 or source.getframerate() != 24000:
                    raise ValueError('AUDIO_FORMAT')
                audio = source.readframes(source.getnframes())
        finally:
            self.wav_free(wav)
        yield audio, 24000

    def close(self):
        if self.synth:
            self.synthesizer_delete(self.synth)
            self.synth = c.c_void_p()
        if self.jtalk:
            self.open_jtalk_rc_delete(self.jtalk)
            self.jtalk = c.c_void_p()
        self.models.clear()
        self.dll_directory.close()
