# Privacy and limits

Selection and recognition use local Windows APIs. Speech uses WinRT, SAPI 5, Piper or Silero CPU processes; translation uses a short-lived CTranslate2 CPU process. Piper and Silero keep selected models resident by default to avoid repeated startup latency. Each has a separate memory option; disabling it, removing the engine's active selection or exiting releases its process. Application network clients are confined to `Translation/Downloads/ModelPreparation.cs`, `Speech/Downloads/RhVoiceInstallation.cs`, `Speech/Downloads/PiperModels.cs` and `Speech/Downloads/SileroModels.cs`. Their catalogs pin download locations, sizes and hashes; their interfaces accept no reading text or images. They run only after an explicit setup action. There is no cloud fallback.

The application never calls clipboard APIs, simulates Copy/Cut, or uses the whole document as a selection fallback. Copy/Cut/Paste command routes are disabled on its selectable settings controls. The source audit checks direct clipboard, whole-document, network and raw-exception logging patterns. The reviewed dependencies are .NET/WPF, Windows UI Automation, WinRT and Win32. These system frameworks have broader capabilities; a pattern scan does not prove every framework path or binary is free of leaks.

Selected text, OCR result and synthesized audio stay in process memory and local current-user IPC. Captures contain only the explicitly selected rectangle. There is no history, content log, telemetry endpoint or application memory dump. An optional selection log (off by default, Settings → General) records only the time, program name, result code and a trace of control types and class names, never the selected text. References and owned buffers are released when an operation or worker ends. Resident Piper and Silero retain model/runtime objects, not request dictionaries or audio history. Cancellation normally acknowledges completion without unloading the model; a nonresponsive worker is terminated. Releasing references is not a guarantee of secure zeroing of every managed/native allocation. The app cannot promise that Windows will never page memory or create an OS-managed dump; it does not change those system policies.

Persistent data is settings (schema version 1 with atomic replacement and a previous backup), installed tools/models when explicitly authorized, and technical test results. The current test reports contain fixed identifiers, pass/fail, numeric error codes and synthetic audio sizes. The test fixtures have no user correspondence or browser history.

In 0.1.0.19, mixed-language reading uses one in-memory PCM playback queue.
Python PCM frames travel through inherited private process pipes, while native
WAV frames use the existing current-user named pipe. Owned playback arrays are
cleared after playback or cancellation. The bounded queue does not store a
document's entire audio. Diagnostic mixed-speech reports run only through an
explicit test switch on fixed synthetic phrases; normal reading creates no such
report. These changes do not guarantee zeroing of runtime/native copies.

Silero normalizes numbers and Latin spellings locally in memory. Its pronunciation helper uses the already bundled eSpeak English dictionary; it does not download a dictionary, start an English voice, or query a service. The application does not cache normalized user text. Normalized fragments are bounded before inference, and only original character counts are returned as diagnostics. Fixed regression fixtures may appear in test reports; live reading text never does.

Network is confined to explicit setup: Microsoft supplies the SDK and Windows voice capabilities, official GitHub releases supply RHVoice, Argos/OPUS supply translation models, PyPI/Hugging Face supply Piper packages and voices, and official Silero/PyTorch servers plus PyPI supply the Russian/English Silero models and CPU runtime. None receives selected text or screenshots. The SDK opts out of CLI telemetry and first-run PATH/certificate changes. Python speech and translation run with isolated paths, a socket/write/subprocess audit guard, bounded memory-only standard-stream IPC, discarded dependency diagnostics, and kill-on-close Windows Jobs. ONNX Runtime telemetry is disabled. Python guards and source review do not prove every native binary is leak-free; no packet-level network audit has been performed.

The app runs as the current user, with `asInvoker` and `uiAccess=false`. Protected fields and unavailable/elevated UIA targets are not bypassed. Windows voice components and RHVoice's official SAPI installer may require a separate administrator process. Piper and Silero model preparation require no elevation. Package signing and publisher trust are separate, consent-gated setup actions; the explicitly authorized public certificate is in TrustedPeople, while its nonexportable private key remains with the current user.


Kokoro и VOICEVOX используют тот же запрет Python socket/subprocess/записи
файлов, что и существующие речевые процессы. У Kokoro отключена телеметрия ORT;
аудио VOICEVOX возвращается C API и освобождается после копирования PCM.
Загрузчик AdditionalModels имеет только фиксированный каталог моделей,
проверяет HTTPS и разрешённые хосты GitHub; текст для чтения ему не передаётся.
Он работает только по явному действию установки. Проверка Python audit hook
не является доказательством отсутствия всех побочных эффектов нативных библиотек.
