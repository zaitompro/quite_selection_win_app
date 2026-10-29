"""Bounded speech pieces; preserve every input character, including punctuation."""
import re


def pieces(text, limit=220):
    start = 0
    for match in re.finditer(r"(?<=[.!?。！？])\s+|\n+", text):
        end = match.end()
        yield from bounded(text[start:end], limit)
        start = end
    yield from bounded(text[start:], limit)


def bounded(text, limit):
    while len(text) > limit:
        split = text.rfind(" ", 0, limit + 1)
        if split < limit // 3:
            split = limit
        else:
            split += 1
        yield text[:split]
        text = text[split:]
    if text:
        yield text
