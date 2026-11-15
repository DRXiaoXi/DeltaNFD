"""Keep vendor driver bytes outside public source; never install or execute them."""
import argparse
import gzip
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
KNOWN_DPC_HASHES = {
    "00a7c7b88877bf59dbaa70de734fcd3f287f1eb92caff4571a4c8e67a5dc0aca",  # original EXE
    "636519d3293feb6779f089865c8c59a0763e720afefbad46fdb164fc06b9127e",  # x64 SYS
    "6871ebb5a285f7e9d34c89eae89414732dc29cd739622243a1770d09227b094c",  # x86 SYS
}
MAX_BYTES = 100 * 1024 * 1024
MAX_ARCHIVE_ENTRIES = 4096
PRIVATE_DIRS = {"private-driver-payloads", "_private-driver-payloads"}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def safe_name(name):
    p = PurePosixPath(name)
    if not name or p.is_absolute() or "\\" in name or ":" in name or "\0" in name or any(x in {"", ".", ".."} for x in name.split("/")):
        raise ValueError("Invalid source/archive path")
    return p


def private_name(name):
    p = safe_name(name)
    parts = tuple(x.lower() for x in p.parts)
    leaf = parts[-1]
    return (any(x in PRIVATE_DIRS for x in parts)
            or any(parts[i:i+2] in {("drivers", "dpctimer"), ("dpc", "payload"), ("dpctimer", "package")} for i in range(len(parts)-1))
            or leaf in {"dpc.sys", "dpclat.sys", "dpclat_x64.sys", "dpclat_x86.sys"}
            or (leaf.endswith((".sys", ".exe")) and leaf.startswith(("dpclat", "dpclatencychecker"))))


def is_dpc_binary(data):
    if sha(data) in KNOWN_DPC_HASHES:
        return True
    # Catch a modified/repacked vendor PE as well as exact hashes. Text-only
    # source mentioning these names is permitted. This is not a general AV scan.
    return data.startswith(b"MZ") and any(marker in data for marker in (
        b"dpclat_static_device", "dpclat_static_device".encode("utf-16le"),
        b"Thesycon DpcLatency Driver", "Thesycon DpcLatency Driver".encode("utf-16le")))


def public_issues(name, data, depth=0, budget=None):
    """Inspect bytes in memory, including bounded ZIP/gzip members; no extraction."""
    budget = [MAX_BYTES, MAX_ARCHIVE_ENTRIES] if budget is None else budget
    issues = []
    if private_name(name):
        issues.append("private vendor-driver path/name in public source")
    budget[0] -= len(data)
    budget[1] -= 1
    if budget[0] < 0 or budget[1] < 0:
        raise ValueError("Source container exceeds inspection budget")
    if is_dpc_binary(data):
        issues.append("DPC vendor binary in public source, including renamed content")
    is_zip = data.startswith((b"PK\x03\x04", b"PK\x05\x06", b"PK\x07\x08"))
    is_gzip = data.startswith(b"\x1f\x8b")
    if not (is_zip or is_gzip):
        return issues
    if depth >= 3:
        raise ValueError("Nested source container exceeds depth limit")
    if is_gzip:
        with gzip.GzipFile(fileobj=io.BytesIO(data)) as f:
            decoded = f.read(budget[0] + 1)
        return issues + public_issues(name + ".decoded", decoded, depth + 1, budget)
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        if len(z.infolist()) > MAX_ARCHIVE_ENTRIES:
            raise ValueError("Source ZIP exceeds member limit")
        seen = set()
        for item in z.infolist():
            inner = item.filename.rstrip("/")
            safe_name(inner)
            if inner.casefold() in seen or item.flag_bits & 1 or (item.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError("Duplicate, encrypted or symlink source ZIP member")
            seen.add(inner.casefold())
            if item.is_dir():
                continue
            if item.file_size > budget[0] or len(seen) > MAX_ARCHIVE_ENTRIES:
                raise ValueError("Source ZIP exceeds inspection budget")
            with z.open(item) as f:
                decoded = f.read(budget[0] + 1)
            issues.extend(f"{inner}: {issue}" for issue in public_issues(inner, decoded, depth + 1, budget))
    return issues


def index_files(root):
    root = root.resolve()
    top = Path(subprocess.check_output(["git", "-C", str(root), "rev-parse", "--show-toplevel"]).decode("utf-8").strip()).resolve()
    if top != root:
        raise ValueError("Expected the exact source repository root, not a parent repository")
    raw = subprocess.check_output(["git", "-C", str(root), "ls-files", "--stage", "-z"])
    items = []
    for entry in raw.split(b"\0"):
        if not entry:
            continue
        info, name = entry.split(b"\t", 1)
        mode, oid, stage = info.split()
        if stage != b"0" or mode not in {b"100644", b"100755"}:
            raise ValueError("Conflicted, symlink or submodule index entry")
        name = name.decode("utf-8")
        safe_name(name)
        items.append((name, oid.decode("ascii")))
    if not items:
        raise ValueError("Empty source index")
    # Canonical index blobs, not checkout bytes: an unstaged edit cannot hide
    # a binary already staged for the next GitHub commit.
    request = "".join(oid + "\n" for _, oid in items).encode("ascii")
    sizes = subprocess.check_output(["git", "-C", str(root), "cat-file", "--batch-check"], input=request).splitlines()
    if len(sizes) != len(items) or len(items) > 20000:
        raise ValueError("Invalid or oversized source inventory")
    total = 0
    for row in sizes:
        fields = row.split()
        if len(fields) != 3 or fields[1] != b"blob":
            raise ValueError("Expected only regular Git blobs")
        total += int(fields[2])
        if total > MAX_BYTES:
            raise ValueError("Source index exceeds inspection budget")
    raw = subprocess.check_output(["git", "-C", str(root), "cat-file", "--batch"], input=request)
    offset, total, files = 0, 0, {}
    for name, _ in items:
        end = raw.index(b"\n", offset)
        _, kind, size = raw[offset:end].split()
        size = int(size)
        total += size
        if kind != b"blob" or total > MAX_BYTES:
            raise ValueError("Source index exceeds inspection budget or contains non-blob data")
        start = end + 1
        files[name] = raw[start:start+size]
        if len(files[name]) != size or raw[start+size:start+size+1] != b"\n":
            raise ValueError("Incomplete Git object read")
        offset = start + size + 1
    if offset != len(raw):
        raise ValueError("Unexpected trailing Git object data")
    return files


def audit_index(root):
    files = index_files(root)
    for name, data in files.items():
        issues = public_issues(name, data)
        if issues:
            raise ValueError(name + ": " + "; ".join(issues))
    return files


def source_archive(root, output):
    files = audit_index(root)
    # Regenerate rather than include a stale inventory from an earlier release.
    files.pop("SOURCE_FILES_SHA256.json", None)
    manifest = {name: sha(data) for name, data in sorted(files.items())}
    files["SOURCE_FILES_SHA256.json"] = json.dumps(manifest, indent=2, ensure_ascii=True).encode("utf-8") + b"\n"
    no_links(output.absolute())
    output = output.resolve()
    if output.is_relative_to(root.resolve()) or output.exists():
        raise ValueError("Source archive must be new and outside the source repository")
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "x", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, data in sorted(files.items()):
            z.writestr("DeltaNFD-Source/" + name, data)
    with zipfile.ZipFile(output) as z:
        for name, data in files.items():
            if z.read("DeltaNFD-Source/" + name) != data:
                raise ValueError("Source archive readback mismatch")
    return {"files": len(files), "sha256": sha(output.read_bytes())}


def catalog(root):
    value = json.loads((root / "installer/driver-payloads.json").read_text(encoding="utf-8-sig"))
    if value.get("schema") != 1:
        raise ValueError("Unknown driver payload catalog")
    return value["payloads"]["dpc-latency-checker-x64"]


def no_links(path):
    for item in (path, *path.parents):
        if item.exists() and (item.is_symlink() or getattr(item.stat(), "st_file_attributes", 0) & 0x400):
            raise ValueError("Private payload path may not cross reparse points")


def verify_private(root, source):
    source = source.absolute()
    no_links(source)
    if source.resolve().is_relative_to(root.resolve()):
        raise ValueError("Vendor binary input must be outside the public source repository")
    spec = catalog(root)
    if source.stat().st_size != spec["bytes"]:
        raise ValueError("Private driver size mismatch")
    data = source.read_bytes()
    if len(data) != spec["bytes"] or sha(data) != spec["sha256"]:
        raise ValueError("Private driver SHA256 mismatch")
    return spec, data


def distribution_gate(spec):
    rights = spec.get("redistribution", {})
    security = spec.get("securityReview", {})
    if rights.get("status") != "authorized" or not rights.get("authorizationEvidenceSha256"):
        raise ValueError("DPC redistribution is not authorized; no driver was staged")
    if security.get("status") != "approved":
        raise ValueError("DPC production security review is not approved; no driver was staged")


def stage_private(root, source, layout, authorization):
    spec = catalog(root)
    distribution_gate(spec)  # No CLI bypass and no copy before license/security review.
    if authorization is None:
        raise ValueError("Missing private authorization evidence")
    no_links(authorization.absolute())
    if authorization.resolve().is_relative_to(root.resolve()) or sha(authorization.read_bytes()) != spec["redistribution"]["authorizationEvidenceSha256"]:
        raise ValueError("Private authorization evidence mismatch")
    spec, data = verify_private(root, source)
    layout = layout.absolute()
    if layout.resolve().is_relative_to(root.resolve()):
        raise ValueError("Private installer layout must be outside the public source repository")
    relative = safe_name(spec["installRelativePath"])
    target = layout.joinpath(*relative.parts)
    no_links(target)
    if target.exists():
        raise ValueError("Refusing to replace an existing staged driver")
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("xb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    if target.read_bytes() != data:
        raise ValueError("Staged driver readback mismatch")
    return {"relativePath": str(relative), "sha256": spec["sha256"]}


def audit_layout(root, layout):
    spec = catalog(root)
    found = []
    for path in layout.rglob("*"):
        if not path.is_file():
            continue
        name = path.relative_to(layout).as_posix()
        data = path.read_bytes()
        # Public source can contain text references; this checks actual bytes.
        if public_issues(name, data):
            distribution_gate(spec)
            if name != spec["installRelativePath"] or len(data) != spec["bytes"] or sha(data) != spec["sha256"]:
                raise ValueError("Unexpected DPC binary/location in installer layout")
            found.append(name)
    return {"driverFiles": found, "redistribution": spec["redistribution"]["status"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["audit-index", "source-archive", "verify-private", "stage-private", "audit-layout"])
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--layout", type=Path)
    parser.add_argument("--authorization-evidence", type=Path)
    args = parser.parse_args()
    try:
        if args.action == "audit-index":
            result = {"publicFilesChecked": len(audit_index(args.root))}
        elif args.action == "source-archive":
            if args.output is None:
                raise ValueError("--output is required")
            result = source_archive(args.root, args.output)
        elif args.action == "verify-private":
            if args.input is None:
                raise ValueError("--input is required")
            spec, _ = verify_private(args.root, args.input)
            result = {"sha256": spec["sha256"], "bytes": spec["bytes"], "redistribution": spec["redistribution"]["status"], "signatureOrAntiCheatAcceptance": False}
        elif args.action == "stage-private":
            if args.input is None or args.layout is None:
                raise ValueError("--input and --layout are required")
            result = stage_private(args.root, args.input, args.layout, args.authorization_evidence)
        else:
            if args.layout is None:
                raise ValueError("--layout is required")
            result = audit_layout(args.root, args.layout)
        print(json.dumps(result, ensure_ascii=True))
        return 0
    except (OSError, ValueError, KeyError, zipfile.BadZipFile, EOFError, subprocess.CalledProcessError) as ex:
        print("Driver payload boundary: " + str(ex))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
