"""Private anonymous feedback receiver. Production: one Gunicorn worker behind Nginx.

Only ZIPs produced by the client contract are accepted; nothing is extracted or served.
"""
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import threading
import time
import uuid
import zipfile

MAX_ARCHIVE = 4 * 1024 * 1024
MAX_EXPANDED = 2 * 1024 * 1024
MAX_STORAGE = 512 * 1024 * 1024
MAX_REPORTS = 1000
RETENTION_SECONDS = 30 * 86400
AREAS = {"运行库", "内存回收", "后台抑制", "帧格", "ACE相关", "着色器", "NVIDIA设置", "进程管理", "自动更新", "其他"}
LOCK = threading.Lock()
UPLOAD_SLOTS = threading.BoundedSemaphore(2)
MIN_FREE_DISK = 64 * 1024 * 1024


class RateGate:
    """Process-wide bounded token bucket; no per-IP dictionary or unbounded queue."""
    def __init__(self, per_minute, burst, clock=time.monotonic):
        self.rate, self.capacity, self.clock = per_minute / 60.0, burst, clock
        self.tokens, self.updated = float(burst), clock()
        self.lock = threading.Lock()

    def allow(self):
        with self.lock:
            now = self.clock()
            self.tokens = min(self.capacity, self.tokens + max(0, now - self.updated) * self.rate)
            self.updated = now
            if self.tokens < 1:
                return False
            self.tokens -= 1
            return True


UPLOAD_RATE = RateGate(10, 5)
HEALTH_RATE = RateGate(30, 3)


class Rejected(Exception):
    def __init__(self, status, code):
        self.status, self.code = status, code


def validate_archive(data):
    if not 0 < len(data) <= MAX_ARCHIVE:
        raise Rejected(413, "archive_too_large")
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            entries = archive.infolist()
            names = [e.filename for e in entries]
            if not 1 <= len(entries) <= 4 or len(set(names)) != len(names) or names.count("report.json") != 1:
                raise Rejected(400, "invalid_entries")
            if sum(e.file_size for e in entries) > MAX_EXPANDED:
                raise Rejected(413, "expanded_too_large")
            documents = {}
            for entry in entries:
                if entry.filename != "report.json" and not re.fullmatch(r"logs/log-0[1-3]\.txt", entry.filename):
                    raise Rejected(400, "invalid_filename")
                if entry.flag_bits & 1 or entry.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                    raise Rejected(400, "invalid_compression")
                limit = 65536 if entry.filename == "report.json" else 600000
                if entry.file_size > limit or (entry.external_attr >> 16) & 0o170000 == 0o120000:
                    raise Rejected(413, "entry_too_large")
                with archive.open(entry) as source:
                    raw = source.read(limit + 1)
                    if len(raw) > limit or len(raw) != entry.file_size:
                        raise Rejected(413, "entry_too_large")
                text = raw.decode("utf-8")
                if "\x00" in text:
                    raise Rejected(400, "invalid_text")
                documents[entry.filename] = text
            report = json.loads(documents["report.json"])
        if not isinstance(report, dict) or type(report.get("schemaVersion")) is not int or report.get("schemaVersion") != 1:
            raise Rejected(400, "invalid_schema")
        request_id = str(uuid.UUID(report.get("requestId", "")))
        if uuid.UUID(request_id).int == 0:
            raise Rejected(400, "invalid_request_id")
        for key, minimum, maximum in [("title", 2, 100), ("description", 10, 8000), ("contact", 0, 200),
                                      ("appVersion", 1, 150), ("osVersion", 1, 300), ("createdUtc", 1, 80)]:
            value = report.get(key)
            if not isinstance(value, str) or not minimum <= len(value.strip()) <= maximum:
                raise Rejected(400, "invalid_" + key)
        if report.get("area") not in AREAS or type(report.get("logCount")) is not int or report.get("logCount") != len(entries) - 1:
            raise Rejected(400, "invalid_report")
        if len(documents) > 1 and set(documents) != {"report.json"} | {f"logs/log-{i:02}.txt" for i in range(1, len(documents))}:
            raise Rejected(400, "invalid_log_sequence")
        return request_id
    except Rejected:
        raise
    except (ValueError, TypeError, AttributeError, RecursionError, zipfile.BadZipFile, NotImplementedError, RuntimeError, EOFError):
        raise Rejected(400, "invalid_archive") from None


def storage_root():
    root = Path(os.environ.get("FEEDBACK_DATA_DIR", "/var/lib/deltanfd-feedback/reports"))
    if not root.is_absolute() or root.is_symlink():
        raise OSError("Feedback storage must be a private absolute directory")
    root.mkdir(mode=0o700, parents=True, exist_ok=True)
    return root


def prune(root):
    cutoff = time.time() - RETENTION_SECONDS
    for path in root.glob("FB-*.zip"):
        if not path.is_symlink() and path.is_file() and path.stat().st_mtime < cutoff:
            path.unlink()
    for path in root.glob(".pending-*"):
        if not path.is_symlink() and path.is_file() and path.stat().st_mtime < time.time() - 86400:
            path.unlink()


def store(data, request_id):
    receipt = "FB-" + uuid.UUID(request_id).hex.upper()
    with LOCK:
        root = storage_root()
        prune(root)
        target = root / (receipt + ".zip")
        if target.exists():
            if target.is_symlink() or hashlib.sha256(target.read_bytes()).digest() != hashlib.sha256(data).digest():
                raise Rejected(409, "request_conflict")
            return receipt, 200
        files = list(root.glob("FB-*.zip"))
        if len(files) >= MAX_REPORTS or sum(p.stat().st_size for p in files) + len(data) > MAX_STORAGE:
            raise Rejected(503, "storage_full")
        if shutil.disk_usage(root).free < MIN_FREE_DISK + len(data):
            raise Rejected(503, "storage_low")
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(prefix=".pending-", dir=root, delete=False) as output:
                temporary = Path(output.name)
                output.write(data)
                output.flush()
                os.fsync(output.fileno())
            # Atomic and exclusive: never overwrite a prior request with different bytes.
            os.link(temporary, target)
        finally:
            if temporary is not None:
                temporary.unlink(missing_ok=True)
    return receipt, 201


def application(environ, start_response):
    status = 500
    result = {"error": "internal_error"}
    admitted = False
    try:
        path, method = environ.get("PATH_INFO"), environ.get("REQUEST_METHOD")
        if path == "/api/feedback/health" and method == "GET":
            if not HEALTH_RATE.allow():
                raise Rejected(429, "health_rate_limited")
            status, result = 200, {"status": "ok", "schemaVersion": 1}
        elif path != "/api/feedback":
            raise Rejected(404, "not_found")
        elif method != "POST":
            raise Rejected(405, "method_not_allowed")
        else:
            if not UPLOAD_RATE.allow():
                raise Rejected(429, "upload_rate_limited")
            admitted = UPLOAD_SLOTS.acquire(blocking=False)
            if not admitted:
                raise Rejected(503, "receiver_busy")
            if environ.get("CONTENT_TYPE", "").split(";", 1)[0].strip().lower() != "application/zip":
                raise Rejected(415, "zip_required")
            try:
                length = int(environ.get("CONTENT_LENGTH") or "0")
            except ValueError:
                raise Rejected(400, "invalid_length") from None
            if not 0 < length <= MAX_ARCHIVE:
                raise Rejected(413, "archive_too_large")
            data = environ["wsgi.input"].read(length)
            if len(data) != length:
                raise Rejected(400, "incomplete_body")
            request_id = validate_archive(data)
            receipt, status = store(data, request_id)
            result = {"status": "accepted", "receipt": receipt, "requestId": request_id}
    except Rejected as error:
        status, result = error.status, {"error": error.code}
    except OSError:
        status, result = 503, {"error": "storage_unavailable"}
    except Exception:
        # Do not echo private input, filesystem paths or tracebacks to anonymous clients.
        status, result = 500, {"error": "internal_error"}
    finally:
        if admitted:
            UPLOAD_SLOTS.release()
    messages = {200: "OK", 201: "Created", 400: "Bad Request", 404: "Not Found", 405: "Method Not Allowed",
                409: "Conflict", 413: "Content Too Large", 415: "Unsupported Media Type", 429: "Too Many Requests", 500: "Internal Server Error", 503: "Service Unavailable"}
    body = json.dumps(result, ensure_ascii=True).encode("utf-8")
    start_response(f"{status} {messages[status]}", [("Content-Type", "application/json"), ("Content-Length", str(len(body))), ("Cache-Control", "no-store")])
    return [body]


if __name__ == "__main__":
    import sys
    if sys.argv[1:] != ["--prune"]:
        raise SystemExit("Serve with Gunicorn; only --prune is supported here")
    with LOCK:
        prune(storage_root())
