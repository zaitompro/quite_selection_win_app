"""Small, deterministic Russian speech normalizer. No downloads, dictionaries or I/O.

Silero's Russian alphabet contains neither digits nor Latin letters. Convert those
before chunking/escaping SSML. This is pronunciation, never translation. Unknown
Latin words use an approximate spelling; this is not a general English G2P model.
"""
import re
import unicodedata


_ONES = ("ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять",
         "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать",
         "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать")
_GEN_ONES = ("нуля", "одного", "двух", "трёх", "четырёх", "пяти", "шести", "семи", "восьми", "девяти",
             "десяти", "одиннадцати", "двенадцати", "тринадцати", "четырнадцати", "пятнадцати",
             "шестнадцати", "семнадцати", "восемнадцати", "девятнадцати")
_TENS = ("", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто")
_GEN_TENS = ("", "", "двадцати", "тридцати", "сорока", "пятидесяти", "шестидесяти", "семидесяти", "восьмидесяти", "девяноста")
_HUNDREDS = ("", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот")
_GEN_HUNDREDS = ("", "ста", "двухсот", "трёхсот", "четырёхсот", "пятисот", "шестисот", "семисот", "восьмисот", "девятисот")
_SCALES = (("тысяча", "тысячи", "тысяч"), ("миллион", "миллиона", "миллионов"),
           ("миллиард", "миллиарда", "миллиардов"), ("триллион", "триллиона", "триллионов"))
_FRACTIONS = ("десятая", "сотая", "тысячная", "десятитысячная", "стотысячная", "миллионная")


def plural_index(value):
    if 11 <= value % 100 <= 14:
        return 2
    return 0 if value % 10 == 1 else 1 if 2 <= value % 10 <= 4 else 2


def cardinal(value, feminine=False, genitive=False):
    """Non-negative integers below 10**15; caller spells larger identifiers."""
    ones = _GEN_ONES if genitive else _ONES
    if value == 0:
        return ones[0]
    if value >= 1000:
        for exponent in range(4, 0, -1):
            scale = 1000 ** exponent
            if value >= scale:
                high, low = divmod(value, scale)
                forms = _SCALES[exponent - 1]
                index = (1 if plural_index(high) == 0 else 2) if genitive else plural_index(high)
                result = cardinal(high, exponent == 1, genitive) + " " + forms[index]
                return result + (" " + cardinal(low, feminine, genitive) if low else "")
    words = []
    hundreds, value = divmod(value, 100)
    if hundreds:
        words.append((_GEN_HUNDREDS if genitive else _HUNDREDS)[hundreds])
    if value >= 20:
        tens, value = divmod(value, 10)
        words.append((_GEN_TENS if genitive else _TENS)[tens])
    if value:
        word = ones[value]
        if feminine and value in (1, 2):
            word = ("одной" if genitive else "одна") if value == 1 else ("двух" if genitive else "две")
        words.append(word)
    return " ".join(words)


def _digits(text):
    return " ".join(_ONES[int(c)] for c in text)


def number(text, feminine=False, genitive=False):
    compact = re.sub(r"[ \u00a0\u202f]", "", text)
    sign = ""
    if compact[:1] in ("-", "−", "+"):
        sign = "плюс " if compact[0] == "+" else "минус "
        compact = compact[1:]
    fields = re.split(r"[.,]", compact)
    whole = fields[0]
    if len(fields) == 1:
        # Keep leading zeroes and long IDs; never truncate or convert via float.
        spoken = _digits(whole) if len(whole) > 15 or (len(whole) > 1 and whole[0] == "0") else cardinal(int(whole), feminine, genitive)
        return sign + spoken
    fraction = fields[1]
    if len(fraction) > 6 or len(whole) > 15 or (len(whole) > 1 and whole[0] == "0"):
        return sign + _digits(whole) + " запятая " + _digits(fraction)
    whole_value, fractional_value = int(whole), int(fraction)
    single_whole = plural_index(whole_value) == 0
    whole_suffix = ("целой" if genitive else "целая") if single_whole else "целых"
    denominator = _FRACTIONS[len(fraction) - 1]
    if plural_index(fractional_value) == 0:
        denominator = denominator[:-2] + "ой" if genitive else denominator
    else:
        denominator = denominator[:-2] + "ых"
    return (sign + cardinal(whole_value, True, genitive) + " " + whole_suffix + " "
            + cardinal(fractional_value, True, genitive) + " " + denominator)


# Singular, genitive singular, genitive plural, grammatical gender.
_UNITS = {
    "с": ("секунда", "секунды", "секунд", True),
    "сек": ("секунда", "секунды", "секунд", True),
    "мс": ("миллисекунда", "миллисекунды", "миллисекунд", True),
    "ms": ("миллисекунда", "миллисекунды", "миллисекунд", True),
    "s": ("секунда", "секунды", "секунд", True),
    "МиБ": ("мебибайт", "мебибайта", "мебибайт", False),
    "MiB": ("мебибайт", "мебибайта", "мебибайт", False),
    "ГиБ": ("гибибайт", "гибибайта", "гибибайт", False),
    "GiB": ("гибибайт", "гибибайта", "гибибайт", False),
    "КиБ": ("кибибайт", "кибибайта", "кибибайт", False),
    "KiB": ("кибибайт", "кибибайта", "кибибайт", False),
    "МБ": ("мегабайт", "мегабайта", "мегабайт", False),
    "MB": ("мегабайт", "мегабайта", "мегабайт", False),
    "ГБ": ("гигабайт", "гигабайта", "гигабайт", False),
    "GB": ("гигабайт", "гигабайта", "гигабайт", False),
    "КБ": ("килобайт", "килобайта", "килобайт", False),
    "KB": ("килобайт", "килобайта", "килобайт", False),
    "%": ("процент", "процента", "процентов", False),
}
_NUM = r"[+−-]?(?:[0-9]{1,3}(?:[ \u00a0\u202f][0-9]{3})+(?![0-9])|[0-9]+)(?:[.,][0-9]+)?"
_MEASUREMENT = re.compile(
    r"(?P<first>" + _NUM + r")(?:(?:[ \t]*[–—-][ \t]*)(?P<last>" + _NUM + r"))?"
    r"(?:[ \t\u00a0\u202f]*(?P<unit>" + "|".join(re.escape(x) for x in sorted(_UNITS, key=len, reverse=True)) + r")(?!\w))?")
_VERSION = re.compile(r"(?<![0-9.])[0-9]+(?:\.[0-9]+){2,}(?![0-9.])")


def _measurement(match):
    first, last, unit = match.group("first", "last", "unit")
    forms = _UNITS.get(unit)
    feminine = forms[3] if forms else False
    result = ("от " + number(first, feminine, True) + " до " + number(last, feminine, True)
              if last else number(first, feminine))
    if forms:
        final = re.sub(r"[+− \u00a0\u202f-]", "", last or first)
        if "." in final or "," in final:
            index = 1
        else:
            index = plural_index(int(final[-2:]))
            if last:
                index = 1 if index == 0 else 2
        result += " " + forms[index]
    return " " + result + " "


# Conventional pronunciations for common product names; fallback is deliberately
# transparent and small, not a growing corpus or a second neural model.
_WORDS = {
    "silero": "силеро", "piper": "пайпер", "ljspeech": "эл джей спич",
    "quietselection": "квайет селекшн", "rhvoice": "эр эйч войс", "sapi": "сапи",
    "windows": "виндоус", "microsoft": "майкрософт", "chrome": "хром",
    "edge": "эдж", "codex": "кодекс", "python": "пайтон", "torch": "торч",
    "pytorch": "пайторч", "github": "гитхаб", "openai": "оупен эй ай",
    "google": "гугл", "voice": "войс", "speech": "спич", "download": "даунлоуд",
    "hello": "хеллоу", "world": "уорлд", "file": "файл", "online": "онлайн",
    "offline": "офлайн", "update": "апдейт", "settings": "сеттингс",
}
_LETTER_NAMES = dict(zip("abcdefghijklmnopqrstuvwxyz",
    ("эй", "би", "си", "ди", "и", "эф", "джи", "эйч", "ай", "джей", "кей", "эл", "эм",
     "эн", "оу", "пи", "кью", "ар", "эс", "ти", "ю", "ви", "дабл ю", "экс", "уай", "зед")))
_LETTERS = dict(zip("abcdefghijklmnopqrstuvwxyz", "абкдэфгхийклмнопкрстуввкыз"))
_LETTERS.update({"j": "дж", "x": "кс", "q": "к", "y": "и"})
_DIGRAPHS = {"tion": "шен", "sion": "жен", "tch": "ч", "sch": "ш", "sh": "ш", "ch": "ч",
             "ph": "ф", "th": "т", "ck": "к", "qu": "кв", "ee": "и", "oo": "у",
             "ea": "и", "ai": "эй", "ay": "эй", "oi": "ой", "oy": "ой", "ou": "ау", "ow": "оу"}
_LATIN = re.compile(r"[A-Za-z]+")
_CAMEL = re.compile(r"[A-Z]+(?=[A-Z][a-z]|$)|[A-Z]?[a-z]+|[A-Z]+")
_SOUNDS = re.compile("|".join(sorted(_DIGRAPHS, key=len, reverse=True)) + "|[a-z]")


def latin_word(word, pronounce=None):
    known = _WORDS.get(word.lower())
    if known:
        return known
    if word.isupper() or len(word) == 1:
        return " ".join(_LETTER_NAMES[c] for c in word.lower())
    parts = _CAMEL.findall(word)
    if len(parts) > 1:
        return " ".join(latin_word(part, pronounce) for part in parts)
    if pronounce is not None:
        pronunciation = pronounce(word)
        if pronunciation:
            return pronunciation
    return _SOUNDS.sub(lambda m: _DIGRAPHS.get(m[0], _LETTERS.get(m[0], m[0])), word.lower())


def normalize(text, pronounce=None):
    # Markdown emphasis is presentation, not a pause or a pronunciation command.
    text = text.replace("*", "").replace("`", "")
    # Latin accents and Unicode decimal digits also occur in pasted/OCR text.
    text = re.sub(r"[^\x00-\x7f]", lambda m: str(unicodedata.decimal(m[0]))
                  if m[0].isdecimal() else m[0], text)
    text = re.sub(r"[À-ɏ]", lambda m: "".join(c for c in unicodedata.normalize("NFKD", m[0])
                  if not unicodedata.combining(c)), text)
    text = _VERSION.sub(lambda m: " точка ".join(number(part) for part in m[0].split(".")), text)
    text = _MEASUREMENT.sub(_measurement, text)
    text = _LATIN.sub(lambda m: " " + latin_word(m[0], pronounce) + " ", text)
    # Preserve sentence boundaries; collapse only horizontal whitespace.
    text = re.sub(r"[^\S\r\n]+", " ", text).strip()
    return re.sub(r" +([.,!?;:])", r"\1", text)
