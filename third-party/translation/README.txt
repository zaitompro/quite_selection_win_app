QuietSelection local translation components

CTranslate2 4.8.3: MIT, OpenNMT/SYSTRAN.
SentencePiece 0.2.2: Apache-2.0, Google; bundled dependency licenses retained.
Argos model weights: MIT/CC0 as confirmed by maintainer PJ-Finlay:
https://github.com/argosopentech/argos-translate/issues/533
Model README files, including training-corpus notices, remain with each installed model.

CPython 3.13.16: original LICENSE.txt retained in translation-runtime.
NumPy 2.5.4 and PyYAML 6.0.3: all original dist-info/licenses retained in runtime/packages.
The wheels' original metadata and download/hash receipts are retained.

CTranslate2 Windows wheel includes Intel OpenMP and statically linked components.
oneDNN 3.1.1 license and THIRD-PARTY-PROGRAMS retained from its official tag.
Intel ISSL and notices retained from Intel's official ScalableVectorSearch LICENSE;
this document also includes its additional Apache terms, which do not relicense CTranslate2.
Pinned CTranslate2 third_party submodule licenses are retained in this directory.
Small bundled header source files are retained unchanged to preserve their notices.
Unused cudnn64_9.dll was omitted from the CPU runtime; original download was not modified.

This is a local development build. A public release still requires a final audit
of the binary redistribution notices, including Microsoft runtime and CUDA code
compiled into the upstream CTranslate2 DLL. No CUDA toolkit is downloaded or used.
