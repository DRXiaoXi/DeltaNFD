"""Prepare fixed Beta0.92 build inputs without running an installer.

Only missing binary inputs are restored; source-controlled files are never replaced.
The immutable file manifest, transfer objects and decoded files are all hashed.
"""
import argparse
import concurrent.futures
import gzip
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import tempfile
import urllib.request

MANIFEST_SHA256 = "e4d70645be7e0e67f195da094eeb2dddc8b3d1469b1845642be5642479d4f029"
MANIFEST_URLS = (
    "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9200/DeltaNFD_Files_0.9200.0_x64.json",
    "https://deltaxiaoxi.xin/updates/packages/0.9200/files.json",
)
DIRECTORIES = {"RuntimeCppOfficial", "RuntimeHighFps", "Vc14Baseline14.42", "Vc14FileRepair14.42", "Vc14OwnershipOriginals"}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def download(urls, limit, expected):
    failures = []
    for url in urls:
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "DeltaNFD-source-assets/1"})
            with urllib.request.urlopen(request, timeout=60) as response:
                data = response.read(limit + 1)
            if len(data) > limit or digest(data) != expected:
                raise ValueError("download size/hash mismatch")
            return data
        except (OSError, ValueError) as error:
            failures.append(f"{url}: {error}")
    raise RuntimeError("No verified source is available: " + "; ".join(failures))


def prepare(root, cache, manifest_bytes, expected_sha=MANIFEST_SHA256, expected_version="0.9200", directories=DIRECTORIES, include_background=True):
    # VSTO resource paths exceed MAX_PATH in deep Windows checkouts.
    if os.name == "nt":
        root = Path(os.path.abspath(root))
        cache = Path(os.path.abspath(cache))
        root = root if str(root).startswith("\\\\?\\") else Path("\\\\?\\" + str(root))
        cache = cache if str(cache).startswith("\\\\?\\") else Path("\\\\?\\" + str(cache))
    if digest(manifest_bytes) != expected_sha:
        raise ValueError("Fixed manifest SHA256 mismatch")
    manifest = json.loads(manifest_bytes)
    if manifest["version"] != expected_version:
        raise ValueError("Unexpected resource baseline")
    selected = []
    for item in manifest["files"]:
        path = PurePosixPath(item["path"])
        if path.parts[:1] != ("Assets",):
            continue
        if not (include_background and item["path"] == "Assets/background.png") and (len(path.parts) < 3 or path.parts[1] not in directories):
            continue
        if path.is_absolute() or any(p in ("..", ".") or ":" in p or "\\" in p for p in path.parts):
            raise ValueError("Invalid resource path")
        if item["compression"] != "gzip" or not 0 <= item["sizeBytes"] <= 128 * 1024 * 1024:
            raise ValueError("Unexpected resource size/encoding")
        selected.append(item)
    if not selected or sum(i["sizeBytes"] for i in selected) > 512 * 1024 * 1024:
        raise ValueError("Unexpected resource inventory")
    cache.mkdir(parents=True, exist_ok=True)

    def restore(item):
        target = root / "src/DeltaNFD" / item["path"]
        if target.exists():
            if target.stat().st_size != item["sizeBytes"] or digest(target.read_bytes()) != item["sha256"]:
                raise ValueError(f"Existing input differs; preserving it: {item['path']}")
            return False
        cached = cache / (item["sha256"] + ".gz")
        if cached.exists():
            data = cached.read_bytes()
            if len(data) != item["downloadSize"] or digest(data) != item["downloadSha256"]:
                raise ValueError(f"Cached object mismatch: {cached.name}")
        else:
            data = download(item["urls"], item["downloadSize"], item["downloadSha256"])
            # Different paths can share an object. Never expose a partial cache file.
            with tempfile.NamedTemporaryFile(dir=cache, delete=False, suffix=".tmp") as temporary:
                temporary.write(data)
                cache_temporary = Path(temporary.name)
            try:
                os.replace(cache_temporary, cached)
            finally:
                cache_temporary.unlink(missing_ok=True)
        with gzip.GzipFile(fileobj=io.BytesIO(data)) as decoder:
            decoded = decoder.read(item["sizeBytes"] + 1)
        if len(decoded) != item["sizeBytes"] or digest(decoded) != item["sha256"]:
            raise ValueError(f"Decoded resource mismatch: {item['path']}")
        target.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(dir=target.parent, delete=False, suffix=".tmp") as temporary:
            temporary.write(decoded)
            temporary_path = Path(temporary.name)
        try:
            if target.exists():
                raise ValueError(f"Input appeared during preparation: {item['path']}")
            os.replace(temporary_path, target)
        finally:
            temporary_path.unlink(missing_ok=True)
        return True

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        restored = sum(pool.map(restore, selected))
    return {"verifiedInputs": len(selected), "restoredInputs": restored, "baseline": expected_version}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache-dir", type=Path, help="Verified compressed-object cache (default: _buildcheck/source-assets-cache)")
    parser.add_argument("--manifest", type=Path, help="Optional local copy; the same fixed manifest SHA256 is required")
    parser.add_argument("--extra-manifest", type=Path, help="Optional immutable 0.9299 inventory for official 14.44 inputs")
    options = parser.parse_args()
    repository = Path(__file__).resolve().parent.parent
    raw = options.manifest.read_bytes() if options.manifest else download(MANIFEST_URLS, 1024 * 1024, MANIFEST_SHA256)
    cache = options.cache_dir or repository / "_buildcheck/source-assets-cache"
    original = prepare(repository, cache, raw)
    extra_sha = "c90973ae81b01bd33288ccffe915215238bc960519f420410eae867bb627199d"
    extra_urls = ("https://deltaxiaoxi.xin/updates/packages/0.9299/files.json",
        "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9299/DeltaNFD_Files_0.9299.0_x64.json")
    extra_raw = options.extra_manifest.read_bytes() if options.extra_manifest else download(extra_urls, 2 * 1024 * 1024, extra_sha)
    extra = prepare(repository, cache, extra_raw, extra_sha, "0.9299", {"Vc14Baseline14.44", "Vc14FileRepair14.44"}, False)
    if extra["verifiedInputs"] != 10: raise ValueError("Expected exactly ten fixed official 14.44 inputs")
    print(json.dumps({"original": original, "current144": extra}))
