# Silero local TTS

QuietSelection loads the official `v5_6_ru` model separately from the MSIX. It contains
`aidar`, `baya`, `kseniya`, `xenia` and `eugene`. The model is distributed under
CC BY-NC-SA 4.0; see `LICENSE-Silero.txt`. This is not the MIT-licensed CIS Base model.
No commercial-use permission is implied by free download or integration.

The optional official `v3_en` model supplies 118 fixed English speakers, `en_0`
through `en_117`. The random speaker is not exposed. Its 57,194,546-byte file uses
the same CC BY-NC-SA 4.0 model license and existing CPU runtime. Its HTTPS-acquired
local integrity pin is in `speech/silero-english-model.json`:
`02b71034d9f13bc4001195017bac9db1c6bb6115e03fea52983e8abcff13b665`.
No new runtime dependency is needed for English.

Sources:

- <https://github.com/snakers4/silero-models>
- <https://models.silero.ai/models/tts/ru/v5_6_ru.pt>
- <https://models.silero.ai/models/tts/en/v3_en.pt>
- <https://github.com/snakers4/silero-models/blob/master/LICENSE>
- <https://github.com/snakers4/silero-models/wiki/SSML>

CPU runtime: PyTorch 2.14.1+cpu for CPython 3.13, Windows x64. Runtime wheels, sizes,
official download URLs and published SHA-256 values are pinned in `runtime.json`.
Wheel license/notice files remain in their original package/dist-info directories
inside `silero-runtime`. The existing private Python runtime is reused.

The model hash in `speech/silero-model.json` pins the file acquired over HTTPS from
the official publisher. It is a local integrity pin, not an independently published
signature or publisher checksum. No model code is executed until its size and hash
match this catalog. No reading text or audio is included in downloads or notices.

The application uses the model's documented SSML prosody rates with escaped literal
text, explicit speaker IDs, 24 kHz output and CPU inference. Model weights are not
modified. Project code controlling inference and playback is separate from the model.
