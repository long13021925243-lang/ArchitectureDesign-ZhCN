"""Build the public installer without a game executable or bundled system fonts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PAK_RELATIVE = 'ArchitectureDesign/Content/Paks/zzz_ArchitectureDesign_ZhCN_P.pak'
DATA_NAMES = ('MaterialBibliothek.json', 'PatchNotes.json')
FONT_NAMES = {
    'ArchitectureDesign/Content/Data/Fonts/CN.ttf': 'msyh.ttc',
    'ArchitectureDesign/Content/Data/Fonts/CNB.ttf': 'msyhbd.ttc',
}

def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def run(argv):
    subprocess.run([str(arg) for arg in argv], check=True)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repak', type=Path, help='Optional repak 0.2.3 path to independently unpack and verify the canonical PAK')
    parser.add_argument('--csc', type=Path, default=Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe')
    parser.add_argument('--dist', type=Path, default=ROOT / 'dist')
    args = parser.parse_args()
    if args.repak:
        args.repak = args.repak.resolve(strict=True)
    args.csc = args.csc.resolve(strict=True)
    dist = args.dist.resolve()
    dist.mkdir(parents=True, exist_ok=True)
    manifest_path = ROOT / 'data/manifest.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    declared = {entry['relative']: entry for entry in manifest['payloadFiles']}
    if len(declared) != len(manifest['payloadFiles']):
        raise ValueError('Duplicate payload path in manifest')
    local_fonts = {name: item['windowsFontName'] for name, item in declared.items() if item.get('windowsFontName')}
    if local_fonts != FONT_NAMES:
        raise ValueError('Public installer must source exactly CN/CNB from Windows Fonts')
    expected_static = {'ArchitectureDesign/Content/Data/' + name for name in DATA_NAMES} | {PAK_RELATIVE}
    if set(declared) - set(local_fonts) != expected_static:
        raise ValueError('Unexpected static payload; refusing to publish it')
    version = subprocess.check_output([str(args.repak), '--version'], text=True).strip() if args.repak else None
    with tempfile.TemporaryDirectory(prefix='ArchitectureDesign-ZhCN-build-', dir=dist) as temp:
        stage = Path(temp)
        if stage.resolve().parent != dist:
            raise ValueError('Temporary build directory escaped the output directory')
        input_root = stage / 'pak-input'
        for name in DATA_NAMES:
            relative = 'ArchitectureDesign/Content/Data/' + name
            source = ROOT / 'data/localized-data' / name
            if sha256(source) != declared[relative]['sha256']:
                raise ValueError('Localized JSON hash differs from the audited manifest: ' + name)
            target = input_root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
        pak = ROOT / 'data/payload/zzz_ArchitectureDesign_ZhCN_P.pak'
        if sha256(pak) != declared[PAK_RELATIVE]['sha256']:
            raise ValueError('Canonical PAK hash differs from manifest')
        if args.repak:
            unpacked = stage / 'pak-roundtrip'
            run([args.repak, 'unpack', pak, '-o', unpacked])
            unpacked_files = {p.relative_to(unpacked).as_posix(): p for p in unpacked.rglob('*') if p.is_file()}
            if set(unpacked_files) != expected_static - {PAK_RELATIVE}:
                raise ValueError('PAK contains undeclared files or fonts')
            for name, path in unpacked_files.items():
                if sha256(path) != declared[name]['sha256']:
                    raise ValueError('PAK roundtrip mismatch: ' + name)
        sources = {name: input_root / name for name in expected_static - {PAK_RELATIVE}}
        sources[PAK_RELATIVE] = pak
        zip_path = dist / 'payload.zip'
        with zipfile.ZipFile(zip_path, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name in sorted(sources):
                entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                entry.create_system = 0
                entry.external_attr = 0x20
                archive.writestr(entry, sources[name].read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
        with zipfile.ZipFile(zip_path) as archive:
            if set(archive.namelist()) != expected_static:
                raise ValueError('Unexpected archive members')
            for name in archive.namelist():
                if hashlib.sha256(archive.read(name)).hexdigest() != declared[name]['sha256']:
                    raise ValueError('ZIP verification failed: ' + name)
        installer = dist / 'ArchitectureDesign_ZhCN_Setup.exe'
        run([args.csc, '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
             '/out:' + str(installer), '/resource:' + str(manifest_path) + ',manifest.json',
             '/resource:' + str(zip_path) + ',payload.zip',
             '/reference:System.Web.Extensions.dll', '/reference:System.IO.Compression.dll',
             '/reference:System.IO.Compression.FileSystem.dll', '/reference:System.Windows.Forms.dll',
             '/reference:System.Drawing.dll', ROOT / 'src/Installer.cs'])
        info = {
            'revision': manifest['revision'], 'gameBuildId': manifest['buildId'],
            'originalExeSha256': manifest['originalSha256'], 'patchedExeSha256': manifest['patchedSha256'],
            'installer': {'file': installer.name, 'bytes': installer.stat().st_size, 'sha256': sha256(installer)},
            'payload': {'file': zip_path.name, 'bytes': zip_path.stat().st_size, 'sha256': sha256(zip_path), 'entries': sorted(sources)},
            'pakSha256': sha256(pak), 'manifestSha256': sha256(manifest_path),
            'installerSourceSha256': sha256(ROOT / 'src/Installer.cs'), 'repakVersion': version,
            'pakRoundtripVerified': bool(args.repak), 'bundledSystemFontFiles': 0,
            'notes': 'The audited canonical PAK avoids repak parallel ordering differences. Payload ZIP has fixed entry order and timestamps. Legacy csc output is not promised to be byte-for-byte deterministic.'
        }
        (dist / 'build-info.json').write_text(json.dumps(info, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print(json.dumps(info, ensure_ascii=True, indent=2))

if __name__ == '__main__':
    main()
