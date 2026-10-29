# Piper CPU runtime and separately downloaded voices

This local evaluation uses Piper TTS 1.8.0 (GPL-3.0), ONNX Runtime 1.31.0
(MIT), pathvalidate 3.3.1 (MIT), FlatBuffers 25.12.19 (Apache-2.0),
packaging 26.3 (Apache-2.0 / BSD-2-Clause) and protobuf 7.36.2 (BSD-3-Clause).
The complete upstream license files and bundled dependency notices are retained
in each wheel's `.dist-info` directory inside `piper-runtime`. The Piper wheel
also contains eSpeak NG and its language data. Python and NumPy are reused from
the existing private translation runtime; see `third-party/translation`.

Exact wheel URLs, sizes and SHA-256 digests: `runtime.json`. Obtain the original
Piper source at <https://github.com/OHF-Voice/piper1-gpl/tree/v1.8.0> and ONNX
Runtime source at <https://github.com/microsoft/onnxruntime/tree/v1.31.0>.
The wheels are extracted without modification. QuietSelection's adapter is in
`src/QuietSelection.Speech`. The application and models have not been published.
Any later redistribution must fulfil all applicable source and license obligations.

Models are downloaded separately and are not part of the MSIX. Their pinned
revision is `c10ece1aade47bb51c153c893d14e5bf8e5b7117` of
<https://huggingface.co/rhasspy/piper-voices>. Sizes and hashes are recorded in
`src/QuietSelection.App/Speech/Downloads/piper-models.json`.

- [Denis model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/ru/ru_RU/denis/medium/MODEL_CARD):
  Russian CC0 dataset, fine-tuned from Lessac medium. The dataset's CC0 status
  does not establish unrestricted rights to all base-model material. This is a
  free local evaluation; review Lessac conditions before distributing the model.
- [Dmitri model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/ru/ru_RU/dmitri/medium/MODEL_CARD):
  Russian CC0 dataset, fine-tuned from Lessac medium. The same base-model review
  applies before redistribution. This additional free local evaluation downloads
  63,206,118 bytes including its configuration and reuses the existing runtime.
- [LJSpeech model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/en/en_US/ljspeech/medium/MODEL_CARD):
  English public-domain dataset, trained from scratch.
- [Irina model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/ru/ru_RU/irina/medium/MODEL_CARD):
  Russian female voice, dataset source RHVoice, dataset license listed as Unknown.
  Fine-tuned from Lessac medium. Local evaluation was explicitly authorized after
  disclosing the unknown dataset license; do not treat free download availability
  as redistribution permission. Model and configuration total 63,206,059 bytes.
- [Ruslan model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/ru/ru_RU/ruslan/medium/MODEL_CARD):
  Russian male voice, RUSLAN dataset under CC BY-NC-SA 4.0. Fine-tuned from Lessac
  medium. Retain the noncommercial restriction and review applicable base-model
  conditions before redistribution. Model and configuration total 63,206,176 bytes.

Both additional models reuse the existing runtime. All five models and their
configurations total 316,360,821 bytes. Only selected active voices stay in memory.

No Japanese Piper voice or additional phonemizer is included in this bundle.
