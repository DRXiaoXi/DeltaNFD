"""Boundary checks using throwaway repositories; never touch product/system state."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from check_repository import violations

ROOT = Path(__file__).resolve().parents[1]
PWSH = shutil.which("pwsh")


class RepositoryBoundaryTests(unittest.TestCase):
    def test_generated_and_private_paths_are_rejected(self):
        for name in ("_buildcheck/a.cs", "src/App/bin/a.dll", "src/App/obj/a.json",
                     "installer/Output/setup.exe", ".tmp_logs12/log.txt", ".env.production", "secret.pem"):
            with self.subTest(name=name):
                self.assertTrue(violations(name, 1))

    def test_source_and_licensed_dependencies_are_allowed(self):
        for name in ("src/App/View.xaml", "tools/vendor/library.lib", ".env.example",
                     "retired/old/Engine.cs", "notes/verification.md"):
            with self.subTest(name=name):
                self.assertFalse(violations(name, 1))

    def test_github_size_boundary(self):
        self.assertFalse(violations("data.dat", 100 * 1024**2 - 1))
        self.assertTrue(violations("data.dat", 100 * 1024**2))


@unittest.skipUnless(PWSH, "PowerShell 7 is needed for Windows cleanup checks")
class CleanupSafetyTests(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / "_buildcheck"
        scratch.mkdir(exist_ok=True)
        self.repo = Path(tempfile.mkdtemp(prefix="workspace-maintenance-fixture-", dir=scratch)).resolve()
        (self.repo / "tools").mkdir()
        self.script = self.repo / "tools/Clean-Workspace.ps1"
        shutil.copyfile(ROOT / "tools/Clean-Workspace.ps1", self.script)
        subprocess.run(["git", "init", "--quiet", str(self.repo)], check=True, capture_output=True)
        (self.repo / ".gitignore").write_text("bin/\n_buildcheck/\n", encoding="utf-8")
        self.output = self.repo / "src/Example/bin"
        self.output.mkdir(parents=True)
        (self.output / "generated.dll").write_text("fixture", encoding="utf-8")
        (self.repo / "src/Example/Engine.cs").write_text("// retain source\n", encoding="utf-8")
        self.plan = self.repo / "plan.json"

    def tearDown(self):
        def remove_readonly(function, path, exception):
            os.chmod(path, 0o700)
            function(path)
        shutil.rmtree(self.repo, onexc=remove_readonly)

    def invoke(self, paths, apply=True, extra=()):
        self.plan.write_text(json.dumps({"root": str(self.repo), "targets": [{"path": p} for p in paths]}), encoding="utf-8")
        command = [PWSH, "-NoProfile", "-File", str(self.script), "-PlanPath", str(self.plan)]
        if apply:
            command.append("-Apply")
        return subprocess.run(command + list(extra), text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=90)

    def test_preview_does_not_delete(self):
        result = self.invoke(["src/Example/bin"], apply=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(self.output.exists())

    def test_apply_removes_only_output_and_repeats_safely(self):
        for _ in range(2):
            result = self.invoke(["src/Example/bin"])
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(self.output.exists())
        self.assertTrue((self.repo / "src/Example/Engine.cs").is_file())
        self.assertFalse((self.repo / ".git/index").exists())

    def test_whatif_does_not_delete(self):
        result = self.invoke(["src/Example/bin"], extra=("-WhatIf",))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(self.output.exists())

    def test_custom_dotnet_output_is_recognized(self):
        output = self.repo / '_buildcheck/probe-build'
        output.mkdir(parents=True)
        (output / 'Probe.deps.json').write_text('{}', encoding='utf-8')
        (output / 'Probe.dll').write_text('generated', encoding='utf-8')
        result = self.invoke(['_buildcheck/probe-build'])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(output.exists())

    def test_invalid_targets_fail_before_any_delete(self):
        for target in ("../outside", str(self.repo.parent), ".git", "src/Example", "src/Example/bin/../../.."):
            with self.subTest(target=target):
                result = self.invoke(["src/Example/bin", target])
                self.assertNotEqual(result.returncode, 0)
                self.assertTrue(self.output.exists())

    def test_frozen_release_is_protected(self):
        protected = self.repo / "_buildcheck/Beta0.922/publish"
        protected.mkdir(parents=True)
        (protected / "DeltaNFD.exe").write_text("frozen", encoding="utf-8")
        result = self.invoke(["src/Example/bin", "_buildcheck/Beta0.922/publish"])
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue(self.output.exists())
        self.assertEqual((protected / "DeltaNFD.exe").read_text(encoding="utf-8"), "frozen")

    def test_git_visible_file_blocks_deletion(self):
        subprocess.run(["git", "-C", str(self.repo), "add", "--force", "src/Example/bin/generated.dll"], check=True, capture_output=True)
        result = self.invoke(["src/Example/bin"])
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue(self.output.exists())

    def test_embedded_repository_blocks_all_deletions(self):
        (self.output / ".git").mkdir()
        result = self.invoke(["src/Example/bin"])
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue((self.output / "generated.dll").is_file())

    @unittest.skipUnless(os.name == "nt", "Windows junction boundary")
    def test_junction_cannot_redirect_cleanup(self):
        destination = self.repo / "untouched"
        destination.mkdir()
        (destination / "evidence.txt").write_text("retain", encoding="utf-8")
        junction = self.output / "redirect"
        command = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:FIXTURE_LINK -Target $env:FIXTURE_TARGET | Out-Null"
        result = subprocess.run([PWSH, "-NoProfile", "-Command", command], env={**os.environ, "FIXTURE_LINK": str(junction), "FIXTURE_TARGET": str(destination)}, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if result.returncode != 0:
            self.skipTest("This host cannot create Windows junctions; rejection must also be checked against existing links")
        result = self.invoke(["src/Example/bin"])
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((destination / "evidence.txt").read_text(encoding="utf-8"), "retain")


if __name__ == "__main__":
    unittest.main()
