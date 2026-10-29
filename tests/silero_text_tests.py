"""Regression tests for information previously lost by Russian Silero.

Optional --app-root PATH --model PATH also checks the pinned model's actual text
frontend, under the production offline/read-only guard. No audio/text is saved.
"""
from pathlib import Path
import re
import sys
import unittest
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src" / "QuietSelection.Speech"))
from silero_text import normalize
from silero_latin import EnglishPronunciation, russian_phonemes
from speech_text import pieces

EXAMPLE = ('Также подключены **пять русских голосов Silero** — доступны в разделе «Речь». '
           'С моделью в памяти задержка короткой фразы составила **0,16–0,21 с**, '
           'расход памяти — **731–748 МиБ** без интерфейса. Английский Piper LJSpeech сохранён.')


class NormalizationTests(unittest.TestCase):
    def test_user_example_keeps_names_and_values(self):
        result = normalize(EXAMPLE)
        for expected in ('силеро', 'пайпер эл джей спич',
                         'от нуля целых шестнадцати сотых до нуля целых двадцати одной сотой секунды',
                         'от семисот тридцати одного до семисот сорока восьми мебибайт'):
            self.assertIn(expected, result)
        self.assertIsNone(re.search(r'[A-Za-z0-9*]', result))

    def test_integers_and_grammatical_units(self):
        for text, expected in [('0', 'ноль'), ('11', 'одиннадцать'), ('101', 'сто один'),
                               ('2001', 'две тысячи один'), ('1000001', 'один миллион один'),
                               ('21 с', 'двадцать одна секунда'), ('22 мс', 'двадцать две миллисекунды'),
                               ('11 MiB', 'одиннадцать мебибайт'), ('1 GB', 'один гигабайт')]:
            with self.subTest(text=text): self.assertEqual(normalize(text), expected)

    def test_decimal_precision(self):
        for text, expected in [('0.16', 'ноль целых шестнадцать сотых'),
                               ('1,01', 'одна целая одна сотая'),
                               ('0,001', 'ноль целых одна тысячная'),
                               ('0,010', 'ноль целых десять тысячных'),
                               ('0,0000001', 'ноль запятая ноль ноль ноль ноль ноль ноль один')]:
            with self.subTest(text=text): self.assertEqual(normalize(text), expected)

    def test_signed_ranges_and_feminine(self):
        self.assertEqual(normalize('-5–-2'), 'от минус пяти до минус двух')
        self.assertEqual(normalize('1–2 с'), 'от одной до двух секунд')
        self.assertEqual(normalize('+12'), 'плюс двенадцать')
        self.assertEqual(normalize('−0,5'), 'минус ноль целых пять десятых')

    def test_grouping_and_leading_zeroes(self):
        self.assertEqual(normalize('1\u202f234\u00a0567'), 'один миллион двести тридцать четыре тысячи пятьсот шестьдесят семь')
        self.assertEqual(normalize('007'), 'ноль ноль семь')
        self.assertEqual(normalize('1000000000000000'), 'один ' + ' '.join(['ноль'] * 15))

    def test_versions_and_unicode_digits(self):
        self.assertEqual(normalize('0.1.0.14'), 'ноль точка один точка ноль точка четырнадцать')
        self.assertEqual(normalize('１２３'), 'сто двадцать три')

    def test_acronyms_and_compounds(self):
        self.assertEqual(normalize('CPU USB'), 'си пи ю ю эс би')
        self.assertEqual(normalize('QuietSelection'), 'квайет селекшн')
        self.assertEqual(normalize('helloWorld'), 'хеллоу уорлд')
        self.assertIsNone(re.search('[A-Za-z]', normalize('Qwerty café x86')))

    def test_russian_text_and_sentences(self):
        text = 'Это русский текст. Ещё строка!\nСледующая строка?'
        self.assertEqual(normalize(text), text)

    def test_unknown_units_not_consumed(self):
        self.assertEqual(normalize('2 слова'), 'два слова')
        self.assertEqual(normalize('10 секунд'), 'десять секунд')

    def test_expansion_bounded_after_normalizing(self):
        source = '12345678901234567890 ' * 100
        spoken = normalize(source)
        chunks = list(pieces(spoken))
        self.assertGreater(len(spoken), len(source))
        self.assertEqual(''.join(chunks), spoken)
        self.assertTrue(all(len(x) <= 220 for x in chunks))
        self.assertEqual(normalize('0' * 60000).split(), ['ноль'] * 60000)

    def test_phoneme_stress_and_no_silent_loss(self):
        self.assertEqual(russian_phonemes('ɛɡzˈæmpəl'), 'эгз+эмпэл')
        self.assertEqual(russian_phonemes('dɪvˈɛləpmənt'), 'див+элэпмэнт')
        self.assertIsNone(russian_phonemes('abc☃'))

    def test_actual_bundled_phonemizer(self):
        pronounce = EnglishPronunciation(ROOT / '.tools' / 'piper-runtime' / 'piper')
        result = normalize('example development', pronounce)
        self.assertEqual(result, 'эгз+эмпэл див+элэпмэнт')
        self.assertNotIn('onnxruntime', sys.modules)
        self.assertNotIn('piper', sys.modules)

    def test_missing_phonemizer_spelling_fallback(self):
        missing = EnglishPronunciation(ROOT / 'nonexistent-synthetic-runtime')
        self.assertEqual(normalize('Example', missing), normalize('Example'))
        self.assertTrue(missing.unavailable)


def model_frontend_check(app_root, model_path):
    # Importing runtime first applies the same production audit guard.
    import silero_worker as worker
    worker.APP_ROOT = app_root
    sys.path.insert(0, str(app_root / 'silero-runtime'))
    cache = worker.VoiceCache(model_path.parent)
    cancel = worker.Cancellation(None)
    try:
        model, _ = cache.get('silero:v5_6_ru:xenia', cancel)
        package = model.packages[0]
        before = package.prepare_text_input('Silero 731 0,16', None)[0]
        assert not re.search('[A-Za-z0-9]', before), 'OLD_FRONTEND_FILTER_NOT_REPRODUCED'
        fixtures = [EXAMPLE, 'Silero', '731', '0,16', 'example development', '0.1.0.14']
        for fixture in fixtures:
            for part in pieces(normalize(fixture, cache.latin)):
                parsed = ET.fromstring(worker.ssml(part, 50))[0].text
                actual = package.prepare_text_input(parsed, None)[0]
                # Every spoken Russian word must reach the model, not just PCM.
                expected_words = re.findall('[а-яё]+', part.lower().replace('+', ''))
                actual_words = re.findall('[а-яё]+', actual.lower().replace('+', ''))
                assert expected_words == actual_words, 'NORMALIZED_WORDS_DROPPED'
        print('SILERO_REAL_FRONTEND_PASS', len(fixtures), flush=True)
    finally:
        cancel.close()


if __name__ == '__main__':
    if '--model' in sys.argv:
        model_frontend_check(Path(sys.argv[sys.argv.index('--app-root') + 1]), Path(sys.argv[sys.argv.index('--model') + 1]))
    else:
        unittest.main()
