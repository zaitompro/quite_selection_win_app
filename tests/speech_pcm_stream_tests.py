"""Bounded in-memory PCM transport; no files, devices or models."""
import base64
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src/QuietSelection.Speech"))
import speech_runtime as runtime


class StreamTests(unittest.TestCase):
    def test_lossless_bounded_frames(self):
        data = bytes(range(256)) * 2400
        frames = []
        old = runtime.send
        runtime.send = frames.append
        class Cancellation:
            def check(self): pass
        try:
            runtime.send_audio(data, 24000, Cancellation())
        finally:
            runtime.send = old
        decoded = [base64.b64decode(x["AudioData"]) for x in frames]
        self.assertEqual(b"".join(decoded), data)
        self.assertTrue(all(0 < len(x) <= 262144 for x in decoded))
        self.assertTrue(all(x["SampleRate"] == 24000 and x["Stage"] == "audio" for x in frames))

    def test_cancel_before_next_frame(self):
        frames = []
        old = runtime.send
        runtime.send = frames.append
        class Cancellation:
            def check(self):
                if frames: raise InterruptedError()
        try:
            with self.assertRaises(InterruptedError):
                runtime.send_audio(bytes(524288), 24000, Cancellation())
        finally:
            runtime.send = old
        self.assertEqual(len(frames), 1)

    def test_invalid_pcm_rejected(self):
        for data, rate in [(b"x", 24000), (b"xx", 0), (b"xx", 100000)]:
            with self.assertRaises(ValueError):
                runtime.send_audio(data, rate, None)


if __name__ == "__main__":
    unittest.main()
