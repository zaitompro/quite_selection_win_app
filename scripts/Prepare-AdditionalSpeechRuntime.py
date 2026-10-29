"""Recreate the project-local VOICEVOX runtime from pinned downloaded archives."""
from pathlib import Path
import hashlib
import shutil
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ASSETS = {
    'voicevox_core-windows-x64-0.17.0.zip': '5a116b73fe338d36e63ae651279246f5968d0866a73780209c5f7b43c9692216',
    'voicevox_onnxruntime-win-x64-1.17.3.tgz': '32b07e7c6c59030434b54eb0255912510942faf193c7a14e0e1a168db5796265',
    'open_jtalk_dic_utf_8-1.11.tar.gz': 'fe6ba0e43542cef98339abdffd903e062008ea170b04e7e2a35da805902f382a',
}
destination = ROOT / '.tools' / 'voicevox-runtime'
destination.mkdir(exist_ok=True)
for name, expected in ASSETS.items():
    archive = ROOT / '.tools' / 'voicevox-downloads' / name
    with archive.open('rb') as source:
        if hashlib.file_digest(source, 'sha256').hexdigest() != expected:
            raise ValueError('RUNTIME_ARCHIVE_HASH_MISMATCH')
    if name.endswith('.zip'):
        with zipfile.ZipFile(archive) as source:
            for item in source.infolist():
                if not item.is_dir() and Path(item.filename).suffix not in ('.lib', '.pdb'):
                    (destination / Path(item.filename).name).write_bytes(source.read(item))
    else:
        with tarfile.open(archive) as source:
            for item in source.getmembers():
                if not item.isfile() or Path(item.name).suffix == '.lib':
                    continue
                folder = destination / 'dictionary' if name.startswith('open_jtalk') else destination
                folder.mkdir(exist_ok=True)
                with source.extractfile(item) as data, (folder / Path(item.name).name).open('wb') as output:
                    shutil.copyfileobj(data, output)
print('VOICEVOX_RUNTIME_PREPARED')
