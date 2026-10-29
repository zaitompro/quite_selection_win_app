"""Promote the already verified evaluation assets to application catalogs."""
import hashlib
import ast
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "src/QuietSelection.App/Speech/Downloads"
models = json.loads((ROOT / "scripts/catalogs/tts-evaluation.json").read_text("utf-8"))["Models"]


def files(model, names):
    result = []
    for item in model["Files"]:
        if item["Name"] not in names:
            continue
        path = ROOT / ".tools" / model["CacheDirectory"] / item["Name"]
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        if path.stat().st_size != item["Bytes"] or (item["Sha256"] and digest != item["Sha256"]):
            raise ValueError("MODEL_HASH_MISMATCH")
        # Application catalogs use flat, allowlisted filenames.
        if path.name != item["Name"]:
            shutil.copyfile(path, path.parent.parent / path.name)
        result.append(dict(Name=path.name, Bytes=item["Bytes"], Sha256=digest, Url=item["Url"]))
    return result


kitten = json.loads((TARGET / "kitten-models.json").read_text("utf-8-sig"))
for voice in kitten["Voices"]:
    voice["Id"] = voice["Id"].replace(":micro-", ":mini-")
    voice["Name"] = voice["Name"].replace("Micro", "Mini")
kitten["Files"] = files(models[0], ["kitten_tts_mini_v0_8.onnx", "voices.npz", "config.json"])
(TARGET / "kitten-models.json").write_text(json.dumps(kitten, ensure_ascii=False, indent=2) + "\n", "utf-8")
pocket_files = files(models[1], [f["Name"] for f in models[1]["Files"] if f["Name"] in ("model.safetensors", "tokenizer.json", "english-upstream.yaml") or f["Name"].startswith("voices/")])
voices = []
source_tree = ast.parse((ROOT / ".tools/pocket-runtime/pocket_tts/utils/utils.py").read_text("utf-8"))
sources = next(ast.literal_eval(node.value) for node in source_tree.body if isinstance(node, ast.Assign)
               and any(isinstance(target, ast.Name) and target.id == "_ORIGINS_OF_PREDEFINED_VOICES" for target in node.targets))
attribution = []
for item in models[1]["Files"]:
    if item["Name"].startswith("voices/"):
        name = Path(item["Name"]).stem
        source = sources[name]
        license_id = "CC-BY-NC-4.0" if "/expresso/" in source or "/ears/" in source else "CC0-1.0" if "/voice-zero/" in source or "/voice-donations/" in source else "CC-BY-4.0"
        suffix = " — только некоммерческое использование" if license_id == "CC-BY-NC-4.0" else ""
        voices.append(dict(Id="pocket:english-2026-09_2:" + name, Speaker=name,
                           Name="Pocket TTS " + name.replace("_", " ").title() + " (английский)" + suffix, Language="en-US"))
        attribution.append(dict(Voice=name, Source=source, License=license_id))
(TARGET / "pocket-models.json").write_text(json.dumps(dict(Files=pocket_files, Voices=voices), ensure_ascii=False, indent=2) + "\n", "utf-8")
(ROOT / "third-party/pocket/voice-sources.json").write_text(json.dumps(attribution, indent=2) + "\n", "utf-8")
