"""No driver execution: synthetic bytes, owned throwaway Git indexes and files."""
import gzip
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import driver_payloads as d

FAKE_PE = b"MZ" + b"\0" * 64 + "Thesycon DpcLatency Driver".encode("utf-16le")


def packed(name, data):
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(name, data)
    return out.getvalue()


class PublicBytesTests(unittest.TestCase):
    def test_source_and_metadata_are_allowed(self):
        for name, data in [("Adapter.cs", b'var device = "dpclat_static_device";'),
                           ("installer/driver-payloads.json", json.dumps(list(d.KNOWN_DPC_HASHES)).encode()),
                           ("native/DpcTimerLab/Core.cs", b"source code"),
                           ("archive.zip", packed("LICENSE", b"text"))]:
            with self.subTest(name=name):
                self.assertEqual(d.public_issues(name, data), [])

    def test_private_names_are_rejected(self):
        for name in ["private-driver-payloads/permission.pdf", "nested/_private-driver-payloads/input.bin",
                     "Drivers/DpcTimer/driver.bin", "native/DpcTimer/package/a.bin", "DPC.SYS",
                     "DPCLAT_X64.SYS", "DPCLatencyChecker_V1.4.exe", "tools/dpclat.exe"]:
            with self.subTest(name=name):
                self.assertTrue(d.public_issues(name, b"synthetic"))

    def test_renamed_and_nested_binary_is_rejected(self):
        for data in [FAKE_PE, packed("renamed.dat", FAKE_PE), gzip.compress(FAKE_PE),
                     packed("inner.zip", packed("driver.data", FAKE_PE))]:
            with self.subTest(length=len(data)):
                self.assertTrue(d.public_issues("renamed.data", data))

    def test_exact_hash_with_no_vendor_text_is_rejected(self):
        raw = b"synthetic known driver identity"
        with patch.object(d, "KNOWN_DPC_HASHES", {d.sha(raw)}):
            self.assertTrue(d.public_issues("data.bin", raw))

    def test_bad_paths_and_ambiguous_zips_rejected(self):
        for name in ["../driver", "C:/driver", "/root", "a\\b", "a//b", "a/./b", "a\0b"]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                d.public_issues(name, b"text")
        with self.assertRaises(ValueError):
            d.public_issues("a.zip", packed("../renamed", b"text"))
        out = io.BytesIO()
        with zipfile.ZipFile(out, "w") as z:
            z.writestr("A.txt", "a")
            z.writestr("a.txt", "b")
        with self.assertRaises(ValueError):
            d.public_issues("a.zip", out.getvalue())

    def test_archive_limits_fail_closed(self):
        with self.assertRaises(ValueError):
            d.public_issues("a.gz", gzip.compress(b"x" * 1000), budget=[50, 10])
        nested = b"text"
        for _ in range(4):
            nested = packed("inner.zip", nested)
        with self.assertRaises(ValueError):
            d.public_issues("a.zip", nested)
        with self.assertRaises(zipfile.BadZipFile):
            d.public_issues("a.zip", b"PK\x03\x04broken")


class OwnedRepositoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="driver-boundary-")
        self.base = Path(self.temp.name)
        self.root = self.base / "source"
        subprocess.check_call(["git", "init", "-q", str(self.root)])
        self.write("Adapter.cs", b"old staged source\r\n")
        self.git("add", "Adapter.cs")

    def tearDown(self):
        self.temp.cleanup()

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.root), *args])

    def write(self, name, data):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def spec(self, data=b"synthetic payload", authorized=False, approved=False, evidence=b"written fixture permission"):
        spec = {"bytes": len(data), "sha256": d.sha(data), "installRelativePath": "Drivers/DpcTimer/dpclat_x64.sys",
                "redistribution": {"status": "authorized" if authorized else "not-authorized", "authorizationEvidenceSha256": d.sha(evidence) if authorized else None},
                "securityReview": {"status": "approved" if approved else "not-approved"}}
        self.write("installer/driver-payloads.json", json.dumps({"schema": 1, "payloads": {"dpc-latency-checker-x64": spec}}).encode())
        return spec

    def test_index_uses_staged_bytes_not_worktree(self):
        self.write("Adapter.cs", b"unstaged edit")
        self.assertIn(b"old staged source", d.audit_index(self.root)["Adapter.cs"])
        path = self.write("renamed.txt", FAKE_PE)
        self.git("add", "renamed.txt")
        path.write_bytes(b"cover up the staged driver")
        before = self.git("ls-files", "--stage")
        with self.assertRaises(ValueError):
            d.audit_index(self.root)
        self.assertEqual(self.git("ls-files", "--stage"), before)
        path.unlink()
        with self.assertRaises(ValueError):
            d.audit_index(self.root)

    def test_nested_repository_root_rejected(self):
        nested = self.root / "nested"
        nested.mkdir()
        with self.assertRaises(ValueError):
            d.audit_index(nested)

    def test_source_archive_matches_canonical_index_and_inventory(self):
        self.write("SOURCE_FILES_SHA256.json", b"stale manifest")
        self.git("add", "SOURCE_FILES_SHA256.json")
        self.write("Adapter.cs", b"unstaged changed bytes")
        output = self.base / "output/source.zip"
        result = d.source_archive(self.root, output)
        with zipfile.ZipFile(output) as z:
            raw = z.read("DeltaNFD-Source/Adapter.cs")
            self.assertIn(b"old staged source", raw)
            inventory = json.loads(z.read("DeltaNFD-Source/SOURCE_FILES_SHA256.json"))
            self.assertEqual(inventory, {"Adapter.cs": hashlib.sha256(raw).hexdigest()})
        self.assertEqual(result["files"], 2)
        before = output.read_bytes()
        with self.assertRaises((ValueError, FileExistsError)):
            d.source_archive(self.root, output)
        self.assertEqual(output.read_bytes(), before)
        with self.assertRaises(ValueError):
            d.source_archive(self.root, self.root / "source.zip")

    def test_archive_refuses_indexed_vendor_bytes_before_creating_output(self):
        self.write("nested.zip", packed("renamed.bin", FAKE_PE))
        self.git("add", "nested.zip")
        output = self.base / "source.zip"
        with self.assertRaises(ValueError):
            d.source_archive(self.root, output)
        self.assertFalse(output.exists())

    def test_private_input_readonly_and_outside_root(self):
        data = b"synthetic payload"
        self.spec(data)
        source = self.base / "input.sys"
        source.write_bytes(data)
        self.assertEqual(d.verify_private(self.root, source)[1], data)
        self.assertEqual(source.read_bytes(), data)
        inside = self.write("local/input.sys", data)
        with self.assertRaises(ValueError):
            d.verify_private(self.root, inside)
        source.write_bytes(b"incorrect length")
        with self.assertRaises(ValueError):
            d.verify_private(self.root, source)
        source.write_bytes(b"x" * len(data))
        with self.assertRaises(ValueError):
            d.verify_private(self.root, source)

    def test_license_and_security_gates_precede_all_copies(self):
        for authorized, approved in [(False, False), (True, False)]:
            with self.subTest(authorized=authorized):
                self.spec(authorized=authorized, approved=approved)
                layout = self.base / ("out-" + str(authorized))
                with self.assertRaises(ValueError):
                    d.stage_private(self.root, self.base / "missing.sys", layout, None)
                self.assertFalse(layout.exists())

    def test_future_cleared_fixture_copy_never_overwrites(self):
        self.spec(authorized=True, approved=True)
        source = self.base / "input.sys"
        source.write_bytes(b"synthetic payload")
        evidence = self.base / "permission.txt"
        evidence.write_bytes(b"wrong permission")
        layout = self.base / "layout"
        with self.assertRaises(ValueError):
            d.stage_private(self.root, source, layout, evidence)
        self.assertFalse(layout.exists())
        evidence.write_bytes(b"written fixture permission")
        d.stage_private(self.root, source, layout, evidence)
        target = layout / "Drivers/DpcTimer/dpclat_x64.sys"
        self.assertEqual(target.read_bytes(), source.read_bytes())
        with self.assertRaises(ValueError):
            d.stage_private(self.root, source, layout, evidence)
        self.assertEqual(d.audit_layout(self.root, layout)["driverFiles"], ["Drivers/DpcTimer/dpclat_x64.sys"])

    def test_layout_detects_renamed_or_compressed_payload(self):
        self.spec()
        layout = self.base / "layout"
        layout.mkdir()
        self.assertEqual(d.audit_layout(self.root, layout)["driverFiles"], [])
        for data in [FAKE_PE, packed("renamed.dat", FAKE_PE)]:
            (layout / "data.bin").write_bytes(data)
            with self.assertRaises(ValueError):
                d.audit_layout(self.root, layout)

    def test_current_product_catalog_blocks_distribution(self):
        with self.assertRaisesRegex(ValueError, "not authorized"):
            d.distribution_gate(d.catalog(d.ROOT))


if __name__ == "__main__":
    unittest.main()
