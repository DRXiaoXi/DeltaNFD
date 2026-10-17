import importlib.util
import gzip
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("file_builder", Path(__file__).with_name("make-file-update.py"))
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class BuilderChecks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="DeltaNFD-file-builder-")
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.layout = self.root / "layout"
        self.layout.mkdir()
        names = ["DeltaNFD.exe", "DeltaNFD.dll", "DeltaNFD.deps.json", "DeltaNFD.runtimeconfig.json", "DeltaNFD.pri",
                 "DeltaNFD.Contracts.dll", "DeltaNFD.Shared.dll", "DeltaNFD.UpdateCore.dll", "Tools/DeploymentControl.ps1",
                 "Backends/DeltaNFD.FileUpdater.exe", "Backends/DeltaNFD.FileUpdater.dll", "Backends/DeltaNFD.FileUpdater.deps.json", "Backends/DeltaNFD.FileUpdater.runtimeconfig.json"]
        for name in ("Ace", "Gpu", "Runtime", "SystemMaintenance", "CpuFrame", "Procman"):
            names.extend("Backends/DeltaNFD.Backend." + name + ext for ext in (".exe", ".dll", ".deps.json", ".runtimeconfig.json"))
        for name in names:
            path = self.layout / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"same fixture payload")
        self.wrapper = self.root / "installer.json"
        self.wrapper.write_text(json.dumps({"version": "0.9192", "displayVersion": "Beta0.9192", "channel": "beta",
            "installers": [{"url": "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.9192/setup.exe", "sizeBytes": 1000, "sha256": "a" * 64}]}))
        self.out = self.root / "output"

    def build(self, **kw):
        return builder.build(self.layout, self.out, "https://deltaxiaoxi.xin/updates/packages/test", self.wrapper, **kw)

    def test_dedup_and_hashes(self):
        result = self.build()
        objects = list((self.out / "objects").glob("*.gz"))
        self.assertEqual(len(objects), 1)
        self.assertEqual(gzip.decompress(objects[0].read_bytes()), b"same fixture payload")
        descriptor = json.loads((self.out / "update.json").read_text())["fileUpdate"]
        self.assertEqual(descriptor["sha256"], builder.sha(self.out / "files.json"))
        self.assertEqual(result["files"][0]["downloadSha256"], builder.sha(objects[0]))
        self.assertEqual((self.out / "files.json").read_bytes(), (self.out / "DeltaNFD_Files_0.9192.0_x64.json").read_bytes())

    def test_repeatable(self):
        self.build()
        other = self.root / "other"
        builder.build(self.layout, other, "https://deltaxiaoxi.xin/updates/packages/test", self.wrapper)
        self.assertEqual((self.out / "files.json").read_bytes(), (other / "files.json").read_bytes())

    def test_existing_output_preserved(self):
        self.out.mkdir()
        (self.out / "important.txt").write_text("keep")
        with self.assertRaises(ValueError): self.build()
        self.assertEqual((self.out / "important.txt").read_text(), "keep")

    def test_incomplete_layout(self):
        (self.layout / "Backends/DeltaNFD.FileUpdater.exe").unlink()
        with self.assertRaises(ValueError): self.build()

    def test_user_data_rejected(self):
        (self.layout / "settings.json").write_text("private")
        with self.assertRaises(ValueError): self.build()
        self.assertFalse(self.out.exists())

    def test_invalid_url_rejected(self):
        for url in ("http://deltaxiaoxi.xin/test", "https://deltaxiaoxi.xin.evil/test", "https://user@deltaxiaoxi.xin/test"):
            with self.assertRaises(ValueError): builder.build(self.layout, self.out, url, self.wrapper)

    def test_previous_inventory_removals(self):
        previous = self.root / "previous.json"
        previous.write_text(json.dumps({"schemaVersion": 1, "files": [{"path": "Assets/obsolete.dll", "sha256": "f" * 64}]}))
        result = self.build(previous=previous)
        self.assertEqual(result["remove"], [{"path": "Assets/obsolete.dll", "previousSha256": "f" * 64}])

    def test_localization(self):
        path = self.layout / "zh-CN" / "test.dll"
        path.parent.mkdir()
        path.write_bytes(b"localized")
        self.assertIn("zh-CN/test.dll", [entry["path"] for entry in self.build()["files"]])

    def test_optional_github_objects(self):
        result = self.build(github_base="https://github.com/DRXiaoXi/DeltaNFD/releases/download/v1")
        self.assertTrue(result["files"][0]["urls"][0].startswith("https://github.com/"))
        self.assertTrue(result["files"][0]["urls"][1].startswith("https://deltaxiaoxi.xin/"))


if __name__ == "__main__":
    unittest.main()
