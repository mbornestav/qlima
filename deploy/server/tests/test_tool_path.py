"""Regression coverage for administrative tools omitted from a non-interactive SSH PATH."""
import os
import pathlib
import subprocess
import tempfile
import unittest

LINUX = pathlib.Path(__file__).resolve().parents[1] / "linux"


@unittest.skipUnless(os.name == "posix", "Requires Linux administrative tool directories.")
class ToolPathTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.bin = pathlib.Path(self.temp.name)
        # Isolate the tool lookup from architecture, capacity and real service checks.
        for name in ("uname", "systemctl", "python3", "curl", "ss", "awk", "df", "getconf", "flock"):
            self.executable(name, "printf 'aarch64\\n'" if name == "uname" else ":")

    def executable(self, name, body):
        path = self.bin / name
        path.write_text("#!/bin/bash\n" + body + "\n", encoding="utf-8")
        path.chmod(0o755)
        return path

    def scripts(self):
        yield ("preflight", (LINUX / "preflight.sh").read_text().split("[[ -d /run/systemd/system ]]")[0])
        yield ("management", (LINUX / "heatbridge-webctl").read_text().split("readonly SERVICE=")[0])

    def locate_runuser(self, script):
        return subprocess.run(
            ["/bin/bash", "-c", script + "\ncommand -v runuser\n"],
            env=os.environ | {"PATH": str(self.bin)},
            capture_output=True, text=True, timeout=10,
        )

    def test_restricted_ssh_path_finds_system_runuser(self):
        candidates = [pathlib.Path(directory) / "runuser" for directory in ("/usr/local/sbin", "/usr/sbin", "/sbin")]
        if not any(path.is_file() and os.access(path, os.X_OK) for path in candidates):
            self.skipTest("Install util-linux to exercise the real runuser lookup.")
        for name, script in self.scripts():
            with self.subTest(script=name):
                result = self.locate_runuser(script)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn(pathlib.Path(result.stdout.strip()), candidates)

    def test_existing_tool_path_is_preserved(self):
        expected = self.executable("runuser", ":")
        for name, script in self.scripts():
            with self.subTest(script=name):
                result = self.locate_runuser(script)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(result.stdout.strip(), str(expected))


if __name__ == "__main__":
    unittest.main()
