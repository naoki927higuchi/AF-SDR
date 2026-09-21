"""Create a source-inclusive AF-SDR distribution; run after Release checks.
Python 3, Git, and the vendor source/binary tree in ../Lib are required.
No user recordings, settings, generator binaries, or test artifacts are copied.
"""
from pathlib import Path
import hashlib
import shutil
import subprocess
import tarfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LIB = ROOT.parent / 'Lib'
VERSION = ET.parse(ROOT / 'AF-SDR/AF-SDR.csproj').findtext('.//Version')
NAME = f'AF-SDR-{VERSION}-win-x64'
DEST = ROOT / 'dist' / NAME
if DEST.exists():
    raise SystemExit(f'Output already exists; preserve or move it before rebuilding: {DEST}')

def copy(source, relative):
    target = DEST / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)

def sha(path):
    return hashlib.file_digest(path.open('rb'), 'sha256').hexdigest()

archive = ROOT / 'packaging/vendor/libusb-1.0.29.tar.bz2'
assert sha(archive) == '5977fc950f8d1395ccea9bd48c06b3f808fd3c2c961b44b0c2e6e29fc3a70a85'
release = ROOT / f'AF-SDR/bin/Release-{VERSION}'
for name in ('AF-SDR.exe', 'AF-SDR.dll', 'AF-SDR.deps.json', 'AF-SDR.runtimeconfig.json', 'rtlsdr.dll'):
    copy(release / name, name)
for name in ('README-ja.txt', 'THIRD-PARTY-NOTICES.txt'):
    copy(ROOT / 'packaging' / name, name)
for name in ('COPYING', 'LICENSE-NOTICE.md'):
    copy(ROOT / 'AF-SDR' / name, name)
copy(ROOT / 'packaging/BUILD-ja.txt', 'Source/BUILD-ja.txt')
copy(ROOT / 'packaging/Build-AF-SDR.ps1', 'Source/Build-AF-SDR.ps1')

# Git whitelist deliberately excludes bin/obj and private untracked recordings.
tracked = subprocess.check_output(['git', 'ls-files', '-z', 'AF-SDR'], cwd=ROOT).decode().split('\0')
for name in filter(None, tracked):
    copy(ROOT / name, 'Source/AF-SDR/' + name)
for name in ('COPYING', 'LICENSE-NOTICE.md'):
    copy(ROOT / 'AF-SDR' / name, 'Source/AF-SDR/AF-SDR/' + name)
copy(release / 'rtlsdr.dll', 'Source/Lib/RTL-SDR-BLOG Windows Release V1.4.0/x64/rtlsdr.dll')
vendor = LIB / 'rtl-sdr-blog-1.4.0'
shutil.copytree(vendor, DEST / 'Source/ThirdParty/rtl-sdr-blog-1.4.0')
copy(vendor / 'COPYING', 'Licenses/rtl-sdr-COPYING')
copy(archive, 'Source/ThirdParty/' + archive.name)
with tarfile.open(archive) as tf:
    license_bytes = tf.extractfile('libusb-1.0.29/COPYING').read()
(DEST / 'Licenses/libusb-COPYING').write_bytes(license_bytes)

commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT).decode().strip()
(DEST / 'BUILD-INFO.txt').write_text(
    f'AF-SDR {VERSION}\nPlatform: Windows x64\nPackaging repository commit: {commit}\n'
    'Application source baseline: b2d5bc2 (1.12.1); application code unchanged.\n'
    'Deployment: framework-dependent; .NET 9 Windows Desktop Runtime x64 required.\n', encoding='utf-8')
entries = sorted(p for p in DEST.rglob('*') if p.is_file())
(DEST / 'SHA256SUMS.txt').write_text(''.join(f'{sha(p)}  {p.relative_to(DEST).as_posix()}\n' for p in entries), encoding='utf-8')
zip_path = DEST.parent / (DEST.name + '.zip')
with zipfile.ZipFile(zip_path, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for p in sorted(DEST.rglob('*')):
        if p.is_file():
            z.write(p, (Path(NAME) / p.relative_to(DEST)).as_posix())
with zipfile.ZipFile(zip_path) as z:
    assert z.testzip() is None
    for line in (DEST / 'SHA256SUMS.txt').read_text(encoding='utf-8').splitlines():
        digest, name = line.split('  ', 1)
        assert hashlib.sha256(z.read(NAME + '/' + name)).hexdigest() == digest, name
zip_path.with_suffix('.zip.sha256').write_text(f'{sha(zip_path)}  {zip_path.name}\n', encoding='ascii')
print(f'Verified {len(entries)} payload files: {zip_path} ({zip_path.stat().st_size:,} bytes)')
