"""Hydrate ignored repair binaries from a verified Alpha0.916 payload; never run installers.

Usage: python tools/prepare-runtime-highfps.py <Alpha0.916 Assets/VCRedistRepair>
The modified Installer.cmd and filtered manifest are tracked with the new source.
"""
import csv
import hashlib
import pathlib
import shutil
import sys

ORIGINAL_MANIFEST = "6C3CDC91281CFAF0A5BB23F511A5E4914BA7A6520CC84E73286693AE69DE7D0A"
FOLDERS = {"ucrt", "vbc", "vstor"}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def main():
    source = pathlib.Path(sys.argv[1]).resolve(strict=True)
    destination = pathlib.Path(__file__).resolve().parents[1] / "src/DeltaNFD/Assets/RuntimeHighFps"
    if digest(source / "payload_manifest_sha256.csv") != ORIGINAL_MANIFEST:
        raise ValueError("Source is not the pinned Alpha0.916 payload")
    if any((destination / year).exists() for year in ("2022", "2026")):
        raise ValueError("Remove mixed v14 resources from the build input before hydration")
    expected = list(csv.DictReader((destination / "payload_manifest_sha256.csv").open(encoding="utf-8-sig")))
    original = {row["relative_path"].replace("\\", "/"): row for row in csv.DictReader(
        (source / "payload_manifest_sha256.csv").open(encoding="utf-8-sig"))}
    if len(expected) != 375:
        raise ValueError("Filtered manifest size changed")
    selected = []
    for row in expected:
        relative = pathlib.PurePosixPath(row["relative_path"])
        if relative.name == "Installer.cmd" and len(relative.parts) == 1:
            continue  # Keep the new, filtered script; never copy the original installer back.
        if relative.is_absolute() or ".." in relative.parts or relative.parts[0] not in FOLDERS:
            raise ValueError("Unexpected filtered path: " + str(relative))
        if original.get(str(relative)) != row:
            raise ValueError("Filtered binary differs from the pinned original: " + str(relative))
        path = source.joinpath(*relative.parts)
        if path.stat().st_size != int(row["size_bytes"]) or digest(path) != row["sha256"].upper():
            raise ValueError("Source binary mismatch: " + str(relative))
        selected.append((path, destination.joinpath(*relative.parts)))
    # All source identities are verified before copying any binary.
    for path, target in selected:
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists() or digest(target) != digest(path):
            shutil.copyfile(path, target)
    for row in expected:
        path = destination.joinpath(*pathlib.PurePosixPath(row["relative_path"]).parts)
        if path.stat().st_size != int(row["size_bytes"]) or digest(path) != row["sha256"].upper():
            raise ValueError("Final filtered payload mismatch: " + str(path))
    print(f"Verified {len(expected)} payload files; C++2026/2022 AIO binaries excluded. No system changes.")


if __name__ == "__main__":
    main()
