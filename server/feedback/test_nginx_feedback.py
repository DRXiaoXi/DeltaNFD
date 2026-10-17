"""Optional Linux-only real Nginx checks, isolated localhost ports and temp config.
Never targets the public website or reloads the system Nginx.
"""
import http.client
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import unittest
from wsgiref.simple_server import make_server, WSGIRequestHandler


@unittest.skipUnless(os.name == "posix" and shutil.which("nginx"), "Requires Linux and installed Nginx")
class NginxLimitsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        def backend(environ, start):
            size = int(environ.get("CONTENT_LENGTH") or 0)
            environ["wsgi.input"].read(size)
            start("200 OK", [("Content-Length", "2")])
            return [b"ok"]
        class Quiet(WSGIRequestHandler):
            def log_message(self, *args): pass
        self.backend = make_server("127.0.0.1", 0, backend, handler_class=Quiet)
        self.thread = threading.Thread(target=self.backend.serve_forever, daemon=True)
        self.thread.start()
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            self.port = probe.getsockname()[1]
        (self.root/"body").mkdir()
        self.marker = self.root/"body/.ready"
        self.marker.touch()
        source = Path(__file__).parent
        http = (source/"nginx-feedback-http.conf").read_text()
        locations = (source/"nginx-feedback-server.inc").read_text().replace(
            "/var/lib/nginx/deltanfd_feedback_body", str(self.root/"body")).replace(
            "/var/log/nginx/deltanfd-feedback-error.log", str(self.root/"errors.log")).replace(
            "127.0.0.1:18792", f"127.0.0.1:{self.backend.server_port}")
        # This isolated root-run test must retain access to its private temp dir.
        worker = "user root;\n" if os.geteuid() == 0 else ""
        config = worker + f"""daemon off; master_process off;
pid {self.root}/nginx.pid;
error_log {self.root}/main-errors.log crit;
events {{ worker_connections 64; }}
http {{ {http}
server {{ listen 127.0.0.1:{self.port}; server_name deltaxiaoxi.xin;
{locations}
location = /updates/probe {{ return 200 'unchanged'; }}
}} }}"""
        (self.root/"nginx.conf").write_text(config)
        checked = subprocess.run([shutil.which("nginx"), "-p", str(self.root)+"/", "-t", "-c", str(self.root/"nginx.conf")], capture_output=True)
        self.assertEqual(checked.returncode, 0, checked.stderr.decode(errors="replace"))
        self.process = subprocess.Popen([shutil.which("nginx"), "-p", str(self.root)+"/", "-c", str(self.root/"nginx.conf")], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(100):
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=.1): break
            except OSError: time.sleep(.02)
        else: self.fail("Isolated Nginx did not start")

    def tearDown(self):
        if hasattr(self, "process"):
            self.process.terminate()
            self.process.wait(timeout=5)
        self.backend.shutdown()
        self.thread.join(timeout=5)
        self.backend.server_close()
        self.temp.cleanup()

    def request(self, path="/api/feedback", source="127.0.0.2", method="POST", body=b"x"):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=3, source_address=(source, 0))
        try:
            connection.request(method, path, body=body, headers={"Host": "deltaxiaoxi.xin", "Content-Type": "application/zip"})
            response = connection.getresponse()
            response.read()
            return response.status
        finally: connection.close()

    def test_global_rate_multiple_ips(self):
        statuses = [self.request(source=f"127.0.0.{i}") for i in range(2, 9)]
        self.assertEqual(statuses[:5], [200]*5)
        self.assertEqual(statuses[5:], [429]*2)
        self.assertEqual(self.request("/updates/probe", method="GET", body=None), 200)

    def test_global_and_per_ip_concurrency(self):
        held = []
        try:
            for i in range(2, 6):
                connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=3, source_address=(f"127.0.0.{i}", 0))
                connection.putrequest("POST", "/api/feedback")
                connection.putheader("Content-Type", "application/zip")
                connection.putheader("Content-Length", "1")
                connection.endheaders()  # Hold body reception, not an upstream worker.
                held.append(connection)
                time.sleep(.05)
                if i == 2: self.assertEqual(self.request(source="127.0.0.2"), 429)
            self.assertEqual(self.request(source="127.0.0.9"), 429)
            self.assertEqual(self.request("/api/feedback/health", source="127.0.0.10", method="GET", body=None), 429)
            self.assertEqual(self.request("/updates/probe", method="GET", body=None), 200)
        finally:
            for connection in held: connection.close()

    def test_health_rate(self):
        results = [self.request("/api/feedback/health", method="GET", body=None) for _ in range(3)]
        self.assertEqual(results, [200, 200, 429])

    def test_missing_bounded_storage_rejects_before_body(self):
        self.marker.unlink()
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=3)
        try:
            connection.putrequest("POST", "/api/feedback")
            connection.putheader("Content-Length", str(4*1024*1024))
            connection.endheaders()  # Do not send any body.
            self.assertEqual(connection.getresponse().status, 503)
        finally: connection.close()


if __name__ == "__main__": unittest.main(verbosity=2)
