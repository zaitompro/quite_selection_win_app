"""Read-only/offline guards: only synthetic operations, never external connections."""
import importlib.util
import sys
import socket

spec = importlib.util.spec_from_file_location("quiet_worker", sys.argv[1])
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)

passed = 0
try:
    socket.socket()
except PermissionError:
    print("PASS offline.socket_creation_denied")
    passed += 1

try:
    with open(sys.argv[2], "w", encoding="utf-8"):
        pass
except PermissionError:
    print("PASS privacy.file_write_denied")
    passed += 1

if worker.utf16_length("A\U0001F600") == 3:
    print("PASS limits.utf16_supplementary_character")
    passed += 1

if worker.normalize_japanese("番号：０１２３４５６７８９。") == "番号:0123456789.":
    print("PASS normalization.fullwidth_digits_preserved")
    passed += 1
if worker.normalize_japanese("日付：２０２７年３月３日。\u200b") == "日付:2027年3月3日.":
    print("PASS normalization.date_and_control_character")
    passed += 1
raise SystemExit(0 if passed == 5 else 1)
