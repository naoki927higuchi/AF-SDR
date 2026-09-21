"""Package the unchanged AF-SignalGenerator Release with licensed source.
Run with Python 3 from a Git checkout after Release verification.
"""
from pathlib import Path
import hashlib
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PRODUCT = 'AF-SignalGenerator'
VERSION = ET.parse(ROOT / PRODUCT / (PRODUCT + '.csproj')).findtext('.//Version')
NAME = f'{PRODUCT}-{VERSION}-win-x64'
DEST = ROOT / 'dist' / NAME
if DEST.exists():
    raise SystemExit(f'Output exists; preserve or move it before rebuilding: {DEST}')
for license_name in ('COPYING', 'LICENSE-NOTICE.md'):
    if not (ROOT / PRODUCT / license_name).is_file():
        raise SystemExit('Product license must be established before packaging.')

def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def copy(source, relative):
    target = DEST / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)

release = ROOT / PRODUCT / 'bin/Release/net9.0-windows'
for extension in ('.exe', '.dll', '.deps.json', '.runtimeconfig.json'):
    copy(release / (PRODUCT + extension), PRODUCT + extension)
for name in ('COPYING', 'LICENSE-NOTICE.md', 'SIGNAL-MODEL.md'):
    copy(ROOT / PRODUCT / name, name)
copy(ROOT / 'packaging/generator/README-ja.txt', 'README-ja.txt')
for name in ('BUILD-ja.txt', 'Build-AF-SignalGenerator.ps1'):
    copy(ROOT / 'packaging/generator' / name, 'Source/' + name)
tracked = subprocess.check_output(['git', 'ls-files', '-z', PRODUCT], cwd=ROOT).decode().split('\0')
for name in filter(None, tracked):
    copy(ROOT / name, 'Source/' + name)
commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT).decode().strip()
(DEST / 'BUILD-INFO.txt').write_text(
    f'{PRODUCT} {VERSION}\nPlatform: Windows x64\nRepository commit: {commit}\n'
    'Deployment: framework-dependent; .NET 9 Windows Desktop Runtime x64 required.\n'
    'No bundled third-party native libraries or NuGet packages.\n', encoding='utf-8')
entries = sorted(p for p in DEST.rglob('*') if p.is_file())
(DEST / 'SHA256SUMS.txt').write_text(''.join(f'{sha(p)}  {p.relative_to(DEST).as_posix()}\n' for p in entries), encoding='utf-8')
archive = DEST.parent / (NAME + '.zip')
with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for p in sorted(DEST.rglob('*')):
        if p.is_file():
            z.write(p, (Path(NAME) / p.relative_to(DEST)).as_posix())
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    for line in (DEST / 'SHA256SUMS.txt').read_text(encoding='utf-8').splitlines():
        digest, name = line.split('  ', 1)
        assert hashlib.sha256(z.read(NAME + '/' + name)).hexdigest() == digest, name
archive.with_suffix('.zip.sha256').write_text(f'{sha(archive)}  {archive.name}\n', encoding='ascii')
print(f'Verified {len(entries)} payload files: {archive} ({archive.stat().st_size:,} bytes)')
