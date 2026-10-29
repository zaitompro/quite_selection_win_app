"""Offline IPC, cancellation, memory counters and playback for speech workers."""
import sys
import os
import json
import base64
import struct
import hashlib
import time
import logging
import ctypes
import threading
import gc
from pathlib import Path

sys.dont_write_bytecode = True
logging.disable(logging.CRITICAL)


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
APP_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))
from speech_text import pieces
from audio_output import play_pcm


class MemoryCounters(ctypes.Structure):
    _fields_ = [("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong)] + [
        (name, ctypes.c_size_t) for name in (
            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
            "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
            "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]


def memory():
    counters = MemoryCounters()
    counters.cb = ctypes.sizeof(counters)
    get_info = ctypes.WinDLL("psapi").GetProcessMemoryInfo
    get_info.argtypes = [ctypes.c_void_p, ctypes.POINTER(MemoryCounters), ctypes.c_ulong]
    get_info.restype = ctypes.c_int
    if not get_info(ctypes.c_void_p(-1), ctypes.byref(counters), counters.cb):
        raise RuntimeError("MEMORY_MEASUREMENT_FAILED")
    return counters


class Cancelled(Exception):
    pass


class Cancellation:
    def __init__(self, event_name):
        self.done = threading.Event()
        self.cancelled = threading.Event()
        self.options = None
        self.thread = None
        self.handle = None
        if event_name:
            self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            self.kernel.OpenEventW.argtypes = [ctypes.c_uint, ctypes.c_int, ctypes.c_wchar_p]
            self.kernel.OpenEventW.restype = ctypes.c_void_p
            self.kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint]
            self.kernel.CloseHandle.argtypes = [ctypes.c_void_p]
            self.handle = self.kernel.OpenEventW(0x00100000, False, event_name)
            if not self.handle:
                raise RuntimeError("CANCEL_EVENT_UNAVAILABLE")
            self.thread = threading.Thread(target=self.watch, daemon=True)
            self.thread.start()

    def watch(self):
        while not self.done.is_set():
            if self.kernel.WaitForSingleObject(self.handle, 10) == 0:
                self.cancelled.set()
                options = self.options
                if options is not None:
                    options.terminate = True
                return

    def set_options(self, options):
        self.options = options
        if self.cancelled.is_set():
            options.terminate = True
        self.check()

    def check(self):
        if self.cancelled.is_set():
            raise Cancelled()

    def close(self):
        self.done.set()
        if self.thread:
            self.thread.join()
        if self.handle:
            self.kernel.CloseHandle(self.handle)
        self.options = None


def exact_read(count):
    data = bytearray()
    while len(data) < count:
        block = sys.stdin.buffer.read(count - len(data))
        if not block:
            if not data:
                raise EOFError()
            raise ValueError("INCOMPLETE_REQUEST")
        data.extend(block)
    return data


def read_request():
    count = struct.unpack("<i", exact_read(4))[0]
    if not 0 < count <= 2_000_000:
        raise ValueError("INVALID_REQUEST")
    return json.loads(exact_read(count))


def send(result):
    encoded = json.dumps(result).encode("utf-8")
    sys.stdout.buffer.write(struct.pack("<i", len(encoded)) + encoded)
    sys.stdout.buffer.flush()


def send_audio(audio, sample_rate, cancellation):
    """Bounded PCM frames over the existing private pipe, never files or sockets."""
    if len(audio) % 2 or not 8000 <= sample_rate <= 96000:
        raise ValueError("AUDIO_FORMAT")
    for offset in range(0, len(audio), 262144):
        cancellation.check()
        send({"Stage": "audio", "SampleRate": sample_rate,
              "AudioData": base64.b64encode(memoryview(audio)[offset:offset + 262144]).decode("ascii")})


