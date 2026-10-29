"""Offline KittenTTS Mini 0.8 adapter; see third-party/kitten/NOTICE.txt.

Token inventory, spacing, style indexing and padding follow KittenML 0.8.1.
Use the existing native eSpeak bridge instead of a second phonemizer runtime.
"""
import importlib.util
import os
import re
import sys
from speech_runtime import APP_ROOT
from silero_english import normalize
sys.path.insert(0, str(APP_ROOT / 'piper-runtime'))
import numpy as np
import onnxruntime as ort

SYMBOLS = (
    "$" + ';:,.!?¡¿—…"«»"" ' + 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz'
    + "ɑɐɒæɓʙβɔɕçɗɖðʤəɘɚɛɜɝɞɟʄɡɠɢʛɦɧħɥʜɨɪʝɭɬɫɮʟɱɯɰŋɳɲɴøɵɸθœɶʘɹɺɾɻʀʁɽʂʃʈʧʉʊʋⱱʌɣɤʍχʎʏʑʐʒʔʡʕʢǀǁǂǃˈˌːˑʼʴʰʱʲʷˠˤ˞↓↑→↗↘'̩'ᵻ"
)
VOCAB = {symbol: index for index, symbol in enumerate(SYMBOLS)}


class KittenEngine:
    count = 1

    def __init__(self, root):
        ort.disable_telemetry_events()
        options = ort.SessionOptions()
        options.intra_op_num_threads = min(4, os.cpu_count() or 1)
        options.inter_op_num_threads = 1
        options.log_severity_level = 4
        options.add_session_config_entry('session.intra_op.allow_spinning', '0')
        options.add_session_config_entry('session.inter_op.allow_spinning', '0')
        self.session = ort.InferenceSession(str(root / 'kitten_tts_mini_v0_8.onnx'),
            sess_options=options, providers=['CPUExecutionProvider'])
        self.voices = np.load(root / 'voices.npz', allow_pickle=False)
        directory = APP_ROOT / 'piper-runtime' / 'piper'
        spec = importlib.util.spec_from_file_location('espeakbridge', directory / 'espeakbridge.pyd')
        self.bridge = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.bridge)
        self.bridge.initialize(str(directory / 'espeak-ng-data'))
        self.bridge.set_voice('en-us')

    def load(self, voice, cancellation):
        cancellation.check()
        if voice['Speaker'] not in self.voices:
            raise ValueError('VOICE_MISSING')
        return True

    def retain(self, voices):
        pass  # All eight profiles share one ONNX session.

    def phonemes(self, text):
        text = text.replace('[[', '[ [').replace(']]', '] ]')
        clauses = self.bridge.get_phonemes(text)
        ipa = ' '.join(''.join(phones + punctuation) for phones, punctuation, _ in clauses)
        return ' '.join(re.findall(r'\w+|[^\w\s]', ipa))

    def synthesize(self, text, voice, rate, cancellation):
        text = normalize(text).strip()
        if not text:
            return
        if text[-1] not in '.!?,;:':
            text += ','
        phonemes = self.phonemes(text)
        profile = self.voices[voice['Speaker']]
        while phonemes:
            end = min(500, len(phonemes))
            if end < len(phonemes):
                boundary = phonemes.rfind(' ', 0, end)
                if boundary > 0:
                    end = boundary + 1
            part, phonemes = phonemes[:end], phonemes[end:]
            tokens = [VOCAB[p] for p in part if p in VOCAB]
            if not tokens:
                continue
            cancellation.check()
            reference = min(len(text), profile.shape[0] - 1)
            run = ort.RunOptions()
            run.log_severity_level = 4
            cancellation.set_options(run)
            try:
                audio = self.session.run(None, {
                    'input_ids': np.array([[0, *tokens, 10, 0]], dtype=np.int64),
                    'style': profile[reference:reference + 1],
                    'speed': np.array([rate], dtype=np.float32),
                }, run)[0].ravel()
            finally:
                cancellation.options = None
            cancellation.check()
            # Official 0.8.1 output padding: 5000 samples at 24 kHz.
            if len(audio) <= 5000 or not np.isfinite(audio).all():
                raise ValueError('EMPTY_AUDIO')
            audio = audio[:-5000]
            yield (np.clip(audio, -1, 1) * 32767).astype('<i2').tobytes(), 24000

    def close(self):
        self.voices.close()
        self.session = None
