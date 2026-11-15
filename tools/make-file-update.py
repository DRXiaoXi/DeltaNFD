#!/usr/bin/env python3
"""Build immutable file-level updates from a verified publish layout; never upload or activate them."""
import argparse
import gzip
import hashlib
import json
import os
import re
from pathlib import Path
from urllib.parse import urlsplit


def sha(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def absolute(path):
    path = Path(path).resolve()
    return Path("\\\\?\\" + str(path)) if os.name == "nt" else path


def write_json(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def build(layout, output, base_url, installer_manifest, previous=None, github_base=None):
    # The output must not already exist or be within the layout. No mutable release overwrite.
    layout = absolute(layout)
    output = absolute(output)
    if output.exists() or output.is_relative_to(layout):
        raise ValueError("Output must be a new directory outside the publish layout")
    uri = urlsplit(base_url)
    if (uri.scheme != "https" or uri.hostname != "deltaxiaoxi.xin"
            or uri.username or uri.password or uri.query or uri.fragment or uri.port not in (None, 443)):
        raise ValueError("Use an HTTPS official file host URL")
    if github_base:
        github = urlsplit(github_base)
        if (github.scheme != "https" or github.hostname != "github.com" or github.username or github.password
                or github.query or github.fragment or github.port not in (None, 443)):
            raise ValueError("Use the official GitHub release asset base URL")
    release = json.loads(Path(installer_manifest).read_text(encoding="utf-8-sig"))
    if not re.fullmatch(r"\d+(\.\d+){1,3}", release.get("version", "")) or not release.get("installers"):
        raise ValueError("A numeric version and complete installer fallback are required")
    required = {"DeltaNFD.exe", "DeltaNFD.dll", "DeltaNFD.deps.json", "DeltaNFD.runtimeconfig.json", "DeltaNFD.pri",
                "DeltaNFD.Contracts.dll", "DeltaNFD.Shared.dll", "DeltaNFD.UpdateCore.dll", "Tools/DeploymentControl.ps1",
                "Backends/DeltaNFD.FileUpdater.exe", "Backends/DeltaNFD.FileUpdater.dll", "Backends/DeltaNFD.FileUpdater.deps.json", "Backends/DeltaNFD.FileUpdater.runtimeconfig.json"}
    for backend in ("Ace", "Gpu", "Runtime", "SystemMaintenance", "CpuFrame", "Procman"):
        required.update("Backends/DeltaNFD.Backend." + backend + ext for ext in (".exe", ".dll", ".deps.json", ".runtimeconfig.json"))
    paths = []
    for directory, subdirectories, names in os.walk(layout, followlinks=False):
        for name in subdirectories + names:
            entry = Path(directory, name)
            if entry.is_symlink() or getattr(entry, "is_junction", lambda: False)():
                raise ValueError("Linked layout entries are not allowed")
        for name in names:
            path = Path(directory, name)
            relative = path.relative_to(layout).as_posix()
            parts = relative.split("/")
            if (any(p.lower() in {"logs", "backups", "plugins", "userdata"} for p in parts)
                    or name.lower().startswith("unins") or name.lower() in {"settings.json", "update-source.json"}):
                raise ValueError("User data or installer-generated state found in publish layout: " + relative)
            if (len(parts) > 1 and parts[0] not in {"Assets", "Backends", "Views", "Tools", "licenses", "Microsoft.UI.Xaml", "NpuDetect", "Controls", "Themes"}
                    and not re.fullmatch(r"[a-z]{2,3}(?:-[a-zA-Z0-9]{2,8}){0,2}", parts[0])):
                raise ValueError("Unexpected publish directory: " + relative)
            if parts[0] in {"Controls", "Themes"} and (len(parts) != 2 or not name.lower().endswith(".xbf")):
                raise ValueError("UI directories allow compiled XBF resources only: " + relative)
            paths.append((relative, path))
    if not required.issubset({name for name, _ in paths}):
        raise ValueError("Publish layout is missing application, backend or updater components")
    output.mkdir(parents=True)
    objects = output / "objects"
    objects.mkdir()
    files = []
    representations = {}
    for relative, source in sorted(paths):
        digest = sha(source)
        if digest not in representations:
            packed = objects / (digest + ".gz")
            with source.open("rb") as src, packed.open("xb") as dst:
                with gzip.GzipFile(filename="", mode="wb", fileobj=dst, compresslevel=6, mtime=0) as compressor:
                    for block in iter(lambda: src.read(1024 * 1024), b""):
                        compressor.write(block)
            representations[digest] = {"downloadSize": packed.stat().st_size, "downloadSha256": sha(packed)}
        urls = ([github_base.rstrip("/") + "/" + digest + ".gz"] if github_base else [])
        urls.append(base_url.rstrip("/") + "/objects/" + digest + ".gz")
        files.append({"path": relative, "sizeBytes": source.stat().st_size, "sha256": digest,
                      "compression": "gzip", "urls": urls,
                      **representations[digest]})
    removes = []
    if previous:
        old = json.loads(Path(previous).read_text(encoding="utf-8-sig"))
        if old.get("schemaVersion") != 1:
            raise ValueError("Previous inventory has an unsupported schema")
        current = {entry["path"].lower() for entry in files}
        removes = [{"path": entry["path"], "previousSha256": entry["sha256"]}
                   for entry in old["files"] if entry["path"].lower() not in current]
    inventory = {"schemaVersion": 1, "version": release["version"], "displayVersion": release.get("displayVersion", ""),
                 "channel": release.get("channel", ""), "files": files, "remove": removes}
    write_json(output / "files.json", inventory)
    numeric = release["version"].split(".")
    normalized = ".".join((numeric + ["0", "0"])[:3])
    (output / ("DeltaNFD_Files_" + normalized + "_x64.json")).write_bytes((output / "files.json").read_bytes())
    release["fileUpdate"] = {"url": base_url.rstrip("/") + "/files.json", "sizeBytes": (output / "files.json").stat().st_size,
                             "sha256": sha(output / "files.json")}
    write_json(output / "update.json", release)
    write_json(output / "build-result.json", {"files": len(files), "uniqueContents": len(representations),
        "uncompressedBytes": sum(f["sizeBytes"] for f in files), "objectBytes": sum(f["downloadSize"] for f in representations.values()),
        "removedFiles": len(removes), "manifestSha256": release["fileUpdate"]["sha256"]})
    return inventory


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--layout", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--installer-manifest", required=True)
    parser.add_argument("--previous-files")
    parser.add_argument("--github-object-base-url")
    options = parser.parse_args()
    build(options.layout, options.output, options.base_url, options.installer_manifest, options.previous_files, options.github_object_base_url)
    print("File update generated locally. Upload immutable objects/files.json before activating update.json.")
