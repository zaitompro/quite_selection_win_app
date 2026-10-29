# Kokoro English INT8

Kokoro v1.0, 82M parameters, converted INT8 ONNX model and the published v1.0
voice archive from the kokoro-onnx model-files-v1.0 release. Only the 28 English
profiles (20 US, 8 GB) are exposed. Model weights and voices are Apache 2.0;
the upstream model card and license are included here. The phoneme vocabulary
comes from kokoro-onnx 0.6.1 (MIT, included license).

Source: https://github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.0
Original model: https://huggingface.co/hexgrad/Kokoro-82M

The application uses the existing Piper eSpeak native IPA bridge and CPU ONNX
Runtime. It implements bounded inference directly; it does not install a second
Python/ORT/PyTorch environment or contact an HTTP speech server. Punctuation is
preserved from the bridge's clause output. Model phonemes are split at 510 tokens
instead of truncating text. This is an eSpeak frontend, not Misaki.

The release predates GitHub asset digests. SHA-256 values in the application
catalog were calculated after HTTPS retrieval from the named release and are
used for subsequent integrity checks; they are not publisher signatures.

Download: 92,361,271 bytes model + 28,214,398 bytes voices. The NPZ archive includes
other languages, but only English profiles are exposed and loaded on demand.
All English profiles share one inference session. No Russian Kokoro is included.
