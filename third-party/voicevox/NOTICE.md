# VOICEVOX model preparation

Only two talk-model files from the official stable model release 0.16.4 are
selected. The catalog uses the SHA-256 digests published in GitHub release asset
metadata. Models are cached in `.tools/voicevox-downloads/0.16.4`, outside source
control. Installation copies verified model weights into the application model directory.

| Requested voice | Style | Style ID | File | Bytes |
| --- | --- | ---: | --- | ---: |
| 四国めたん (Shikoku Metan) | ノーマル (Normal) | 2 | 0.vvm | 58,214,379 |
| 玄野武宏 (Kurono Takehiro) | ノーマル (Normal) | 11 | 4.vvm | 58,211,265 |

Total model download: 116,425,644 bytes. The publisher bundles other speakers
and styles in these required files; these are not separate per-style downloads.
The integration selection is restricted to the two Normal style IDs above.
No additional whisper, emotional-style or singing models are selected.
The model archives remain unmodified.

`README.txt` and `TERMS.txt` are the original release notices. Model embedding and
generated audio are governed by those terms and the linked voice-specific terms.
Required credit strings for the requested voices are `VOICEVOX:四国めたん` and
`VOICEVOX:玄野武宏`. This model preparation does not publish or redistribute an app.

Sources:

- https://github.com/VOICEVOX/voicevox_vvm/releases/tag/0.16.4
- https://github.com/VOICEVOX/voicevox_vvm
- https://zunko.jp/con_ongen_kiyaku.html
- https://www.virvoxproject.com/voicevoxの利用規約

Runtime integration: VOICEVOX Core C API 0.17.0, CPU VOICEVOX ONNX Runtime
1.17.3 and Open JTalk UTF-8 dictionary 1.11 are bundled in the application.
They execute in an isolated worker through the C ABI, without an HTTP server.
The selected VVM remains loaded; changing to the other character unloads the
previous VVM. Text/query/audio stay in memory. A native synthesis that cannot
acknowledge cancellation is terminated by the client after its 750 ms grace.

The runtime archives total 29,745,065 bytes. Get-AdditionalSpeech.ps1 downloads
sequentially; Prepare-AdditionalSpeechRuntime.py verifies and extracts them.
Core has a publisher asset digest; the older ORT/dictionary hashes were recorded
after HTTPS download. Included runtime/dictionary notices remain applicable.

https://github.com/VOICEVOX/voicevox_core/blob/0.17.0/docs/guide/user/usage.md
