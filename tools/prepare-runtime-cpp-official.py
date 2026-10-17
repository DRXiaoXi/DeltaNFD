"""Prepare pinned Microsoft VC++ 2005-2013 originals. Never execute an installer.

Requires 7-Zip; --source accepts an existing YEAR/ARCH/vcredist.exe cache.
Without --source downloads only the fixed Microsoft URLs below. Extracted MSI/CAB
files come from these exact originals, never from an AIO or repack.
"""
import argparse
import hashlib
import json
import pathlib
import shutil
import struct
import subprocess
import tempfile
import urllib.request
import xml.etree.ElementTree as ET

REPO = pathlib.Path(__file__).resolve().parents[1]
DEST = REPO / "src/DeltaNFD/Assets/RuntimeCppOfficial"
CATALOG = REPO / "src/DeltaNFD.Execution/Execution/AlphaRestore/RuntimeCppOfficial.catalog.json"
SOURCES = [
    ("2005", "x64", "8.0.61000", "4487570BD86E2E1AAC29DB2A1D0A91EB63361FCAAC570808EB327CD4E0E2240D", "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x64.EXE"),
    ("2005", "x86", "8.0.61001", "8648C5FC29C44B9112FE52F9A33F80E7FC42D10F3B5B42B2121542A13E44ADFD", "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x86.EXE"),
    ("2008", "x64", "9.0.30729.6161", "C5E273A4A16AB4D5471E91C7477719A2F45DDADB76C7F98A38FA5074A6838654", "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe"),
    ("2008", "x86", "9.0.30729.6161", "8742BCBF24EF328A72D2A27B693CC7071E38D3BB4B9B44DEC42AA3D2C8D61D92", "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x86.exe"),
    ("2010", "x64", "10.0.40219.325", "F3B7A76D84D23F91957AA18456A14B4E90609E4CE8194C5653384ED38DADA6F3", "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe"),
    ("2010", "x86", "10.0.40219.325", "99DCE3C841CC6028560830F7866C9CE2928C98CF3256892EF8E6CF755147B0D8", "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x86.exe"),
    ("2012", "x64", "11.0.61030.0", "681BE3E5BA9FD3DA02C09D7E565ADFA078640ED66A0D58583EFAD2C1E3CC4064", "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe"),
    ("2012", "x86", "11.0.61030.0", "B924AD8062EAF4E70437C8BE50FA612162795FF0839479546CE907FFA8D6E386", "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe"),
    ("2013", "x64", "12.0.40664.0", "A4BBA7701E355AE29C403431F871A537897C363E215CAFE706615E270984F17C", "https://download.visualstudio.microsoft.com/download/pr/10912041/cee5d6bca2ddbcd039da727bf4acb48a/vcredist_x64.exe"),
    ("2013", "x86", "12.0.40664.0", "53B605D1100AB0A88B867447BBF9274B5938125024BA01F5105A9E178A3DCDBD", "https://download.visualstudio.microsoft.com/download/pr/10912113/5da66ddebb0ad32ebd4b922fd82e8e25/vcredist_x86.exe"),
]


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def extract(seven_zip, source, target):
    result = subprocess.run([seven_zip, "x", str(source), "-o" + str(target), "-y"], capture_output=True)
    if result.returncode not in (0, 1):
        raise ValueError("7-Zip extraction failed: " + str(source))


def unpack(seven_zip, exe, work, year):
    if int(year) < 2012:
        target = work / "extract"
        extract(seven_zip, exe, target)
        return [(p.name, p) for p in sorted(target.iterdir()) if p.suffix.lower() in (".msi", ".cab")]
    # Burn embeds an UX CAB and an attached payload CAB. Read bytes, never run /layout.
    data = exe.read_bytes()
    cabinets = []
    offset = 0
    while (offset := data.find(b"MSCF", offset)) >= 0:
        size = struct.unpack_from("<I", data, offset + 8)[0]
        if 36 <= size <= len(data) - offset:
            cabinet = work / (str(offset) + ".cab")
            cabinet.write_bytes(data[offset:offset + size])
            target = work / str(offset)
            extract(seven_zip, cabinet, target)
            cabinets.append(target)
        offset += 4
    manifests = [p / "0" for p in cabinets if (p / "0").exists()]
    if len(manifests) != 1:
        raise ValueError("Burn manifest not uniquely identified")
    root = ET.parse(manifests[0]).getroot()
    mapped = []
    for payload in root.findall("{*}Payload"):
        relative = pathlib.PureWindowsPath(payload.attrib["FilePath"])
        if relative.is_absolute() or ".." in relative.parts or relative.suffix.lower() not in (".msi", ".cab"):
            raise ValueError("Unexpected original payload path")
        source_name = payload.attrib["SourcePath"]
        if pathlib.PureWindowsPath(source_name).name != source_name:
            raise ValueError("Unexpected Burn source")
        found = [p / source_name for p in cabinets if (p / source_name).is_file()]
        if len(found) != 1:
            raise ValueError("Original payload missing")
        if hashlib.sha1(found[0].read_bytes()).hexdigest().upper() != payload.attrib["Hash"].upper():
            raise ValueError("Burn payload differs from pinned original")
        mapped.append(("/".join(relative.parts), found[0]))
    return mapped


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=pathlib.Path)
    parser.add_argument("--seven-zip", default=r"C:\Program Files\7-Zip\7z.exe")
    parser.add_argument("--refresh-catalog", action="store_true", help="Explicit developer-only generation; review diff before building")
    args = parser.parse_args()
    packages = []
    with tempfile.TemporaryDirectory(prefix="legacy-original-", dir=REPO / "_buildcheck") as temporary:
        work = pathlib.Path(temporary)
        prepared = work / "prepared"
        for year, arch, version, expected, url in SOURCES:
            folder = work / (year + "-" + arch)
            folder.mkdir()
            exe = folder / "vcredist.exe"
            if args.source:
                shutil.copyfile(args.source / year / arch / "vcredist.exe", exe)
            else:
                with urllib.request.urlopen(url, timeout=120) as response:
                    if not response.url.startswith(("https://download.microsoft.com/", "https://download.visualstudio.microsoft.com/")):
                        raise ValueError("Unexpected Microsoft download redirect")
                    exe.write_bytes(response.read(32 * 1024 * 1024))
            if digest(exe) != expected:
                raise ValueError("Original EXE hash mismatch: " + year + " " + arch)
            selected = [("vcredist.exe", exe)] + unpack(args.seven_zip, exe, folder, year)
            if len([name for name, _ in selected if name.endswith(".msi")]) != (2 if int(year) >= 2012 else 1):
                raise ValueError("Unexpected original MSI count")
            records = []
            for name, source in sorted(selected):
                relative = pathlib.PurePosixPath(year, arch, name)
                target = prepared.joinpath(*relative.parts)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
                records.append(dict(Path=str(relative), Bytes=source.stat().st_size, Sha256=digest(source)))
            packages.append(dict(Year=year, Arch=arch, Version=version, Url=url, Files=records))
        catalog = (json.dumps(packages, indent=2) + "\n").encode("utf-8")
        if not args.refresh_catalog and (not CATALOG.exists() or json.loads(CATALOG.read_text(encoding="utf-8-sig")) != packages):
            raise ValueError("Extracted files differ from reviewed catalog; no assets copied")
        if args.refresh_catalog:
            CATALOG.write_bytes(catalog)
        for source in prepared.rglob("*"):
            if source.is_file():
                target = DEST / source.relative_to(prepared)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
    print("Prepared 10 pinned Microsoft originals and original MSI/CAB sources. No installer executed.")


if __name__ == "__main__":
    main()
