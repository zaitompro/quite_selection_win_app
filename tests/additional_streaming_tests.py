"""Segmentation, bounded lookahead, failure and cancellation regressions."""
from pathlib import Path
import sys
import threading
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src/QuietSelection.Speech'))
from additional_text import speech_parts
from audio_prefetch import prefetch


class Stopped(Exception):
    pass


class Cancellation:
    def __init__(self):
        self.cancelled = threading.Event()
        self.options = None

    def check(self):
        if self.cancelled.is_set():
            raise Stopped()


class StreamingTests(unittest.TestCase):
    def test_japanese_without_spaces(self):
        self.assertEqual(list(speech_parts('これは読み上げのテストです。話す速度を変更できます。', 'voicevox')),
                         ['これは読み上げのテストです。', '話す速度を変更できます。'])

    def test_lossless(self):
        for engine in ('kokoro', 'voicevox', 'kitten', 'pocket'):
            for text in ('Hello, world! Next sentence.', '「最初です。」次です！\n三番目です。',
                         'Version 0.1.0.17 uses 1,234 MB at 10:30. Next.',
                         'word ' * 2000, '字' * 1000, '🗣️' * 1000, ' \n\n'):
                parts = list(speech_parts(text, engine))
                self.assertEqual(''.join(parts), text)
                self.assertLessEqual(max(map(len, parts)), 141)

    def test_numbers_not_split(self):
        parts = list(speech_parts('Version 0.1.0.17 uses 1,234 MB at 10:30. Next.', 'kokoro'))
        self.assertEqual(parts[0], 'Version 0.1.0.17 uses 1,234 MB at 10:30.')

    def test_lookahead_is_bounded_and_overlaps_consumer(self):
        cancellation = Cancellation()
        second_ready = threading.Event()
        produced = []
        def source():
            for index in range(5):
                produced.append(index)
                if index == 1:
                    second_ready.set()
                yield index
        stream = prefetch(source(), cancellation)
        self.assertEqual(next(stream), 0)
        self.assertTrue(second_ready.wait(2), 'Next fragment was not prepared during playback')
        self.assertEqual(produced, [0, 1], 'More than one next fragment was generated')
        self.assertEqual(list(stream), [1, 2, 3, 4])
        self.assertFalse(cancellation.cancelled.is_set())

    def test_failure_after_first_audio_propagates(self):
        def source():
            yield 1
            raise ValueError('synthetic error')
        stream = prefetch(source(), Cancellation())
        self.assertEqual(next(stream), 1)
        with self.assertRaisesRegex(ValueError, 'synthetic error'):
            next(stream)

    def test_cancel_during_inference_joins_before_reuse(self):
        cancellation = Cancellation()
        computing = threading.Event()
        closed = threading.Event()
        def source():
            try:
                yield 1
                computing.set()
                while not cancellation.cancelled.wait(0.01):
                    pass
                cancellation.check()
                yield 2
            finally:
                closed.set()
        stream = prefetch(source(), cancellation)
        self.assertEqual(next(stream), 1)
        self.assertTrue(computing.wait(2))
        stream.close()
        self.assertTrue(closed.is_set())

    def test_close_with_ready_buffer(self):
        cancellation = Cancellation()
        ready = threading.Event()
        def source():
            yield 1
            ready.set()
            yield 2
            raise AssertionError('Read past one fragment of lookahead')
        stream = prefetch(source(), cancellation)
        self.assertEqual(next(stream), 1)
        self.assertTrue(ready.wait(2))
        stream.close()


if __name__ == '__main__':
    unittest.main()
