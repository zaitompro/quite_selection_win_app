"""Use the already bundled eSpeak dictionary for approximate Russian pronunciation.

Loads only Piper's native phonemizer, not Piper/ONNX or another voice model. No
user text is cached, saved or sent to another process. English spelling is only a
heuristic for Latin text: proper names and other languages remain approximate.
"""
import importlib.util
import re
import unicodedata


_PHONES = {
    "aɪ": "ай", "eɪ": "эй", "ɔɪ": "ой", "oʊ": "оу", "əʊ": "оу", "aʊ": "ау",
    "tʃ": "ч", "dʒ": "дж", "ɚɹ": "эр", "ɪə": "иэ", "ɛə": "эа", "ʊə": "уэ",
    "a": "а", "ɑ": "а", "ɐ": "а", "æ": "э", "ʌ": "а", "ə": "э", "ɚ": "эр",
    "ɜ": "э", "ɛ": "э", "e": "э", "i": "и", "ɪ": "и", "ᵻ": "и", "o": "о",
    "ɔ": "о", "ɒ": "о", "u": "у", "ʊ": "у", "y": "ю",
    "p": "п", "b": "б", "t": "т", "d": "д", "k": "к", "ɡ": "г", "g": "г",
    "f": "ф", "v": "в", "θ": "т", "ð": "з", "s": "с", "z": "з", "ʃ": "ш",
    "ʒ": "ж", "h": "х", "x": "х", "m": "м", "n": "н", "ŋ": "нг", "l": "л",
    "ɫ": "л", "r": "р", "ɹ": "р", "ɾ": "т", "j": "й", "w": "в", "ʔ": "",
}
_TOKENS = re.compile("|".join(sorted(_PHONES, key=len, reverse=True)) + r"|.", re.DOTALL)


def russian_phonemes(ipa):
    ipa = re.sub(r"\([^)]+\)", "", unicodedata.normalize("NFC", ipa))
    result, stress = [], False
    for match in _TOKENS.finditer(ipa):
        token = match[0]
        if token == "ˈ":
            stress = True
        elif token.isspace():
            result.append(" ")
            stress = False
        elif token in _PHONES:
            spoken = _PHONES[token]
            if stress and spoken and spoken[0] in "аэиоуыюяё":
                spoken = "+" + spoken
                stress = False
            result.append(spoken)
        elif token not in "ˌːˑ_" and not unicodedata.combining(token):
            # Unknown phones must trigger the spelling fallback, not disappear.
            return None
    return "".join(result).strip() or None


class EnglishPronunciation:
    def __init__(self, directory):
        self.directory = directory
        self.bridge = None
        self.unavailable = False

    def __call__(self, word):
        if self.unavailable or len(word) > 128:
            return None
        try:
            if self.bridge is None:
                spec = importlib.util.spec_from_file_location("espeakbridge", self.directory / "espeakbridge.pyd")
                if spec is None or spec.loader is None:
                    return None
                bridge = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(bridge)
                bridge.initialize(str(self.directory / "espeak-ng-data"))
                bridge.set_voice("en-us")
                self.bridge = bridge
            return russian_phonemes(" ".join(clause[0] for clause in self.bridge.get_phonemes(word)))
        except (ImportError, OSError, RuntimeError, ValueError):
            self.unavailable = True
            return None
