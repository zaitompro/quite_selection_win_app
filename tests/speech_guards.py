"""Privacy and segmentation tests, synthetic inputs only."""
import importlib.util
from pathlib import Path
import sys

source = Path(__file__).resolve().parents[1] / "src" / "QuietSelection.Speech" / "worker.py"
spec = importlib.util.spec_from_file_location("speech_worker", source)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
from speech_text import pieces

count = 0
for text in ("Первое предложение. Second sentence!\n最後。", "слово " * 1000, "a" * 1000,
             "Дата 03.03.2027, цена 3.14. Конец.", "🗣️" * 1000):
    parts = list(pieces(text))
    assert "".join(parts) == text and max(map(len, parts)) <= 220
    count += 1
try:
    import socket
    socket.socket()
    raise AssertionError("SOCKET_NOT_BLOCKED")
except PermissionError:
    count += 1
try:
    open("synthetic-forbidden-output.txt", "w")
    raise AssertionError("WRITE_NOT_BLOCKED")
except PermissionError:
    count += 1
print("SPEECH_GUARDS_PASS", count)
