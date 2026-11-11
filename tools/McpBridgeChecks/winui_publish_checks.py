"""Check unpackaged WinUI resources against the source and this build's output."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def check_layout(source, product, build, makepri):
    expected = sorted(
        path.relative_to(source).with_suffix('.xbf').as_posix()
        for path in source.rglob('*.xaml')
        if not {'bin', 'obj'}.intersection(path.relative_to(source).parts)
    )
    if not expected or 'MainWindow.xbf' not in expected or 'Views/SettingsPage.xbf' not in expected:
        raise ValueError('Source does not contain the application UI resources')
    resources = {}
    for name in expected + ['DeltaNFD.pri']:
        delivered, built = product / name, build / name
        if not delivered.is_file() or delivered.stat().st_size == 0:
            raise ValueError(f'Missing or empty published UI resource: {name}')
        if not built.is_file() or sha(delivered) != sha(built):
            raise ValueError(f'Published UI resource differs from this build: {name}')
        with delivered.open('rb') as stream:
            signature = stream.read(4 if name.endswith('.xbf') else 8)
        if name.endswith('.xbf') and signature != b'XBF\0':
            raise ValueError(f'Invalid XBF header: {name}')
        if name.endswith('.pri') and not signature.startswith(b'mrm_pri'):
            raise ValueError(f'Invalid PRI header: {name}')
        resources[name] = {'bytes': delivered.stat().st_size, 'sha256': sha(delivered)}
    with tempfile.TemporaryDirectory(prefix='deltanfd-winui-pri-') as temporary:
        dump = Path(temporary) / 'resources.xml'
        completed = subprocess.run(
            [str(makepri.resolve()), 'dump', '/if', str((product / 'DeltaNFD.pri').resolve()),
             '/of', str(dump), '/dt', 'detailed'], capture_output=True, timeout=60
        )
        if completed.returncode != 0 or not dump.is_file():
            raise ValueError(f'makepri failed to parse the delivered PRI: {completed.returncode}')
        tree = ET.parse(dump)
        indexed = {node.attrib.get('uri', '').split('/Files/', 1)[-1].replace('\\', '/')
                   for node in tree.iter('NamedResource') if '/Files/' in node.attrib.get('uri', '')}
        missing = set(expected) - indexed
        if missing:
            raise ValueError(f'PRI does not index published UI resources: {sorted(missing)}')
    return {'passed': True, 'xbfCount': len(expected), 'resourceCount': len(resources),
            'sameBuildSha256': True, 'priParsedAndAllXbfIndexed': True, 'resources': resources}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--product', type=Path, required=True)
    parser.add_argument('--build', type=Path, required=True)
    parser.add_argument('--makepri', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = check_layout(args.source, args.product, args.build, args.makepri)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: value for key, value in result.items() if key != 'resources'}))


if __name__ == '__main__':
    main()
