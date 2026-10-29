"""Download CPU inference wheels sequentially, verify hashes, extract locally.

PyTorch, NumPy, PyYAML, sentencepiece and typing_extensions reuse existing runtimes.
No web server, audio upload, voice cloning or optional GPU packages are installed.
The first run records a lock; subsequent runs use the exact recorded artifacts.
"""
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / ".tools/pocket-runtime-downloads"
DEST = ROOT / ".tools/pocket-runtime"
LOCK = ROOT / "third-party/pocket/runtime.json"
PACKAGES = [
    "pocket-tts", "einops", "safetensors", "tokenizers", "pydantic",
    "pydantic-core", "annotated-types", "typing-inspection", "scipy",
    "huggingface-hub", "requests", "charset-normalizer", "idna", "urllib3",
    "certifi", "httpx", "httpcore", "h11", "anyio", "hf-xet", "packaging", "tqdm",
    "httpx2", "httpcore2", "click", "colorama", "truststore",
]


def compatible(name):
    parts = name[:-4].split("-")
    python, abi, platform = parts[-3:]
    if platform == "any":
        return "py3" in python.split(".") and abi == "none"
    if platform != "win_amd64":
        return False
    return (python == "cp313" and abi in ("cp313", "none")) or (
        abi == "abi3" and python.startswith("cp3") and int(python[2:]) <= 313)


def run():
    CACHE.mkdir(parents=True, exist_ok=True)
    DEST.mkdir(parents=True, exist_ok=True)
    LOCK.parent.mkdir(parents=True, exist_ok=True)
    locked = json.loads(LOCK.read_text("utf-8")) if LOCK.exists() else []
    for index, package in enumerate(PACKAGES):
        if index < len(locked):
            item = locked[index]
        else:
            version = "/3.3.0" if package == "pocket-tts" else ""
            with urllib.request.urlopen(f"https://pypi.org/pypi/{package}{version}/json") as response:
                metadata = json.load(response)
            candidates = [f for f in metadata["urls"] if f["filename"].endswith(".whl")
                          and compatible(f["filename"]) and not f["yanked"]]
            if not candidates:
                raise RuntimeError("NO_COMPATIBLE_WHEEL: " + package)
            wheel = candidates[0]
            item = dict(Package=package, Version=metadata["info"]["version"],
                        Name=wheel["filename"], Bytes=wheel["size"],
                        Sha256=wheel["digests"]["sha256"], Url=wheel["url"])
            locked.append(item)
            LOCK.write_text(json.dumps(locked, indent=2) + "\n", encoding="utf-8")
        path = CACHE / item["Name"]
        if not path.exists():
            print("Download " + item["Name"], flush=True)
            with urllib.request.urlopen(item["Url"]) as response, path.open("wb") as output:
                while block := response.read(65536):
                    output.write(block)
        if path.stat().st_size != item["Bytes"] or hashlib.sha256(path.read_bytes()).hexdigest() != item["Sha256"]:
            raise RuntimeError("WHEEL_HASH_MISMATCH")
        with zipfile.ZipFile(path) as archive:
            for entry in archive.infolist():
                target = (DEST / entry.filename).resolve()
                if not target.is_relative_to(DEST.resolve()):
                    raise RuntimeError("WHEEL_PATH_INVALID")
            archive.extractall(DEST)
        print("Verified " + item["Name"], flush=True)
    # This application runs eager CPU inference only. Importing Dynamo solely
    # for a compilation decorator/registration initializes writable disk caches,
    # which the speech worker deliberately forbids. No tensor math is changed.
    patches = [
        ("pocket_tts/modules/attention.py", "@torch.compiler.disable\n",
         "# QuietSelection: eager-only runtime; skip Dynamo decorator/cache initialization.\n"),
        ("einops/_torch_specific.py", "\nallow_ops_in_compiled_graph()\n",
         "\n# QuietSelection: eager-only runtime; no Dynamo registration or disk cache.\n"),
    ]
    for name, old, new in patches:
        path = DEST / name
        source = path.read_text("utf-8")
        if source.count(old) != 1:
            raise RuntimeError("EAGER_PATCH_SOURCE_CHANGED")
        path.write_text(source.replace(old, new), encoding="utf-8")


if __name__ == "__main__":
    run()
