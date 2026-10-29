"""Reproducible developer preparation of the approved archive; no network access."""
import hashlib
import importlib.util
import json
import shutil
import sys
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
archive = root / ".tools/translation-downloads/opus-2021-02-18.zip"
receipt = json.loads((archive.parent / "direct-ja-ru-receipt.json").read_text(encoding="utf-8-sig"))
catalog = json.loads((root / "src/QuietSelection.App/Translation/Downloads/models.json").read_text(encoding="utf-8-sig"))
pinned = next(item for item in catalog if item["Name"] == archive.name)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


if archive.stat().st_size != 288618087 or receipt["sha256"] != pinned["Sha256"] or digest(archive) != pinned["Sha256"]:
    raise SystemExit("ARCHIVE_RECEIPT_MISMATCH")

artifact = root / "artifacts/direct-ja-ru"
source = artifact / "ja_ru_source"
destination = root / "artifacts/translation-fixture-models/ja_ru"
names = ("opus.spm32k-spm32k.transformer-align.model1.npz.best-perplexity.npz",
         "opus.spm32k-spm32k.vocab.yml", "source.spm", "target.spm", "README.md", "LICENSE")
source_files = []
with zipfile.ZipFile(archive) as package:
    if len(package.infolist()) > 100 or sum(x.file_size for x in package.infolist()) != 313165621:
        raise SystemExit("ARCHIVE_LIMIT")
    for name in names:
        matches = [item for item in package.infolist() if item.filename == name]
        if len(matches) != 1:
            raise SystemExit("ARCHIVE_ENTRY")
        source.mkdir(parents=True, exist_ok=True)
        path = source / name
        if not path.exists():
            with package.open(matches[0]) as input_stream, path.open("xb") as output_stream:
                shutil.copyfileobj(input_stream, output_stream)
        if path.stat().st_size != matches[0].file_size:
            raise SystemExit("SOURCE_SIZE")
        source_files.append({"Path": "ja_ru_source/" + name, "Bytes": path.stat().st_size,
                             "Sha256": digest(path), "ArchivePath": name})
if source_files != pinned["SourceFiles"]:
    raise SystemExit("SOURCE_HASH_MISMATCH")

spec = importlib.util.spec_from_file_location("prepare_marian", root / "src/QuietSelection.Translation/prepare_marian.py")
converter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(converter)
if not destination.exists():
    converter.prepare(source, destination)
files = [{"Path": "ja_ru/" + file.relative_to(destination).as_posix(), "Bytes": file.stat().st_size,
          "Sha256": digest(file)} for file in sorted(destination.rglob("*")) if file.is_file()]
if files != pinned["Files"]:
    raise SystemExit("CONVERTED_HASH_MISMATCH")
candidate = {"Name": archive.name, "Bytes": archive.stat().st_size, "Sha256": receipt["sha256"],
             "Url": receipt["source"], "Files": files, "Converter": "marian-ja-ru-v1", "SourceFiles": source_files}
(artifact / "catalog-entry.json").write_text(json.dumps(candidate, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"conversionComplete": True, "runtimeBytes": sum(file["Bytes"] for file in files),
                  "catalogCandidate": str(artifact / "catalog-entry.json")}))
