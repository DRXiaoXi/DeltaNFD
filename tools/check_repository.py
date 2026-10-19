"""Check source repository boundaries without building or changing Git's index."""
import argparse
import subprocess
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
LOCAL_ROOTS = {
    "_buildcheck", "artifacts", "dist", "research", ".vs", ".idea", ".zcode",
    ".aws", ".codex", ".agents", ".tmp_logs7", ".tmp_logs8",
}
GENERATED_DIRS = {"bin", "obj", "__pycache__", "node_modules", "testresults"}
PRIVATE_NAMES = {"id_rsa", "id_ed25519", "credentials", "credentials.json"}


def violations(name, size):
    path = PurePosixPath(name)
    parts = [part.lower() for part in path.parts]
    issues = []
    if parts[0] in LOCAL_ROOTS or parts[0].startswith(".tmp_logs"):
        issues.append("local output/evidence directory")
    if any(part in GENERATED_DIRS for part in parts[:-1]):
        issues.append("generated build/cache directory")
    if parts[:2] in (["installer", "output"], ["installer", "backup"], ["installer", "publish"]):
        issues.append("generated installer directory")
    leaf = parts[-1]
    if (leaf.startswith(".env") and leaf not in {".env.example", ".env.sample", ".env.template"}) or leaf in PRIVATE_NAMES:
        issues.append("local credential/configuration filename")
    if path.suffix.lower() in {".pfx", ".p12", ".pem", ".key", ".binlog", ".dmp", ".etl", ".log"}:
        issues.append("private key or local diagnostic artifact")
    if len(parts) == 1 and leaf.endswith(".zip"):
        issues.append("generated root source archive")
    if size >= 100 * 1024 * 1024:
        issues.append("file reaches the GitHub 100 MiB limit")
    return issues


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--include-untracked", action="store_true", help="Also inspect non-ignored new source files")
    args = parser.parse_args()
    command = ["git", "-C", str(ROOT), "ls-files", "-z", "--cached"]
    if args.include_untracked:
        command += ["--others", "--exclude-standard"]
    names = sorted({item.decode("utf-8") for item in subprocess.check_output(command).split(b"\0") if item})
    failures = []
    warnings = []
    total = 0
    for name in names:
        path = ROOT / name
        # Deleted tracked files are valid changes; checkout CI checks the committed tree.
        if not path.exists():
            continue
        if path.is_symlink():
            failures.append(f"{name}: symbolic links require explicit review")
            continue
        if not path.is_file():
            failures.append(f"{name}: nested repository or non-regular tracked entry")
            continue
        size = path.stat().st_size
        total += size
        failures.extend(f"{name}: {issue}" for issue in violations(name, size))
        if 50 * 1024 * 1024 <= size < 100 * 1024 * 1024:
            warnings.append(f"{name}: {size / 1024**2:.1f} MiB; prefer verified external inputs or Release assets")
    for message in warnings:
        print(f"WARN {message}")
    for message in failures:
        print(f"ERROR {message}")
    print(f"Repository check: {len(names)} paths, {total / 1024**2:.1f} MiB, {len(failures)} errors, {len(warnings)} warnings")
    return int(bool(failures))


if __name__ == "__main__":
    raise SystemExit(main())
