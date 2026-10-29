"""Small offline English number frontend for Silero v3's letters-only alphabet."""
import re

SMALL = ('zero one two three four five six seven eight nine ten eleven twelve '
         'thirteen fourteen fifteen sixteen seventeen eighteen nineteen').split()
TENS = 'zero ten twenty thirty forty fifty sixty seventy eighty ninety'.split()


def integer(digits):
    if len(digits) > 15 or len(digits) > 1 and digits.startswith('0'):
        return ' '.join(SMALL[int(d)] for d in digits)
    value = int(digits)
    if value < 20:
        return SMALL[value]
    if value < 100:
        return TENS[value // 10] + (' ' + SMALL[value % 10] if value % 10 else '')
    for scale, name in ((10**12, 'trillion'), (10**9, 'billion'), (10**6, 'million'),
                        (1000, 'thousand'), (100, 'hundred')):
        if value >= scale:
            return integer(str(value // scale)) + ' ' + name + (
                ' ' + integer(str(value % scale)) if value % scale else '')


def number(match):
    parts = match.group().split('.')
    return integer(parts[0]) + ''.join(' point ' + ' '.join(SMALL[int(d)] for d in p) for p in parts[1:])


def normalize(text):
    # English grouping commas only; decimal separator is a dot. Source markup is
    # kept literal/escaped by the worker, never interpreted as caller-supplied SSML.
    text = re.sub(r'(?<![\d,])\d{1,3}(?:,\d{3})+(?![\d,])', lambda m: m.group().replace(',', ''), text)
    text = re.sub(r'(?<=\d)\s*[-–—]\s*(?=\d)', ' to ', text)
    text = re.sub(r'(?<![\w.])-(?=\d)', 'minus ', text)
    text = re.sub(r'\+(?=\d)', 'plus ', text)
    text = re.sub(r'\bMiB\b', 'mebibytes', text)
    text = re.sub(r'\bGiB\b', 'gibibytes', text)
    text = re.sub(r'\bMB\b', 'megabytes', text)
    text = re.sub(r'\bGB\b', 'gigabytes', text)
    text = re.sub(r'(?<=\d)\s*ms\b', ' milliseconds', text)
    text = re.sub(r'(?<=\d)\s*s\b', ' seconds', text)
    text = text.replace('%', ' percent ').replace('&', ' and ').replace('*', '')
    text = re.sub(r'[0-9]+(?:\.[0-9]+)*', lambda m: ' ' + number(m) + ' ', text)
    return re.sub(r'\s+', ' ', text).strip()
