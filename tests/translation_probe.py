"""Developer-only diagnostics using fixed, synthetic phrases. Never accepts source text."""
import sys
import importlib.util
import time
import traceback
import ctypes

spec = importlib.util.spec_from_file_location("quiet_worker", sys.argv[1])
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
engine = worker.Engine(sys.argv[2])
for source, text in [("en", "This is a translation test."), ("ja", "これは翻訳のテストです。"),
                     ("ja", "会議は2027年3月3日に開催されます。"),
                     ("en", "Dear colleague,\nPlease send the report by Friday.\nThank you."),
                     ("en", "1. Open the file.\n2. Read the message.\n3. Close the window.")]:
    started = time.monotonic()
    try:
        result = engine.run({"Target": "ru", "Segments": [{"Source": source, "Text": text}]})
        print(source, "PASS", round(time.monotonic() - started, 3), ascii(result), flush=True)
    except Exception as ex:
        print(source, type(ex).__name__, [(f.name, f.lineno) for f in traceback.extract_tb(ex.__traceback__)], flush=True)

class Memory(ctypes.Structure):
    _fields_ = [("cb", ctypes.c_ulong), ("faults", ctypes.c_ulong)] + [(name, ctypes.c_size_t) for name in
        ("peak_working", "working", "peak_paged_pool", "paged_pool", "peak_nonpaged_pool", "nonpaged_pool", "pagefile", "peak_pagefile", "private")]

memory = Memory()
memory.cb = ctypes.sizeof(memory)
ctypes.windll.psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ulong]
ctypes.windll.psapi.GetProcessMemoryInfo(ctypes.c_void_p(-1), ctypes.byref(memory), memory.cb)
print("MEMORY", memory.peak_working, memory.peak_pagefile, flush=True)
