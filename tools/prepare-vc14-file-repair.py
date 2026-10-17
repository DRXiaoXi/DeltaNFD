"""Extract pinned 14.42 originals for forced-file MSI repair; never run an installer."""
import argparse
import ctypes
from ctypes import wintypes
import importlib.util
import json
import pathlib
import shutil
import struct
import tempfile
import xml.etree.ElementTree as ET

REPO = pathlib.Path(__file__).resolve().parents[1]
DEST = REPO / 'src/DeltaNFD/Assets/Vc14FileRepair14.42'
CATALOG = REPO / 'src/DeltaNFD.Execution/Execution/Vc14FileRepair.catalog.json'
HASHES = {'x64': '1821577409C35B2B9505AC833E246376CC68A8262972100444010B57226F0940',
          'x86': 'DD1A8BE03398367745A87A5E35BEBDAB00FDAD080CF42AF0C3F20802D08C25D4'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seven-zip', default='C:/Program Files/7-Zip/7z.exe')
    parser.add_argument('--refresh-catalog', action='store_true')
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('official', REPO / 'tools/prepare-runtime-cpp-official.py')
    official = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(official)

    def unpack(exe, work):
        data = exe.read_bytes()
        cabinets = []
        offset = 0
        while (offset := data.find(b'MSCF', offset)) >= 0:
            size = struct.unpack_from('<I', data, offset + 8)[0]
            if 36 <= size <= len(data) - offset:
                cabinet = work / (str(offset) + '.cab')
                cabinet.write_bytes(data[offset:offset + size])
                folder = work / str(offset)
                official.extract(args.seven_zip, cabinet, folder)
                cabinets.append(folder)
            offset += 4
        manifests = [c / '0' for c in cabinets if (c / '0').is_file()]
        if len(manifests) != 1: raise ValueError('Ambiguous Burn manifest')
        result = []
        for payload in ET.parse(manifests[0]).getroot().findall('{*}Payload'):
            relative = pathlib.PureWindowsPath(payload.attrib['FilePath'])
            if relative.is_absolute() or '..' in relative.parts: raise ValueError('Unsafe original path')
            if relative.suffix.lower() not in ('.msi', '.cab'): continue
            source = payload.attrib['SourcePath']
            if pathlib.PureWindowsPath(source).name != source: raise ValueError('Unsafe source')
            found = [c / source for c in cabinets if (c / source).is_file()]
            if len(found) != 1: raise ValueError('Missing original payload')
            import hashlib
            if hashlib.sha1(found[0].read_bytes()).hexdigest().upper() != payload.attrib['Hash'].upper():
                raise ValueError('Payload differs from pinned original')
            result.append(('/'.join(relative.parts), found[0]))
        return result
    msi = ctypes.WinDLL('msi')
    msi.MsiOpenDatabaseW.argtypes = [wintypes.LPCWSTR, ctypes.c_void_p, ctypes.POINTER(wintypes.UINT)]
    msi.MsiDatabaseOpenViewW.argtypes = [wintypes.UINT, wintypes.LPCWSTR, ctypes.POINTER(wintypes.UINT)]
    msi.MsiViewExecute.argtypes = [wintypes.UINT, wintypes.UINT]
    msi.MsiViewFetch.argtypes = [wintypes.UINT, ctypes.POINTER(wintypes.UINT)]
    msi.MsiRecordGetStringW.argtypes = [wintypes.UINT, wintypes.UINT, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]

    def rows(path, sql):
        database, view, record = wintypes.UINT(), wintypes.UINT(), wintypes.UINT()
        def check(code):
            if code: raise ValueError('MSI read-only query failed: ' + str(code))
        check(msi.MsiOpenDatabaseW(str(path), None, ctypes.byref(database)))
        result = []
        try:
            check(msi.MsiDatabaseOpenViewW(database, sql, ctypes.byref(view)))
            check(msi.MsiViewExecute(view, 0))
            while (code := msi.MsiViewFetch(view, ctypes.byref(record))) == 0:
                try:
                    buffer, length = ctypes.create_unicode_buffer(4096), wintypes.DWORD(4096)
                    check(msi.MsiRecordGetStringW(record, 1, buffer, ctypes.byref(length)))
                    result.append(buffer.value)
                finally: msi.MsiCloseHandle(record)
            if code != 259: check(code)
            return result
        finally:
            if view.value: msi.MsiCloseHandle(view)
            msi.MsiCloseHandle(database)

    packages = []
    scratch = REPO / '_buildcheck'
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='vc14-file-source-', dir=scratch) as temporary:
        work = pathlib.Path(temporary).resolve()
        if work.parent != scratch.resolve() or not work.name.startswith('vc14-file-source-'):
            raise ValueError('Unexpected isolated extraction root')
        for arch, expected in HASHES.items():
            exe = REPO / f'src/DeltaNFD/Assets/Vc14Baseline14.42/vc_redist.{arch}.exe'
            if official.digest(exe) != expected: raise ValueError('Baseline original hash mismatch')
            extracted = work / arch
            extracted.mkdir()
            files = unpack(exe, extracted)
            msi_folders = {pathlib.PurePosixPath(name).parent for name, _ in files if name.endswith(f'_{arch}.msi')}
            selected = [(name, path) for name, path in files if pathlib.PurePosixPath(name).parent in msi_folders]
            if len(selected) != 4: raise ValueError('Expected only two MSI and two original CAB sources: ' + str([n for n, _ in selected]))
            prepared = {}
            for name, source in selected:
                relative = pathlib.PurePosixPath(arch, name)
                target = work / 'prepared' / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
                prepared[name] = (str(relative), target)
            for role in ('Minimum', 'Additional'):
                name = next(n for n in prepared if n.endswith(f'vc_runtime{role}_{arch}.msi'))
                path = prepared[name][1]
                def prop(key): return rows(path, f"SELECT `Value` FROM `Property` WHERE `Property`='{key}'")[0]
                if prop('ProductVersion') != '14.42.34433' or prop('Manufacturer') != 'Microsoft Corporation':
                    raise ValueError('Unexpected original MSI identity')
                cabinets = rows(path, 'SELECT `Cabinet` FROM `Media`')
                if len(cabinets) != 1 or '/' in cabinets[0] or '\\' in cabinets[0] or cabinets[0].startswith('#'):
                    raise ValueError('Unexpected MSI media source')
                media = str(pathlib.PurePosixPath(name).parent / cabinets[0])
                records = [dict(Path=prepared[n][0], Bytes=prepared[n][1].stat().st_size, Sha256=official.digest(prepared[n][1]))
                           for n in (name, media)]
                packages.append(dict(Architecture=arch, Role=role, Code=prop('ProductCode'), Name=prop('ProductName'),
                                     Version=prop('ProductVersion'), UpgradeCode=prop('UpgradeCode'), OriginalSha256=expected, Files=records))
        encoded = (json.dumps(packages, indent=2) + '\n').encode('utf-8')
        if args.refresh_catalog: CATALOG.write_bytes(encoded)
        elif not CATALOG.exists() or json.loads(CATALOG.read_text(encoding='utf-8-sig')) != packages:
            raise ValueError('Source differs from reviewed catalog; nothing copied')
        for source in (work / 'prepared').rglob('*'):
            if source.is_file():
                target = DEST / source.relative_to(work / 'prepared')
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
    print('Prepared four pinned 14.42 MSI/CAB packages. No installer executed.')


if __name__ == '__main__': main()
