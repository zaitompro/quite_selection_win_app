[English](README.md) | [Русский](README.ru.md)

# QuietSelection for Windows 11

An accessible, local desktop application for reading selected text, recognizing a user-selected screen region, and translating selections. C#/.NET 10 + WPF; a small C++/WinRT bridge uses Windows speech and OCR APIs. Local translation uses an isolated CTranslate2 CPU process.

**Development milestone, not a completed release.** Signed MSIX 0.1.0.21 is installed locally with private .NET and Python runtimes. Speech supports WinRT, SAPI 5 (RHVoice Elena/SLT), local Piper Denis/Dmitri/Irina/Ruslan/LJSpeech models, five Russian Silero V5.6 voices, eight English KittenTTS Mini 0.8 voices, 21 English Pocket TTS profiles, 28 English Kokoro INT8 profiles, and VOICEVOX Normal voices Shikoku Metan / Kurono Takehiro. The English Silero experiment was rejected after user listening; its model has been removed from application data and the download cache. Selected Piper, Silero, Kitten, Pocket, Kokoro and VOICEVOX models stay loaded by default; Speech settings can release their memory. English Piper LJSpeech remains available for comparison; the selected Micro Bella profile migrates to Mini Bella at the same speed. Kitten Micro was removed after user listening; Kokoro remains available for comparison. Kokoro/VOICEVOX use tuned CPU threads and prepare the next audio fragment during playback; see [performance measurements](docs/speech-performance.md). Russian and English Silero can share one resident PyTorch process. Alt+1–4 on the number row are supported alongside the previous shortcuts. Japanese to Russian uses a direct OPUS-MT model. Core tests: 55/55. Measured speech memory and installed verification are recorded in [current status](docs/status.md) and [voices](docs/voices.md). External compatibility acceptance, rollback, autostart, and accessibility polish remain unfinished.

Automatic reading routes Russian, English and Japanese insertions within a sentence to the exact selected voices of enabled languages. The next audio block is prepared during playback, including across engines. Rapid short insertions can still pause with a slow model. Alt+2 and disabled auto-detection retain the primary voice throughout. In that mode Russian Silero still uses approximate Russian pronunciation for Latin words through the existing local eSpeak dictionary. Numbers, decimal fractions, ranges and common units are normalized before synthesis.

## Repository layout

- `src/QuietSelection.Core`: settings, limits, conservative language planning, cancellation.
- `src/QuietSelection.App`: WPF interface, tray, commands, UI Automation selection, isolated workers, region picker.
- `src/QuietSelection.Native`: C++/WinRT speech and OCR bridge.
- `src/QuietSelection.Translation`: bounded offline Python inference worker.
- `src/QuietSelection.Speech`: bounded offline Piper, Silero, Kitten, Pocket, Kokoro and VOICEVOX CPU speech workers.
- `src/QuietSelection.Setup`: explicit signing, public-certificate trust, installation and voice servicing helper.
- `tests/QuietSelection.Core.Tests`: dependency-free behavior test runner.
- `assets/fixtures`: synthetic text and an offline HTML acceptance page.
- `scripts`: explicit SDK setup, build, tests, packaging and consent-gated installation helpers.
- `packaging`: stable MSIX identity and manifest.
- `docs`: architecture, privacy, verification, and remaining work.
- `.tools` and `artifacts`: ignored toolchain, build and test outputs.

## Build and run

Use Windows 11 x64, the pinned .NET SDK 10.0.402, MSVC x64 and Windows SDK 10.0.26100.0. Existing Visual Studio tools are reused. The SDK archive is downloaded into this repository only after consent and is checked against Microsoft's SHA512 metadata. No NuGet package source is enabled.

```powershell
./scripts/Install-ProjectSdk.ps1  # explicit network download; obtain consent first
./scripts/Get-TranslationBundle.ps1 -ConfirmDownload  # separately approved 357.5 MB
./scripts/Prepare-TranslationRuntime.ps1
./scripts/Get-DirectJapaneseModel.ps1 -ConfirmDownload  # separately approved 288.6 MB
./scripts/Build.ps1
./scripts/Test-Core.ps1
./scripts/Test-Privacy.ps1
./scripts/Test-WindowsApi.ps1    # opens a synthetic test window
./scripts/Start-QuietSelection.ps1
```

Settings appear in Russian. Configure explicit installed voices in Speech, and choose shortcuts in Keyboard shortcuts. No shortcuts or autostart are enabled on first launch. Closing settings keeps the tray process alive; Exit terminates it. During an operation any reading command acts as Stop. Model preparation converts the Marian archive locally and reuses verified installed models. Developer reproduction and limitations are documented in [local translation](docs/translation.md).

The voice wizard offers verified RHVoice installers, separate Piper and Silero model preparation actions, and Windows voice capabilities. SAPI installation may require elevation; Piper and Silero preparation do not. Download and synthesis are separate actions, and installing a voice never silently changes the saved selection. Read [voice installation and memory measurements](docs/voices.md) and [local translation](docs/translation.md).

## Packaging and privacy

`./scripts/Build-Package.ps1` prepares an unsigned MSIX with a private .NET runtime already present in the downloaded SDK. It installs nothing. [Package identity and local signing](docs/installation.md) require separate consent before changing trust or installing a package. The local certificate's private key must never be exported into the repository.

The app does not use the system clipboard, emulate copying, fetch entire documents, call cloud processing, keep reading history or log user text. User data crosses local current-user IPC into isolated workers. Resident speech workers retain model objects between requests and release request/audio references after each operation. Read [privacy and its limits](docs/privacy.md). Static source scanning is not a proof about every system binary.

The project source license is [MIT](LICENSE). Bundled runtimes have their own licenses, including Piper's GPL-3.0-or-later; upstream notices are preserved. Speech and translation models are separate application data. Silero V5.6 Russian and V3 English are CC BY-NC-SA 4.0, with a noncommercial restriction. See `third-party/piper`, `third-party/silero`, `third-party/translation`, [voice conditions](docs/voices.md) and [translation conditions](docs/translation.md).
