"""Synthetic-only streaming tempo checks: duration, pitch, boundaries, bypass."""
from pathlib import Path
import sys
import unittest
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src/QuietSelection.Speech"))
from speech_tempo import change_tempo


class TempoTests(unittest.TestCase):
    def test_normal_speed_exact_samples(self):
        data = np.linspace(-0.8, 0.8, 1731, dtype=np.float32)
        result = np.concatenate(list(change_tempo([data[:51], data[51:]], 1)))
        np.testing.assert_array_equal(result, data)

    def test_duration_pitch_and_chunk_independence(self):
        sample_rate = 24000
        tone = (0.4 * np.sin(2 * np.pi * 220 * np.arange(sample_rate * 2) / sample_rate)).astype(np.float32)
        for rate in (0.5, 0.75, 1.22, 1.5, 2.0):
            with self.subTest(rate=rate):
                result = np.concatenate(list(change_tempo([tone], rate)))
                streamed = np.concatenate(list(change_tempo(
                    (tone[i:i + 137] for i in range(0, len(tone), 137)), rate)))
                np.testing.assert_allclose(result, streamed, atol=1e-6)
                self.assertEqual(len(result), round(len(tone) / rate))
                middle = result[2400:-2400]
                frequencies = np.fft.rfftfreq(len(middle), 1/sample_rate)
                peak = frequencies[np.argmax(np.abs(np.fft.rfft(middle)))]
                self.assertLess(abs(peak - 220), 3)
                self.assertLess(float(np.max(np.abs(np.diff(result)))), 0.1)

    def test_empty_silence_short(self):
        self.assertEqual(list(change_tempo([], 1.5)), [])
        for size in (1, 79, 1920):
            for rate in (0.5, 2.0):
                chunks = list(change_tempo([np.zeros(size, np.float32)], rate))
                result = np.concatenate(chunks) if chunks else np.empty(0)
                self.assertTrue(np.isfinite(result).all())
                self.assertEqual(len(result), round(size / rate))


if __name__ == "__main__":
    unittest.main()
