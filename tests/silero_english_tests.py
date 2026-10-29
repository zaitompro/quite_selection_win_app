"""Number information must survive the English model's alphabet filter."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src/QuietSelection.Speech'))
from silero_english import normalize


class EnglishTests(unittest.TestCase):
    def test_information(self):
        cases = {
            'Silero takes 57.2 MB.': 'Silero takes fifty seven point two megabytes.',
            '731–748 MiB': 'seven hundred thirty one to seven hundred forty eight mebibytes',
            '0.16–0.21 s': 'zero point one six to zero point two one seconds',
            '1,234,567': 'one million two hundred thirty four thousand five hundred sixty seven',
            '-12 +3': 'minus twelve plus three',
            '007': 'zero zero seven',
            'v3.1.0': 'v three point one point zero',
            '100% & 20 ms': 'one hundred percent and twenty milliseconds',
            'Hello, world!': 'Hello, world!',
        }
        for source, expected in cases.items():
            with self.subTest(source=source):
                # Spacing before punctuation is harmless to the model's frontend.
                self.assertEqual(normalize(source).replace(' .', '.'), expected)

    def test_very_long_integer_does_not_fail(self):
        self.assertEqual(normalize('9' * 5000).split(), ['nine'] * 5000)


if __name__ == '__main__':
    unittest.main()
