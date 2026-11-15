#!/usr/bin/env python3
"""Restore pinned 14.51 build assets from local official bundles. Never run the bundles."""
import argparse
import hashlib
import json
import pathlib
import shutil
import stat
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def regular(path):
    if not path.is_file() or path.is_symlink() or path.stat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
        raise ValueError(f'Not a regular local file: {path}')


def safe_child(root, relative):
    parts = pathlib.PurePosixPath(relative).parts
    if not parts or any(p in ('..', '.') or ':' in p or '\\' in p for p in parts) or pathlib.PurePosixPath(relative).is_absolute():
        raise ValueError('Invalid pinned relative path: ' + relative)
    path = root.joinpath(*parts)
    path.resolve().relative_to(root.resolve())
    for parent in [path, *path.parents]:
        if parent.exists() and (parent.is_symlink() or parent.stat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT):
            raise ValueError(f'Reparse path rejected: {parent}')
        if parent == root:
            break
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--x86', required=True, type=pathlib.Path)
    parser.add_argument('--x64', required=True, type=pathlib.Path)
    parser.add_argument('--root', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    root = args.root.absolute()
    if not (root / 'DeltaNFD.sln').is_file():
        raise ValueError('Repository root does not contain DeltaNFD.sln')
    catalog = json.loads((root / 'src/DeltaNFD.Execution/Execution/Vc14FileRepair14.51.catalog.json').read_text(encoding='utf-8-sig'))
    if len(catalog) != 4 or {(p['Architecture'], p['Role']) for p in catalog} != {(a, r) for a in ['x86', 'x64'] for r in ['Minimum', 'Additional']}:
        raise ValueError('Expected four pinned 14.51 MSI packages')
    originals = {}
    for arch in ['x86', 'x64']:
        path = getattr(args, arch).absolute()
        regular(path)
        expected = {p['OriginalSha256'] for p in catalog if p['Architecture'] == arch}
        if len(expected) != 1 or sha(path) not in expected:
            raise ValueError(f'{arch} bundle does not match the pinned original SHA256')
        originals[arch] = path
    # Both originals are verified before creating staging or changing any build assets.
    wix = shutil.which('wix')
    if not wix:
        raise ValueError('WiX Toolset 5 (wix command) is required for static Burn extraction; no installer will be executed')
    # Burn's cabinet extractor can silently lose files under a long checkout path.
    # Keep its private working path short; catalog verification still precedes all asset writes.
    stage = pathlib.Path(tempfile.gettempdir()) / ('vc1451-' + uuid.uuid4().hex)
    stage.mkdir()  # Inherit the workspace ACL; Python 3.13's mode 0700 can exclude a restricted build token.
    assets = safe_child(root, 'src/DeltaNFD/Assets')
    plan = []
    ns = {'b': 'http://schemas.microsoft.com/wix/2008/Burn'}
    for arch in ['x86', 'x64']:
        payloads = stage / arch / 'payloads'
        ba = stage / arch / 'ba'
        payloads.parent.mkdir(parents=True)
        log = stage / (arch + '.log')
        with log.open('w', encoding='utf-8') as stream:
            subprocess.run([wix, 'burn', 'extract', str(originals[arch]), '-o', str(payloads), '-oba', str(ba)],
                           check=True, stdout=stream, stderr=subprocess.STDOUT)
        manifest = ET.parse(ba / 'manifest.xml')
        if manifest.find('b:Registration', ns).attrib['Version'] != '14.51.36247.0':
            raise ValueError('Unexpected bundle version')
        mapping = {p.attrib['FilePath'].replace('\\', '/'): p.attrib['SourcePath'] for p in manifest.findall('b:Payload', ns)
                   if p.attrib.get('SourcePath', '').startswith('a')}
        for package in [p for p in catalog if p['Architecture'] == arch]:
            if package['Version'] != '14.51.36247' or len(package['Files']) != 2:
                raise ValueError('Unexpected 14.51 package metadata')
            for entry in package['Files']:
                relative = entry['Path']
                if not relative.startswith(arch + '/'):
                    raise ValueError('Wrong catalog architecture')
                token = mapping[relative[len(arch) + 1:]]
                if token not in ['a0', 'a1', 'a2', 'a3', 'a4', 'a5']:
                    raise ValueError('Invalid Burn payload source')
                # WiX 5 resolves payload names when BA extraction is requested in the same call.
                source = safe_child(payloads, 'WixAttachedContainer/' + relative[len(arch) + 1:])
                if not source.exists():
                    source = safe_child(payloads, token)
                regular(source)
                if source.stat().st_size != entry['Bytes'] or sha(source) != entry['Sha256']:
                    raise ValueError('Extracted MSI/CAB does not match pinned catalog: ' + relative)
                plan.append((source, safe_child(assets, 'Vc14FileRepair14.51/' + relative)))
        plan.append((originals[arch], safe_child(assets, f'Vc14Baseline14.51/vc_redist.{arch}.exe')))
    for source, target in plan:
        if target.exists():
            regular(target)
            if sha(target) != sha(source):
                raise ValueError(f'Existing asset differs; preserved without overwrite: {target}')
    written = 0
    for source, target in plan:
        if not target.exists():
            target.parent.mkdir(parents=True, exist_ok=True)
            # Each file is complete before becoming an input; interrupted imports can be rerun.
            temporary = target.with_name(target.name + '.' + uuid.uuid4().hex + '.tmp')
            shutil.copyfile(source, temporary)
            if sha(temporary) != sha(source):
                raise ValueError('Copy verification failed: ' + str(temporary))
            temporary.rename(target)  # Windows refuses an existing destination, including a concurrent import.
            written += 1
        if sha(target) != sha(source):
            raise ValueError('Final asset verification failed: ' + str(target))
    print(json.dumps({'verified': len(plan), 'written': written, 'staging': str(stage), 'installerExecuted': False}))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
