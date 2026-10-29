"""Pocket TTS CPU streaming using verified local weights and stock profiles."""
import contextlib
import os
import sys
from speech_runtime import APP_ROOT
from silero_english import normalize

sys.path.insert(0, str(APP_ROOT / "silero-runtime"))
sys.path.insert(0, str(APP_ROOT / "pocket-runtime"))
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["TOKENIZERS_PARALLELISM"] = "false"
import numpy as np
import torch
from pocket_tts import TTSModel
from pocket_tts.utils.config import load_config
from speech_tempo import change_tempo


class PocketEngine:
    count = 1

    def __init__(self, root):
        self.root = root
        self.states = {}
        torch.set_num_threads(2)
        if torch.get_num_interop_threads() != 1:
            torch.set_num_interop_threads(1)
        config = load_config(root / "english-upstream.yaml")
        config.weights_path = str(root / "model.safetensors")
        config.weights_path_without_voice_cloning = None
        config.flow_lm.lookup_table.tokenizer_path = str(root / "tokenizer.json")
        # Pinned 3.3.0 loader accepts a validated in-memory config. No URL
        # resolver, cache directory or user audio is involved.
        with contextlib.redirect_stdout(sys.stderr):
            self.model = TTSModel._from_pydantic_config_with_weights(
                config, temp=config.default_temperature, sampler_decode_steps=1,
                noise_clamp=None, eos_threshold=-4.0)
        self.model.has_voice_cloning = False
        self.model.max_decoder_frames_per_call = 2
        self.model.eval()

    def load(self, voice, cancellation):
        cancellation.check()
        speaker = voice["Speaker"]
        hit = speaker in self.states
        if not hit:
            # At most two selected profiles, alongside one shared model.
            if len(self.states) >= 2:
                self.states.pop(next(iter(self.states)))
            self.states[speaker] = self.model.get_state_for_audio_prompt(
                self.root / (speaker + ".safetensors"))
        cancellation.check()
        return hit

    def retain(self, voices):
        wanted = {v["Speaker"] for v in voices}
        self.states = {k: v for k, v in self.states.items() if k in wanted}

    def synthesize(self, text, voice, rate, cancellation):
        text = normalize(text).strip()
        if not text:
            return
        stream = self.model.generate_audio_stream(self.states[voice["Speaker"]],
            text, max_tokens=50, copy_state=True, stop=cancellation.cancelled)
        completed = False

        def arrays():
            for chunk in stream:
                cancellation.check()
                audio = chunk.detach().cpu().numpy().ravel()
                if not np.isfinite(audio).all():
                    raise ValueError("INVALID_AUDIO")
                yield audio

        try:
            pending = np.empty(0, dtype=np.float32)
            for audio in change_tempo(arrays(), rate):
                cancellation.check()
                pending = np.concatenate((pending, audio))
                # Keep WinMM/IPC blocks at 80 ms even when WSOLA produces
                # 20 ms overlaps, avoiding a device round-trip per overlap.
                while len(pending) >= 1920:
                    yield (np.clip(pending[:1920], -1, 1) * 32767).astype("<i2").tobytes(), 24000
                    pending = pending[1920:].copy()
                    cancellation.check()
            if len(pending):
                yield (np.clip(pending, -1, 1) * 32767).astype("<i2").tobytes(), 24000
            completed = True
        finally:
            if not completed:
                cancellation.cancelled.set()
            # Upstream joins its decoder when exhausted. Drain on cancellation
            # before acknowledging or reusing the model; never emit late PCM.
            for _ in stream:
                pass
            stream.close()

    def close(self):
        self.states.clear()
        self.model = None
