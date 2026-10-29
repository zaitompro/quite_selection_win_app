"""Bounded in-memory PCM playback with cooperative cancellation (Windows only)."""
import ctypes
import time


class WaveFormat(ctypes.Structure):
    _pack_ = 2
    _fields_ = [("tag", ctypes.c_ushort), ("channels", ctypes.c_ushort),
                ("sample_rate", ctypes.c_uint), ("average_bytes", ctypes.c_uint),
                ("alignment", ctypes.c_ushort), ("bits", ctypes.c_ushort),
                ("extra_size", ctypes.c_ushort)]


class WaveHeader(ctypes.Structure):
    _fields_ = [("data", ctypes.c_void_p), ("length", ctypes.c_uint),
                ("recorded", ctypes.c_uint), ("user", ctypes.c_size_t),
                ("flags", ctypes.c_uint), ("loops", ctypes.c_uint),
                ("next", ctypes.c_void_p), ("reserved", ctypes.c_size_t)]


def play_pcm(audio, sample_rate, cancellation):
    library = ctypes.WinDLL("winmm")
    library.waveOutOpen.argtypes = [ctypes.POINTER(ctypes.c_void_p), ctypes.c_uint,
                                   ctypes.POINTER(WaveFormat), ctypes.c_size_t,
                                   ctypes.c_size_t, ctypes.c_uint]
    for name in ("waveOutPrepareHeader", "waveOutWrite", "waveOutUnprepareHeader"):
        getattr(library, name).argtypes = [ctypes.c_void_p, ctypes.POINTER(WaveHeader), ctypes.c_uint]
    library.waveOutReset.argtypes = [ctypes.c_void_p]
    library.waveOutClose.argtypes = [ctypes.c_void_p]
    handle = ctypes.c_void_p()
    format = WaveFormat(1, 1, sample_rate, sample_rate * 2, 2, 16, 0)
    buffer = ctypes.create_string_buffer(audio)
    header = WaveHeader(data=ctypes.cast(buffer, ctypes.c_void_p), length=len(audio))
    if library.waveOutOpen(ctypes.byref(handle), 0xFFFFFFFF, ctypes.byref(format), 0, 0, 0):
        raise RuntimeError("AUDIO_DEVICE_FAILURE")
    prepared = False
    try:
        if library.waveOutPrepareHeader(handle, ctypes.byref(header), ctypes.sizeof(header)):
            raise RuntimeError("AUDIO_PREPARE_FAILURE")
        prepared = True
        cancellation.check()
        if library.waveOutWrite(handle, ctypes.byref(header), ctypes.sizeof(header)):
            raise RuntimeError("AUDIO_WRITE_FAILURE")
        while not header.flags & 1:  # WHDR_DONE
            cancellation.check()
            time.sleep(0.01)
    finally:
        library.waveOutReset(handle)
        if prepared:
            library.waveOutUnprepareHeader(handle, ctypes.byref(header), ctypes.sizeof(header))
        library.waveOutClose(handle)
