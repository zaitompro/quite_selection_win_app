"""Synthetic privacy/SSML tests; importing the worker enables its production audit guard."""
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src" / "QuietSelection.Speech"))
import silero_worker as worker

count = 0
for speed, rate in ((0, "x-slow"), (25, "slow"), (50, "medium"), (75, "fast"), (100, "x-fast")):
    text = 'Тест <break time="999s"/> & <prosody rate="x-slow">слово</prosody>'
    parsed = ET.fromstring(worker.ssml(text, speed))
    assert len(list(parsed.iter())) == 2 and parsed[0].text == text
    assert parsed[0].attrib == {"rate": rate}
    count += 1
for action in ("socket", "write", "subprocess"):
    try:
        if action == "socket":
            import socket
            socket.socket()
        elif action == "write":
            open("synthetic-forbidden-output.txt", "w")
        else:
            import subprocess
            subprocess.Popen([sys.executable, "-c", "pass"])
        raise AssertionError("SIDE_EFFECT_ALLOWED")
    except PermissionError:
        count += 1
literal = "Текст **без закрытия и *звёздочка"
encoded = worker.ssml(literal, 50)
assert "*" not in encoded and ET.fromstring(encoded)[0].text == literal
count += 1
normalized = worker.normalize('Число 731 <break time="999s"/> **Silero** & <prosody rate="x-slow">тест</prosody>')
parsed = ET.fromstring(worker.ssml(normalized, 50))
assert len(list(parsed.iter())) == 2 and parsed[0].text == normalized
assert 'семьсот тридцать один' in normalized and 'силеро' in normalized
count += 1
print("SILERO_GUARDS_PASS", count)
