"""Compare only built-in synthetic Japanese fixtures; accepts no user text."""
import ctypes
import importlib.util
import json
import sys
import time

spec = importlib.util.spec_from_file_location("quiet_worker", sys.argv[1])
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
sys.stdout.reconfigure(encoding="utf-8")

samples = [
    ("short", "今日はいい天気です。"),
    ("test", "これは翻訳のテストです。"),
    ("date", "会議は2027年3月3日に開催されます。"),
    ("fullwidth_date", "会議は２０２７年３月３日に開催されます。"),
    ("number", "番号は１２３４５です。"),
    ("letter", "お世話になっております。\n金曜日までに報告書を送ってください。\nよろしくお願いします。"),
    ("list", "1. ファイルを開きます。\n2. メッセージを読みます。\n3. ウィンドウを閉じます。"),
    ("short_notice", "公演期間：2027/03/03(水)"),
]
for route in ("direct", "pivot"):
    worker.ROUTES[("ja", "ru")] = ("ja_ru",) if route == "direct" else ("ja_en", "en_ru")
    engine = worker.Engine(sys.argv[2])
    for name, source in samples:
        started = time.monotonic()
        try:
            result = engine.run({"Target": "ru", "Segments": [{"Source": "ja", "Text": source}]})
            print(json.dumps({"route": route, "case": name, "seconds": round(time.monotonic() - started, 3),
                              "translation": result}, ensure_ascii=False), flush=True)
        except Exception as error:
            print(json.dumps({"route": route, "case": name, "error": type(error).__name__}), flush=True)
    del engine

class Memory(ctypes.Structure):
    _fields_ = [("cb", ctypes.c_ulong), ("faults", ctypes.c_ulong)] + [(name, ctypes.c_size_t) for name in
        ("peak_working", "working", "peak_paged_pool", "paged_pool", "peak_nonpaged_pool", "nonpaged_pool", "pagefile", "peak_pagefile", "private")]

memory = Memory()
memory.cb = ctypes.sizeof(memory)
ctypes.windll.psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ulong]
ctypes.windll.psapi.GetProcessMemoryInfo(ctypes.c_void_p(-1), ctypes.byref(memory), memory.cb)
print(json.dumps({"peakWorkingSetBytes": memory.peak_working, "peakCommitBytes": memory.peak_pagefile}))
