# Architecture

The repository is a single solution with three managed production projects (Core, App and Setup), one native bridge, isolated Python translation and speech workers, and a small dependency-free behavioral test executable. UI framework references come from the downloaded SDK; there are no third-party NuGet dependencies.

The WPF shell owns settings, tray, registered hotkeys and the operation controller. Every operation uses a settings snapshot. A second command cancels the existing operation and does not start another. Its slot is held until the helper terminates or the resident speech worker acknowledges cancellation. Late cancelled results cannot start speech. The shortcut selector supports Alt+1 through Alt+4 on the number row as well as the previous Ctrl+Shift/Ctrl+Alt function-key choices. It registers the replacement before releasing the old binding, refuses duplicates and occupied combinations, and suppresses key-repeat notifications. Fresh profiles still start without global shortcuts; the requested Alt-number assignments are specific to this user's settings. These global bindings override matching local app commands, such as Calculator's mode shortcuts.

UI Automation executes on the MTA Main thread of a dedicated resident process (since 0.1.0.18). Successful reads and normal provider errors preserve its apartment; no elements, ranges or text are cached between requests. It checks the freshly focused element's process and password flag, then checks that element and at most six parents for TextPattern. Only nonempty selected ranges are requested, with a remaining UTF-16 budget plus one unit for overflow detection. No descendants, document ranges, text values or names are used. Temporary focus/provider unavailability permits two 50 ms retries inside the overall two-second deadline. The parent watches the original foreground HWND while acquiring selection. It kills and awaits the helper on cancellation, IPC failure or timeout; the next request starts a fresh process. A kill-on-close Windows Job prevents a helper surviving the parent. `scripts/Test-Selection.ps1 [-Installed]` verifies repeated and changed selections plus recovery, using only a synthetic WPF window; it does not certify Edge compatibility.

IPC uses a random named pipe restricted to the current user, or the direct child's redirected standard streams, and a bounded length-prefixed protocol. It carries text in memory only. Application exceptions are reduced to enumerated error codes. Startup diagnostics contain only exception type and HRESULT.

Piper can retain the selected active voices in one CPU Python process. `KeepPiperLoaded` defaults to true, including when older settings omit it. The shell preloads at startup and after a voice selection change, displays readiness, and releases unselected models. The catalog contains Denis/Dmitri/Irina/Ruslan for Russian and LJSpeech for English; at most two selected models are retained. Turning the option off or selecting no Piper voices closes the process. Cold requests remain available with the option off. Exit closes the worker's kill-on-close Job.

Piper packages are separate from translation packages; Python and NumPy are reused without replacement. Exact `piper:` IDs are never substituted. Size and SHA-256 are verified before loading; size and file timestamps are checked before reusing a resident model, and changed files invalidate it. Legacy pinned configurations that omit phoneme_type (Irina) use the upstream eSpeak default; downloaded configurations are not rewritten. ONNX Runtime telemetry, CPU memory arena, memory patterns and idle thread spinning are disabled. Each model has two inference threads. A Windows Job has a 1 GiB private commit limit; this is not a physical RAM limit. The model can remain resident during a translation operation, so its idle working set must be included in the combined memory budget.

The process has a random local cancellation event, reset for each serial request. Each request has its own ONNX RunOptions and acknowledgement. Cancellation terminates inference or resets WinMM waveOut playback, retaining the loaded model. If acknowledgement does not arrive within 750 ms, the parent kills the worker; the next request must reload. Speech is split at sentence boundaries, with a maximum input segment of 220 Unicode code points. PCM is played from bounded memory buffers and released. Request dictionaries and inference/audio references are released after each operation; no request history is retained. The slider maps 0/50/100 to approximately 0.5/1/2 speed. Literal text cannot activate Piper's raw-phoneme extension. Python blocks socket, write and subprocess audit events; diagnostics are discarded. Numeric measurements distinguish current working set from the resident process's lifetime peak and first PCM readiness from acoustic output latency.

The Piper downloader is separate from inference, with fixed HTTPS hosts, a pinned revision, range requests, progress, cancellation and integrity verification. Preparation unloads resident voices, reuses verified files, tests every staged voice through real synthesis, then replaces the model directory. A failed or cancelled preparation retains the previous ready models. Reading never downloads a missing voice and never substitutes another ID. The UI offers explicit installation and leaves saved voice choices unchanged. Model data stays outside the MSIX so app updates preserve it.

Silero uses a separate PyTorch CPU process and package directory. Five exact `silero:v5_6_ru:<speaker>` IDs share one Russian model; 118 `silero:v3_en:en_0` through `silero:v3_en:en_117` IDs share one English model. The random speaker is not exposed. `KeepSileroLoaded` is independent of `KeepPiperLoaded` and defaults to true for older settings. Only active selected Silero voices are preloaded: at most one per language, two models sharing one PyTorch process. Switching speakers within a language retains the same model. Removing a language evicts only that model; disabling retention terminates the worker. English continues through Piper until the user explicitly selects a Silero voice. `PythonSpeechClient` supplies the common serial IPC, Windows cancellation event and kill-on-close Job; Python protocol, cancellation, memory counters and playback are shared in `speech_runtime.py`. The Silero Job has a 3 GiB private commit ceiling, not a physical-memory guarantee.

Silero uses two CPU threads, 24 kHz PCM, sentence fragments limited to 220 normalized characters and the model's five documented SSML rate presets. Source text is normalized for its selected language before chunking, then XML-escaped; it cannot add prosody or pauses. A fixed fixture in the selected language warms inference during preload. Cancellation is observed between native inference calls and during playback; if native inference does not return within the common 750 ms grace period, the process is terminated and the next request reloads. Python denies socket, write and subprocess audit events before importing PyTorch. Model size and hash are verified before deserialization; changed file metadata invalidates the resident instance.

`silero_text.py` expands integers, signed values, decimal fractions, numeric ranges, grouped numbers, version components and common time/memory units. Leading zeroes and integer identifiers longer than 15 digits are spoken digit by digit; decimal arithmetic never uses floating point. Expansion happens before the 220-character limit, while `ProcessedCharacters` still counts the original UTF-16 input. The language planner preserves dots between digits to avoid splitting decimals/versions before they reach the worker. This is a bounded rule set, not general Russian morphological analysis or date/formula interpretation.

Latin tokens in Russian Silero requests stay with the selected Russian speaker. A small product-name lexicon and spelled acronyms cover conventional names. Other words use the existing Piper `espeakbridge.pyd` and local English dictionary, loaded directly without importing Piper/ONNX or loading an English voice. `silero_latin.py` maps phonemes and primary stress to Russian spelling. Unknown phonemes or an unavailable bridge fall back to deterministic spelling conversion. The phonemizer retains its engine/dictionary, without an application cache of reading text. This is approximate English pronunciation, especially for names and other languages. English requests routed to Piper are unchanged; there is no automatic voice substitution.

`SileroModels` downloads only the selected language’s pinned model from its official HTTPS host. Preparation stages a verified copy, synthesizes all 5 Russian or 118 English speakers, then atomically publishes that file without replacing the other language’s model. Weights remain outside the MSIX. Downloading does not select a voice or rewrite settings. The Python environment is assembled from pinned wheels by `Prepare-SileroRuntime.ps1`; it does not alter Piper or translation packages.

The pinned V5.6 package has a mixed-alphabet SSML bug: its Russian package stores `ext_alph=None`, while the parser calls `.keys()` on that value. The loader normalizes it to an empty mapping in memory, leaving the downloaded file unchanged. Application-side normalization supplies pronounceable Russian spelling separately. Markdown asterisks/backticks are removed as presentation markers; the SSML escape layer additionally encodes any remaining asterisks because V5.6 scans raw markup before XML parsing. Ordinary, even incomplete Markdown cannot trigger the model's emphasis syntax.

The native C++ module supports both installed WinRT voices and SAPI 5 voices (including RHVoice). Existing WinRT IDs remain unchanged; SAPI IDs use `sapi5:` followed by the exact enumerated token ID. Missing tokens never fall back to another voice. WinRT speed maps 0/50/100 to 0.5/1/6; SAPI maps the slider in five-point steps to −10…10. SAPI uses `SPF_IS_NOT_XML`, a bounded 128 MiB in-memory PCM stream and an in-memory WAV header. Audio is played by the worker; terminating it stops synthesis/playback and releases the voice. No user audio or text is written to disk.

The RHVoice downloader lives in `Speech/Downloads`, separate from inference. Its embedded catalog pins official URLs, exact sizes and SHA-256 hashes. HTTPS redirects are restricted to GitHub release hosts. Cancellation removes partial files; valid cached installers are reused. Before UAC handoff the installer is verified again and locked against replacement. Only the official NSIS installer is elevated; the reader stays unelevated. The two installers currently lack Authenticode signatures, which is disclosed before installation. Installed availability is confirmed by enumeration and actual synthesis. The setup CLI links the same downloader implementation for project-local preparation and verification.

Language planning is limited to ru/en/ja and uses scripts and sentence boundaries, not a statistical language model. Since 0.1.0.19, ordinary automatic reading splits mixed-script sentences into the enabled Russian, English and Japanese voices, preserving their exact IDs and speeds. Within such sentences Han is treated as Japanese; Latin is treated as English, so this is not detection of arbitrary Chinese or European languages. Standalone Han-only, very short Latin and unsupported text retain the conservative primary-voice fallback. Primary-only reading and disabled auto-detection never switch voices.

Translation retains its separate conservative detector and shows an explicit source choice for uncertain spans, confined to this operation. It preflights all routes, model hashes and the target voice, prepares the entire result, then speaks with the primary voice. The CTranslate2/SentencePiece Python process has bounded standard-stream IPC, a 1.5 GiB commit limit and two CPU threads. Models are loaded one at a time. Japanese to Russian uses a direct OPUS-MT model with separate input and output tokenizers; there is no implicit pivot fallback. English to Russian and Japanese to English retain their Argos models. The preparation process converts pinned Marian data locally under a separate 3 GiB job limit. Existing verified models are reused, and the old installation is retained until the replacement passes integrity and real inference checks. See [translation](translation.md).

The region picker is a nonactivating overlay on the monitor containing the pointer. It records physical screen coordinates, polls only Escape during selection, supports right-click cancellation, and cancels when foreground changes. It never restores focus. It removes the overlay and flushes the compositor before the worker captures the selected rectangle. Production OCR checks package identity. Full interaction and DPI acceptance are pending.

The voice installer is independent from reading. Its language input is whitelisted. It installs only the Basic/TextToSpeech Windows capabilities, after explicit consent and a separate UAC prompt, then the parent checks enumeration and synthesis. No restart or display-language change is requested. Both Russian and English installation completed successfully on this PC, and both synthesized fixed phrases at three speeds.

`silero_english.py` expands English integers, grouping commas, dot decimals/versions, ranges, signs and common time/memory units before the English model filters unsupported characters. It keeps Latin text in English. Long/leading-zero identifiers use individual digits. This is a deterministic number frontend, not general date or formula interpretation. Actual model frontend tests verify that normalized number words survive its letters-only alphabet.


## Kokoro и VOICEVOX (с 0.1.0.16)

`PythonSpeechClient` обслуживает также Kokoro/VOICEVOX через общий
`additional_worker.py`. Каталоги моделей встроены в приложение; перед загрузкой
проверяются размер и SHA-256. Отдельные адаптеры `kokoro_engine.py` и
`voicevox_engine.py` используют общий протокол, счётчики памяти, отмену и
воспроизведение PCM в памяти. HTTP-сервер не запускается.

Kokoro использует одну INT8 ONNX-сессию и профили голосов из NPZ с
`allow_pickle=False`. Фонемизатор — уже встроенный eSpeak bridge Piper; en-US
соответствует `en-us`, en-GB — `en`. VOICEVOX использует C ABI Core 0.17.0,
VOICEVOX ONNX Runtime 1.17.3 и словарь Open JTalk 1.11; разрешены только Normal
style 2 и 11. Раздельные процессы исключают конфликт двух ONNX Runtime.

Выбранные модели удерживаются в памяти, опция общая для двух новых движков.
Kokoro не перезагружает веса при смене английского профиля; VOICEVOX выгружает
прежний VVM после переключения на другого персонажа. Без удержания процесс
завершается после чтения. Фрагменты ограничены 140 символами, фонемы Kokoro —
510 на вызов без отбрасывания хвоста. Отмена ORT использует RunOptions; для
неотменяемого вызова VOICEVOX действует завершение процесса после 750 мс ожидания.
Job Object ограничивает private commit каждого нового движка 3 ГиБ и завершает
его при закрытии приложения. Это аварийный предел, не обещанный расход ОЗУ.

В 0.1.0.17 оба адаптера используют до восьми потоков CPU (не больше числа
логических процессоров). Kokoro повторно использует arena и memory patterns;
spinning остаётся выключенным. `additional_text.py` отделяет японские
предложения без пробелов и достаточно длинные части по знакам паузы.
`LanguagePlanner.BatchForPrefetch` объединяет соседние предложения одного
голоса/языка/скорости до 1000 символов, сохраняя порядок и полный текст.
`audio_prefetch.py` готовит один следующий фрагмент во время воспроизведения
текущего. Semaphore берётся до продвижения генератора: очередь и незавершённый
синтез не могут накапливать дальнейшие фрагменты. При закрытии генератора
поток подготовки завершается до подтверждения запроса или повторного
использования модели. Измерения и ограничения приведены в
[speech-performance.md](speech-performance.md).

## Mixed-voice playback (0.1.0.19)

`WorkerClient.SpeakPlanAsync` owns one playback operation and uses the existing
resident engines. Python workers emit bounded PCM16 frames (at most 262144 raw
bytes) over their inherited stdout pipe. Native SAPI/WinRT workers return
bounded in-memory WAV data, parsed into the same PCM stream. No audio files or
HTTP server are used. Existing voice previews retain their worker playback path.

`PrefetchPlayback` has one producer and one playback consumer with a channel
capacity of one. Besides the playing block and queued block, a producer can
hold a block while waiting to enqueue; worker inference buffers and pipe
buffers are additional bounded overhead. Preparation can cross voice/engine
boundaries while the current block plays. Only one engine synthesizes at a time;
voice order and per-language speed are retained. This can hide synthesis
latency behind sufficiently long preceding speech, but cannot guarantee
continuous speech for rapid alternation of short words.

Cancellation stops the player and producer, drains/releases queued PCM arrays,
and waits for the worker's acknowledgement. A worker stuck in native inference
is killed after the existing 750 ms grace period. A cancelled stream is drained
before reusing its protocol. Native workers are terminated at cancellation.
The application holds the worker configuration gate through the whole plan.
Changing selected voices cannot mutate an active reading operation.
