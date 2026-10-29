"""Offline conversion of pinned Marian data; never executes archive scripts."""
import sys
import shutil
from pathlib import Path

sys.dont_write_bytecode = True


def deny_network_and_processes(event, args):
    if event.startswith("socket.") or event in ("subprocess.Popen", "os.system", "os.startfile"):
        raise PermissionError("OFFLINE_PREPARATION")


sys.addaudithook(deny_network_and_processes)
from ctranslate2.converters import MarianConverter

MODEL = "opus.spm32k-spm32k.transformer-align.model1.npz.best-perplexity.npz"
VOCAB = "opus.spm32k-spm32k.vocab.yml"


def prepare(source, destination):
    source, destination = Path(source).resolve(), Path(destination).resolve()
    if destination.exists() or source == destination or source in destination.parents:
        raise ValueError("INVALID_DESTINATION")
    converter = MarianConverter(str(source / MODEL), [str(source / VOCAB)])
    converter.convert(str(destination / "model"), quantization="int8")
    for name in ("source.spm", "target.spm", "README.md", "LICENSE"):
        shutil.copyfile(source / name, destination / name)


if __name__ == "__main__":
    try:
        prepare(sys.argv[1], sys.argv[2])
    except BaseException:
        # The caller receives only a failure code, never dependency diagnostics.
        raise SystemExit(1)
