import io
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
import uuid
import zipfile
from unittest.mock import patch

import feedback_app as app


def package(**changes):
    report = dict(schemaVersion=1, requestId=str(uuid.uuid4()), createdUtc="2026-10-15T00:00:00Z",
                  appVersion="Beta0.9192", osVersion="Synthetic Windows", title="测试问题",
                  area="其他", description="点击测试后出现异常，预期正常完成。", contact="", logCount=0)
    report.update(changes)
    return zipped({"report.json": json.dumps(report, ensure_ascii=False)})


def zipped(entries):
    result = io.BytesIO()
    with zipfile.ZipFile(result, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, text in entries.items():
            archive.writestr(name, text)
    return result.getvalue()


def call(data=b"", method="POST", path="/api/feedback", content_type="application/zip", length=None, remote_addr="127.0.0.1", input_stream=None):
    result = {}
    def respond(status, headers):
        result.update(status=int(status.split()[0]), headers=dict(headers))
    body = b"".join(app.application({"PATH_INFO": path, "REQUEST_METHOD": method, "CONTENT_TYPE": content_type,
                                    "CONTENT_LENGTH": str(len(data)) if length is None else length,
                                    "REMOTE_ADDR": remote_addr, "wsgi.input": input_stream if input_stream is not None else io.BytesIO(data)}, respond))
    return result["status"], json.loads(body)


class FeedbackTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.environment = patch.dict(os.environ, FEEDBACK_DATA_DIR=self.temp.name)
        self.environment.start()
        self.gates = [patch.object(app, "UPLOAD_RATE", app.RateGate(60000, 1000)),
                      patch.object(app, "HEALTH_RATE", app.RateGate(60000, 1000)),
                      patch.object(app, "UPLOAD_SLOTS", __import__("threading").BoundedSemaphore(2))]
        for gate in self.gates: gate.start()

    def tearDown(self):
        for gate in reversed(self.gates): gate.stop()
        self.environment.stop()
        self.temp.cleanup()

    def test_accept_and_idempotent(self):
        data = package()
        status, result = call(data)
        self.assertEqual(status, 201)
        self.assertTrue(result["receipt"].startswith("FB-"))
        self.assertEqual(call(data)[0], 200)
        self.assertEqual(len(list(Path(self.temp.name).glob("*.zip"))), 1)

    def test_same_id_different_bytes_conflict(self):
        request = str(uuid.uuid4())
        self.assertEqual(call(package(requestId=request))[0], 201)
        self.assertEqual(call(package(requestId=request, title="另一问题"))[0], 409)

    def test_bad_schema_and_fields(self):
        for changes in [dict(schemaVersion=2), dict(requestId="../bad"), dict(title=""), dict(description="短"),
                        dict(area="unknown"), dict(logCount=1), dict(contact="a"*201), dict(title=["wrong"])]:
            with self.subTest(changes=changes): self.assertEqual(call(package(**changes))[0], 400)

    def test_paths_binary_and_zipbomb(self):
        self.assertEqual(call(b"not zip")[0], 400)
        self.assertEqual(call(zipped({"../escape": "bad", "report.json": "{}"}))[0], 400)
        self.assertEqual(call(zipped({"report.json": "{}", "logs/log-01.txt": "\x00"}))[0], 400)
        self.assertEqual(call(zipped({"report.json": "{}", "logs/log-01.txt": "x" * (app.MAX_EXPANDED+1)}))[0], 413)
        self.assertFalse((Path(self.temp.name).parent/"escape").exists())

    def test_http_contract(self):
        self.assertEqual(call(method="GET", path="/api/feedback/health")[0], 200)
        self.assertEqual(call(method="GET")[0], 405)
        self.assertEqual(call(path="/api/feedback/reports")[0], 404)
        self.assertEqual(call(package(), content_type="application/json")[0], 415)
        self.assertEqual(call(package(), length=str(app.MAX_ARCHIVE+1))[0], 413)
        self.assertEqual(call(package(), length="invalid")[0], 400)
        self.assertEqual(call(package(), length="9999")[0], 400)

    def test_quota_and_retention(self):
        path = Path(self.temp.name)/("FB-"+"A"*32+".zip")
        path.write_bytes(b"expired")
        os.utime(path, (time.time()-31*86400,)*2)
        self.assertEqual(call(package())[0], 201)
        self.assertFalse(path.exists())
        with patch.object(app, "MAX_REPORTS", 1): self.assertEqual(call(package())[0], 503)
        with patch.object(app, "MAX_STORAGE", 1): self.assertEqual(call(package())[0], 503)

    def test_client_fixture(self):
        fixture = os.environ.get("FEEDBACK_CLIENT_FIXTURE")
        if not fixture: self.skipTest("No optional .NET fixture supplied")
        self.assertEqual(call(Path(fixture).read_bytes())[0], 201)

    def test_rate_bucket_refill(self):
        now = [0.0]
        gate = app.RateGate(10, 2, clock=lambda: now[0])
        self.assertTrue(gate.allow())
        self.assertTrue(gate.allow())
        self.assertFalse(gate.allow())
        now[0] = 5.9
        self.assertFalse(gate.allow())
        now[0] = 6.0
        self.assertTrue(gate.allow())
        self.assertFalse(gate.allow())
        now[0] = 600.0
        self.assertTrue(gate.allow())
        self.assertTrue(gate.allow())
        self.assertFalse(gate.allow())

    def test_global_rate_across_ips_before_validation(self):
        with patch.object(app, "UPLOAD_RATE", app.RateGate(10, 2)):
            self.assertEqual(call(package(), remote_addr="192.0.2.1")[0], 201)
            self.assertEqual(call(package(), remote_addr="192.0.2.2")[0], 201)
            with patch.object(app, "validate_archive") as validate:
                unread = unittest.mock.Mock()
                unread.read.side_effect = AssertionError("Rejected requests must not read their body")
                self.assertEqual(call(b"untrusted", remote_addr="192.0.2.3", input_stream=unread)[0], 429)
                unread.read.assert_not_called()
                validate.assert_not_called()

    def test_health_rate_independent_from_upload(self):
        with patch.object(app, "HEALTH_RATE", app.RateGate(30, 1)):
            self.assertEqual(call(method="GET", path="/api/feedback/health")[0], 200)
            self.assertEqual(call(method="GET", path="/api/feedback/health", remote_addr="192.0.2.2")[0], 429)
            self.assertEqual(call(package())[0], 201)

    def test_concurrency_rejects_without_waiting(self):
        app.UPLOAD_SLOTS.acquire()
        app.UPLOAD_SLOTS.acquire()
        try:
            with patch.object(app, "validate_archive") as validate:
                unread = unittest.mock.Mock()
                unread.read.side_effect = AssertionError("Busy receiver must not read another body")
                self.assertEqual(call(package(), input_stream=unread)[0], 503)
                unread.read.assert_not_called()
                validate.assert_not_called()
        finally:
            app.UPLOAD_SLOTS.release()
            app.UPLOAD_SLOTS.release()
        self.assertEqual(call(package())[0], 201)

    def test_slot_released_after_failures(self):
        self.assertEqual(call(b"broken zip")[0], 400)
        with patch.object(app, "store", side_effect=OSError("synthetic storage failure")):
            self.assertEqual(call(package())[0], 503)
        self.assertTrue(app.UPLOAD_SLOTS.acquire(blocking=False))
        self.assertTrue(app.UPLOAD_SLOTS.acquire(blocking=False))
        self.assertFalse(app.UPLOAD_SLOTS.acquire(blocking=False))
        app.UPLOAD_SLOTS.release()
        app.UPLOAD_SLOTS.release()

    def test_disk_reserve_and_idempotent_receipt(self):
        from types import SimpleNamespace
        data = package()
        self.assertEqual(call(data)[0], 201)
        with patch.object(app.shutil, "disk_usage", return_value=SimpleNamespace(free=app.MIN_FREE_DISK)):
            self.assertEqual(call(package())[0], 503)
            self.assertEqual(call(data)[0], 200)
        self.assertEqual(len(list(Path(self.temp.name).glob("*.zip"))), 1)

    def test_actual_http_roundtrip(self):
        import threading
        import urllib.request
        from wsgiref.simple_server import make_server, WSGIRequestHandler
        class Quiet(WSGIRequestHandler):
            def log_message(self, *args): pass
        server = make_server("127.0.0.1", 0, app.application, handler_class=Quiet)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            fixture = os.environ.get("FEEDBACK_CLIENT_FIXTURE")
            data = Path(fixture).read_bytes() if fixture else package()
            url = f"http://127.0.0.1:{server.server_port}/api/feedback"
            for expected in (201, 200):
                request = urllib.request.Request(url, data=data, headers={"Content-Type": "application/zip"})
                with urllib.request.urlopen(request, timeout=5) as response:
                    self.assertEqual(response.status, expected)
                    result = json.loads(response.read())
                    self.assertEqual(result["status"], "accepted")
            self.assertEqual(len(list(Path(self.temp.name).glob("*.zip"))), 1)
        finally:
            server.shutdown()
            thread.join(timeout=5)
            server.server_close()


if __name__ == "__main__": unittest.main(verbosity=2)
