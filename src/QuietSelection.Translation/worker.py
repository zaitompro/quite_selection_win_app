"""One request per process. Bounded binary IPC; no network, text files, or logs."""
import sys
import os
import json
import struct
import re
import unicodedata
from pathlib import Path

sys.dont_write_bytecode = True


def deny_side_effects(event, args):
    if event.startswith("socket.") or event in ("subprocess.Popen", "os.system", "os.startfile"):
        raise PermissionError("OFFLINE_WORKER")
    if event == "open":
        mode, flags = args[1], args[2]
        if (isinstance(mode, str) and any(c in mode for c in "wax+")) or (
            isinstance(flags, int) and flags & (os.O_WRONLY | os.O_RDWR | os.O_CREAT | os.O_TRUNC)
        ):
            raise PermissionError("READ_ONLY_WORKER")


sys.addaudithook(deny_side_effects)
import ctranslate2
import sentencepiece

LIMIT = 60_000
WIRE_LIMIT = 2_000_000
MODEL_FOLDERS = {"en_ru": "translate-en_ru-1_9", "ja_en": "ja_en", "ja_ru": "ja_ru"}
ROUTES = {("en", "ru"): ("en_ru",), ("ja", "en"): ("ja_en",),
          ("ja", "ru"): ("ja_ru",)}


def normalize_japanese(text):
    # OPUS preprocessing, with fullwidth digits kept numeric (upstream maps １ to a quote).
    replacements = str.maketrans({
        "，": ",", "、": ",", "”": '"', "“": '"', "∶": ":", "：": ":",
        "？": "?", "《": '"', "》": '"', "）": ")", "！": "!", "（": "(",
        "；": ";", "」": '"', "「": '"', "～": "~", "’": "'", "…": "...",
        "━": "-", "〈": "<", "〉": ">", "【": "[", "】": "]", "％": "%",
        **{chr(0xFF10 + n): str(n) for n in range(10)},
    })
    text = re.sub(r"[。．] *", ". ", text.translate(replacements))
    text = "".join(" " if unicodedata.category(c).startswith("C") else c for c in text)
    return re.sub(" +", " ", text).strip()


def utf16_length(value):
    return len(value.encode("utf-16-le")) // 2


class Engine:
    def __init__(self, root):
        self.root = Path(root)
        self.models = {}

    def load(self, pair):
        if pair not in self.models:
            # Keep only one model resident on this 8 GB PC.
            self.models.clear()
            folder = self.root / MODEL_FOLDERS[pair]
            source_name = "source.spm" if pair == "ja_ru" else "sentencepiece.model"
            source_tokenizer = sentencepiece.SentencePieceProcessor(model_file=str(folder / source_name))
            target_tokenizer = (sentencepiece.SentencePieceProcessor(model_file=str(folder / "target.spm"))
                                if pair == "ja_ru" else source_tokenizer)
            translator = ctranslate2.Translator(str(folder / "model"), device="cpu", compute_type="int8",
                                                inter_threads=1, intra_threads=2, max_queued_batches=1)
            self.models[pair] = source_tokenizer, target_tokenizer, translator
        return self.models[pair]

    def part(self, text, pair):
        # Preserve whitespace and punctuation-only spans exactly. No hidden truncation.
        if not any(c.isalpha() for c in text):
            return text
        source_tokenizer, target_tokenizer, translator = self.load(pair)
        leading = text[:len(text) - len(text.lstrip())]
        trailing = text[len(text.rstrip()):]
        body = text.strip()
        tokens = source_tokenizer.encode(normalize_japanese(body) if pair == "ja_ru" else body, out_type=str)
        if len(tokens) > 180:
            middle = len(body) // 2
            boundaries = [m.end() for m in re.finditer(r"\s+|[。！？.!?]", body)
                          if len(body) // 4 < m.end() < len(body) * 3 // 4]
            split = min(boundaries, key=lambda i: abs(i - middle)) if boundaries else middle
            separator = "" if body[split - 1].isspace() else " "
            return leading + self.part(body[:split], pair) + separator + self.part(body[split:], pair) + trailing
        result = translator.translate_batch([tokens], beam_size=6 if pair == "ja_ru" else 2, max_batch_size=1,
                                             max_input_length=0, max_decoding_length=512,
                                             return_end_token=True, replace_unknowns=True)[0].hypotheses[0]
        if not result or result[-1] != "</s>":
            raise ValueError("INCOMPLETE_TRANSLATION")
        translated = target_tokenizer.decode(result[:-1]).strip()
        if not translated:
            raise ValueError("EMPTY_TRANSLATION")
        return leading + translated + trailing

    def run(self, request):
        segments, target = request["Segments"], request["Target"]
        if target not in ("ru", "en", "ja") or len(segments) > LIMIT:
            raise ValueError("INVALID_REQUEST")
        if sum(utf16_length(s["Text"]) for s in segments) > LIMIT:
            raise ValueError("TOO_LONG")
        # Resolve/load every required model before translating even the first span.
        required = set()
        for segment in segments:
            source = segment["Source"]
            if source != target:
                required.update(ROUTES[(source, target)])
        for pair in ("ja_en", "en_ru", "ja_ru"):
            if pair in required:
                self.load(pair)
        output = [segment["Text"] for segment in segments]
        # Apply each stage to all matching spans, retaining their original positions.
        # This avoids reloading models for every sentence in a mixed selection.
        for pair in ("ja_en", "en_ru", "ja_ru"):
            for index, segment in enumerate(segments):
                if pair in ROUTES.get((segment["Source"], target), ()):
                    output[index] = "".join(self.part(line, pair) for line in output[index].splitlines(keepends=True))
            if sum(utf16_length(x) for x in output) > LIMIT:
                raise ValueError("TOO_LONG")
        return "".join(output)


def main():
    response = {"Success": False, "Text": "", "Failure": "TRANSLATION_FAILED"}
    try:
        size_bytes = sys.stdin.buffer.read(4)
        if len(size_bytes) != 4:
            raise ValueError("INVALID_FRAME")
        size = struct.unpack("<i", size_bytes)[0]
        if not 0 < size <= WIRE_LIMIT:
            raise ValueError("INVALID_FRAME")
        data = sys.stdin.buffer.read(size)
        if len(data) != size:
            raise ValueError("INVALID_FRAME")
        request = json.loads(data)
        response = {"Success": True, "Text": Engine(sys.argv[1]).run(request), "Failure": ""}
    except BaseException as error:
        # Exceptions from tokenizers may contain input. Never serialize them.
        response["Failure"] = "MEMORY_LIMIT" if isinstance(error, MemoryError) else "RUNTIME_ERROR" if isinstance(error, RuntimeError) else "ENGINE_FAILURE"
    encoded = json.dumps(response, ensure_ascii=False).encode("utf-8")
    if len(encoded) > WIRE_LIMIT:
        return 1
    sys.stdout.buffer.write(struct.pack("<i", len(encoded)))
    sys.stdout.buffer.write(encoded)
    sys.stdout.buffer.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
