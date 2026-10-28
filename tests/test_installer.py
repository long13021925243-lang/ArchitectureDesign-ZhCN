"""Windows installer integration tests using fresh simulated game directories only.

Requires a legally installed original game EXE supplied by --original. No game or
system-font file is changed. Font fault injection uses a separately compiled test
installer whose source points at this run's private font directory; production
code does not expose an environment-variable or command-line override.
"""
import argparse
import contextlib
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid


REPO = Path(__file__).resolve().parents[1]


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def relative(value):
    parts = value.replace('\\', '/').split('/')
    assert all(part and part not in ('.', '..') and ':' not in part for part in parts)
    return Path(*parts)


def run_process(args, timeout=240):
    return subprocess.run([str(arg) for arg in args], stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, timeout=timeout,
                          creationflags=subprocess.CREATE_NO_WINDOW)


@contextlib.contextmanager
def replacement_lock(path):
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    create = kernel.CreateFileW
    create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                       wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    create.restype = wintypes.HANDLE
    close = kernel.CloseHandle
    close.argtypes = [wintypes.HANDLE]
    close.restype = wintypes.BOOL
    # Allow reads/writes for preparation, but prevent File.Replace/Delete.
    handle = create(str(path), 0x80000000, 3, None, 3, 0x80, None)
    assert handle != wintypes.HANDLE(-1).value, ctypes.get_last_error()
    try:
        yield
    finally:
        assert close(handle)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--original', required=True, type=Path)
    parser.add_argument('--setup', type=Path, default=REPO / 'dist/ArchitectureDesign_ZhCN_Setup.exe')
    parser.add_argument('--manifest', type=Path, default=REPO / 'data/manifest.json')
    parser.add_argument('--legacy-setup', type=Path, help='Optional previously released R3 installer')
    parser.add_argument('--source', type=Path, default=REPO / 'src/Installer.cs')
    parser.add_argument('--csc', type=Path, default=Path(os.environ.get('WINDIR', 'C:/Windows')) /
                        'Microsoft.NET/Framework64/v4.0.30319/csc.exe')
    args = parser.parse_args()
    if os.name != 'nt':
        parser.error('These integration tests require Windows.')
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    manifest = read_json(args.manifest)
    assert sha(args.original) == manifest['originalSha256'], 'Original EXE hash mismatch'
    assert args.setup.is_file() and args.source.is_file() and args.csc.is_file()
    exe_relative = relative(manifest['exeRelative'])
    payload = {relative(item['relative']): item for item in manifest['payloadFiles']}
    fonts = {path: item['windowsFontName'] for path, item in payload.items() if item.get('windowsFontName')}
    assert len(fonts) == 2 and set(fonts.values()) == {'msyh.ttc', 'msyhbd.ttc'}
    windows_fonts = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Fonts'
    assert all((windows_fonts / name).is_file() for name in fonts.values()), 'Windows Chinese fonts are required'
    stamp = time.strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:8]
    run = REPO / '.test-runs' / stamp
    run.mkdir(parents=True, exist_ok=False)
    controlled_fonts = run / 'test-fonts'
    controlled_fonts.mkdir()
    report_path = run / 'report.json'
    report = {'status': 'running', 'production_setup_sha256': sha(args.setup),
              'patched_exe_sha256': manifest['patchedSha256'], 'tests': [],
              'scope': 'Only fresh simulated roots; original EXE and Windows font sources are read-only.'}

    def save():
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')

    def case(name, action):
        print('START ' + name, flush=True)
        item = {'name': name, 'status': 'running'}
        report['tests'].append(item)
        save()
        started = time.monotonic()
        try:
            details = action()
            item.update(status='passed', seconds=round(time.monotonic() - started, 3))
            if details is not None:
                item['details'] = details
            print('PASS ' + name, flush=True)
        except Exception as error:
            item.update(status='failed', error=str(error))
            report['status'] = 'failed'
            raise
        finally:
            save()

    def command(root, action, installer=None, expected=0):
        result = run_process([installer or args.setup, '/' + action, '/root', root, '/silent'])
        log_file = root / 'ArchitectureDesign_ZhCN_Installer.log'
        log = log_file.read_text(encoding='utf-8-sig') if log_file.exists() else ''
        if expected is not None:
            assert result.returncode == expected, (action, result.returncode, log[-1800:])
        return {'exit_code': result.returncode, 'log': log}

    def target_hashes(root):
        return {path: sha(root / path) if (root / path).exists() else None
                for path in [exe_relative, *payload]}

    def expected_hashes(font_directory=windows_fonts):
        result = {exe_relative: manifest['patchedSha256']}
        for path, item in payload.items():
            result[path] = sha(font_directory / fonts[path]) if path in fonts else item['sha256']
        return result

    def check(root, expected):
        assert target_hashes(root) == expected, 'Installed/backup target contents differ'
        assert (root / 'Saved/UserHouse.sav').read_bytes() == b'UNRELATED USER SAVE\x00\xff'

    def fixture(name, legacy_files=True):
        root = run / name
        target = root / exe_relative
        target.parent.mkdir(parents=True)
        shutil.copyfile(args.original, target)
        if legacy_files:
            for index, path in enumerate(payload):
                target = root / path
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(('PREINSTALL-RESOURCE-' + str(index)).encode('ascii'))
        (root / 'Saved').mkdir()
        (root / 'Saved/UserHouse.sav').write_bytes(b'UNRELATED USER SAVE\x00\xff')
        return root, target_hashes(root)

    def backup_dir(root):
        return root / 'ArchitectureDesign_ZhCN_Backup' / str(manifest['buildId'])

    def backup_hashes(root):
        base = backup_dir(root) / 'files'
        return {path.relative_to(base).as_posix(): sha(path) for path in base.rglob('*') if path.is_file()}

    def journal(root):
        return read_json(backup_dir(root) / 'journal.json')

    def compile_csharp(source, output, resources=()):
        command_line = [args.csc, '/nologo', '/target:exe', '/platform:x64', '/out:' + str(output)]
        command_line += ['/reference:' + name + '.dll' for name in
                         ['System.Web.Extensions', 'System.IO.Compression', 'System.IO.Compression.FileSystem',
                          'System.Windows.Forms', 'System.Drawing']]
        command_line += ['/resource:' + str(path) + ',' + name for path, name in resources]
        result = run_process([*command_line, source])
        assert result.returncode == 0, result.stdout.decode('utf-8', errors='replace')

    # Extract the actual release's embedded resources, rather than trusting a separate ZIP.
    extractor_source = run / 'ExtractResources.cs'
    extractor_source.write_text('''using System; using System.IO; using System.Reflection;
class ExtractResources { static void Main(string[] a) {
var assembly = Assembly.LoadFile(Path.GetFullPath(a[0])); Directory.CreateDirectory(a[1]);
foreach(var name in new[]{"manifest.json", "payload.zip"}) {
using(var source=assembly.GetManifestResourceStream(name)) {
if(source==null) throw new Exception("Missing resource: "+name);
using(var output=File.Create(Path.Combine(a[1], name))) source.CopyTo(output);
}}}}''', encoding='utf-8')
    extractor = run / 'ExtractResources.exe'
    compile_csharp(extractor_source, extractor)
    embedded = run / 'embedded'
    extracted = run_process([extractor, args.setup.resolve(), embedded])
    assert extracted.returncode == 0
    assert read_json(embedded / 'manifest.json') == manifest, 'Release and source manifest differ'
    test_source = run / 'Installer-TestOnly.cs'
    source = args.source.read_text(encoding='utf-8-sig')
    expression = 'Environment.GetFolderPath(Environment.SpecialFolder.Fonts)'
    assert source.count(expression) == 2, 'Re-review the production source before updating this test-only substitution'
    injected_path = '@"' + str(controlled_fonts).replace('"', '""') + '"'
    test_source.write_text('// TEST ONLY: isolated font fault injection.\n' + source.replace(expression, injected_path), encoding='utf-8-sig')
    test_setup = run / 'Installer-TestOnly.exe'
    compile_csharp(test_source, test_setup, [(embedded / name, name) for name in ['manifest.json', 'payload.zip']])
    report['test_only_setup_sha256'] = sha(test_setup)

    def font_variant(swapped=False):
        for name in fonts.values():
            source_name = ('msyhbd.ttc' if name == 'msyh.ttc' else 'msyh.ttc') if swapped else name
            shutil.copyfile(windows_fonts / source_name, controlled_fonts / name)

    def remove_test_fonts():
        for name in fonts.values():
            path = controlled_fonts / name
            assert path.parent == controlled_fonts and controlled_fonts.is_relative_to(run)
            path.unlink(missing_ok=True)

    main_root, main_before = fixture('production-cycle')
    initial_backup = {}
    try:
        def release_archive():
            import zipfile
            with zipfile.ZipFile(embedded / 'payload.zip') as archive:
                actual = {relative(name) for name in archive.namelist()}
                assert actual == set(payload) - set(fonts), 'ZIP has unexpected/missing/redistributed font files'
                for path in actual:
                    assert hashlib.sha256(archive.read(path.as_posix())).hexdigest() == payload[path]['sha256']
            return {'zip_payload_entries': len(actual), 'zip_font_entries': 0,
                    'note': 'PAK contents are checked by the separate release build verification.'}
        case('release_zip_contains_only_declared_nonfont_entries', release_archive)

        def install():
            command(main_root, 'install')
            check(main_root, expected_hashes())
            initial_backup.update(backup_hashes(main_root))
            assert initial_backup == {path.as_posix(): value for path, value in main_before.items()}
        case('production_install_preserves_original_files_and_save', install)
        case('production_verify', lambda: command(main_root, 'verify') and None)

        def repeat():
            old_journal = sha(backup_dir(main_root) / 'journal.json')
            command(main_root, 'install')
            check(main_root, expected_hashes())
            assert backup_hashes(main_root) == initial_backup
            assert sha(backup_dir(main_root) / 'journal.json') == old_journal
        case('repeat_install_has_no_payload_or_backup_changes', repeat)

        def repair():
            first_font = next(iter(fonts))
            (main_root / first_font).unlink()
            command(main_root, 'verify', expected=3)
            command(main_root, 'install')
            nonfont = next(path for path in payload if path not in fonts)
            (main_root / nonfont).write_bytes(b'INTENTIONALLY CORRUPT TEST RESOURCE')
            command(main_root, 'restore', expected=3)
            command(main_root, 'install')
            check(main_root, expected_hashes())
            assert backup_hashes(main_root) == initial_backup
        case('repair_missing_font_and_damaged_resource', repair)

        def restore():
            command(main_root, 'restore')
            check(main_root, main_before)
            assert backup_hashes(main_root) == initial_backup
        case('production_restore_is_byte_exact', restore)

        def clean_cycle():
            root, before = fixture('clean-install', legacy_files=False)
            command(root, 'install')
            check(root, expected_hashes())
            command(root, 'restore')
            check(root, before)
            assert backup_hashes(root) == {exe_relative.as_posix(): manifest['originalSha256']}
        case('clean_install_restore_removes_only_added_payload', clean_cycle)

        def unknown():
            root = run / 'unknown-version'
            target = root / exe_relative
            target.parent.mkdir(parents=True)
            target.write_bytes(b'MZ-UNKNOWN-VERSION-TEST')
            before = sha(target)
            command(root, 'install', expected=3)
            assert sha(target) == before and not backup_dir(root).exists()
        case('unknown_game_refused_without_mutation', unknown)

        if args.legacy_setup:
            def upgrade():
                root, before = fixture('legacy-upgrade')
                command(root, 'install', args.legacy_setup)
                backups = backup_hashes(root)
                before_fields = [(row['relative'], row['existedBefore'], row['beforeSha256']) for row in journal(root)['files']]
                command(root, 'install')
                command(root, 'verify')
                check(root, expected_hashes())
                assert backup_hashes(root) == backups
                assert [(row['relative'], row['existedBefore'], row['beforeSha256']) for row in journal(root)['files']] == before_fields
                command(root, 'restore')
                check(root, before)
            case('legacy_R3_upgrade_preserves_first_backup_and_restore', upgrade)

        dynamic, dynamic_before = fixture('font-variants')
        font_variant(False)
        expected_a = expected_hashes(controlled_fonts)
        font_variant(True)
        expected_b = expected_hashes(controlled_fonts)
        assert any(expected_a[path] != expected_b[path] for path in fonts)
        font_variant(False)
        dynamic_backups = {}

        def font_update():
            command(dynamic, 'install', test_setup)
            check(dynamic, expected_a)
            dynamic_backups.update(backup_hashes(dynamic))
            font_variant(True)
            command(dynamic, 'verify', test_setup)  # Recorded installed font A remains valid.
            command(dynamic, 'install', test_setup)
            check(dynamic, expected_b)
            assert backup_hashes(dynamic) == dynamic_backups
        case('different_valid_font_versions_and_system_font_update', font_update)

        def missing_restore():
            remove_test_fonts()
            command(dynamic, 'verify', test_setup)
            command(dynamic, 'restore', test_setup)
            check(dynamic, dynamic_before)
        case('verify_and_restore_without_system_fonts', missing_restore)

        def missing_install():
            before_journal = sha(backup_dir(dynamic) / 'journal.json')
            command(dynamic, 'install', test_setup, expected=3)
            check(dynamic, dynamic_before)
            assert backup_hashes(dynamic) == dynamic_backups
            assert sha(backup_dir(dynamic) / 'journal.json') == before_journal
            for name in fonts.values():
                (controlled_fonts / name).write_bytes(b'INVALID FONT TEST')
            command(dynamic, 'install', test_setup, expected=3)
            check(dynamic, dynamic_before)
            assert sha(backup_dir(dynamic) / 'journal.json') == before_journal
            remove_test_fonts()
        case('missing_or_invalid_fonts_refuse_install_without_mutation', missing_install)

        def update_rollback():
            font_variant(False)
            command(dynamic, 'install', test_setup)
            font_variant(True)
            first_font, last_font = list(fonts)
            with replacement_lock(dynamic / last_font):
                result = command(dynamic, 'install', test_setup, expected=None)
                assert result['exit_code'] != 0
            assert '已安装：' + str(first_font) in result['log'] and '正在回滚' in result['log']
            check(dynamic, expected_a)
            assert journal(dynamic)['status'] == 'rolled_back'
            assert backup_hashes(dynamic) == dynamic_backups
            command(dynamic, 'verify', test_setup)
            remove_test_fonts()
            command(dynamic, 'restore', test_setup)
            check(dynamic, dynamic_before)
        case('font_update_late_failure_rolls_back_and_restores_without_fonts', update_rollback)

        def corrupt_rollback():
            font_variant(False)
            command(dynamic, 'install', test_setup)
            first_font, last_font = list(fonts)
            (dynamic / first_font).write_bytes(b'BROKEN FONT TEST ONLY')
            corrupt_hash = sha(dynamic / first_font)
            font_variant(True)
            with replacement_lock(dynamic / last_font):
                result = command(dynamic, 'install', test_setup, expected=None)
                assert result['exit_code'] != 0
            assert sha(dynamic / first_font) == corrupt_hash
            command(dynamic, 'verify', test_setup, expected=3)
            command(dynamic, 'restore', test_setup, expected=3)
            command(dynamic, 'install', test_setup)
            check(dynamic, expected_b)
            command(dynamic, 'verify', test_setup)
            command(dynamic, 'restore', test_setup)
            check(dynamic, dynamic_before)
        case('failed_repair_does_not_accept_corrupt_font_as_valid', corrupt_rollback)
        report['status'] = 'passed'
    except Exception:
        report['status'] = 'failed'
        raise
    finally:
        report['finished_utc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
        report['total_tests'] = len(report['tests'])
        save()
        print('REPORT ' + str(report_path), flush=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
