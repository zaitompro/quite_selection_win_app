"""Lossless clause boundaries for the additional speech engines."""
import re
from speech_text import bounded


def speech_parts(text, engine):
    # Japanese full stops need no following whitespace. Decimal points and
    # numeric separators (120.6, 1,234, 10:30) must remain within one fragment.
    pattern = r'[。！？!?]+[」』”’\"\')）]*\s*|[.;:](?=\s)|[,、，；：](?!\d)|\n+'
    start = 0
    for match in re.finditer(pattern, text):
        end = match.end()
        part = text[start:end]
        # Avoid very short comma/colon fragments; keep them with their clause.
        sentence = any(c in match.group() for c in '.。！？!?\n')
        if not sentence and len(part.strip()) < (24 if engine in ('kokoro', 'kitten', 'pocket') else 12):
            continue
        yield from bounded(part, 140)
        start = end
    yield from bounded(text[start:], 140)
