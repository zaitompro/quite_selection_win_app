"""Kokoro v1 INT8 inference using bundled ORT and eSpeak's native IPA bridge."""
import importlib.util
import json
import os
import sys
from speech_runtime import APP_ROOT
sys.path.insert(0, str(APP_ROOT / 'piper-runtime'))
import numpy as np
import onnxruntime as ort


class KokoroEngine:
    count = 1

    def __init__(self, root):
        ort.disable_telemetry_events()
        options = ort.SessionOptions()
        options.intra_op_num_threads = min(8, os.cpu_count() or 1)
        options.inter_op_num_threads = 1
        # Reuse intermediate buffers instead of reallocating every reading.
        # Measured together with 1/2/4/8 threads; precision/model are unchanged.
        options.enable_cpu_mem_arena = True
        options.enable_mem_pattern = True
        options.log_severity_level = 4
        options.add_session_config_entry('session.intra_op.allow_spinning', '0')
        options.add_session_config_entry('session.inter_op.allow_spinning', '0')
        self.session = ort.InferenceSession(str(root / 'kokoro-v1.0.int8.onnx'), sess_options=options, providers=['CPUExecutionProvider'])
        self.voices = np.load(root / 'voices-v1.0.bin', allow_pickle=False)
        self.vocab = json.loads((APP_ROOT / 'speech' / 'kokoro-config.json').read_text(encoding='utf-8'))['vocab']
        directory = APP_ROOT / 'piper-runtime' / 'piper'
        spec = importlib.util.spec_from_file_location('espeakbridge', directory / 'espeakbridge.pyd')
        self.bridge = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.bridge)
        self.bridge.initialize(str(directory / 'espeak-ng-data'))

    def load(self, voice, cancellation):
        cancellation.check()
        if voice['Speaker'] not in self.voices:
            raise ValueError('VOICE_MISSING')
        return True

    def retain(self, voices):
        pass  # All profiles share one model; arrays are read from NPZ on demand.

    def phonemes(self, text, voice):
        self.bridge.set_voice('en-us' if voice['Language'] == 'en-US' else 'en')
        # Block eSpeak phoneme-input markup; punctuation is returned separately by the bridge.
        text = text.replace('[[', '[ [').replace(']]', '] ]')
        clauses = self.bridge.get_phonemes(text)
        return ' '.join(''.join(p for p in phones + punctuation if p in self.vocab) for phones, punctuation, _ in clauses).strip()

    def synthesize(self, text, voice, rate, cancellation):
        phonemes = self.phonemes(text, voice)
        if not phonemes:
            return
        profile = self.voices[voice['Speaker']]
        # Never silently truncate input at the model's 510-phoneme limit.
        while phonemes:
            end = min(510, len(phonemes))
            if end < len(phonemes):
                boundary = phonemes.rfind(' ', 0, end)
                if boundary > 0:
                    end = boundary + 1
            part, phonemes = phonemes[:end], phonemes[end:]
            tokens = [self.vocab[p] for p in part]
            if not tokens:
                continue
            cancellation.check()
            run = ort.RunOptions()
            run.log_severity_level = 4
            cancellation.set_options(run)
            try:
                audio = self.session.run(None, {'tokens': np.array([[0, *tokens, 0]], dtype=np.int64),
                    'style': profile[min(len(tokens), len(profile)) - 1], 'speed': np.array([rate], dtype=np.float32)}, run)[0].ravel()
            finally:
                cancellation.options = None
            cancellation.check()
            # Keep 40 ms around speech, trimming only near-silent model padding.
            active = np.flatnonzero(np.abs(audio) > 0.005)
            if active.size:
                audio = audio[max(0, int(active[0]) - 960):min(len(audio), int(active[-1]) + 961)]
            yield (np.clip(audio, -1, 1) * 32767).astype('<i2').tobytes(), 24000

    def close(self):
        self.voices.close()
        self.session = None
