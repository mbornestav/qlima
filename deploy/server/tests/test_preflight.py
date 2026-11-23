"""Run the complete read-only preflight against a simulated small Raspberry Pi."""
import os
import pathlib
import subprocess
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "linux" / "preflight.sh"


@unittest.skipUnless(os.name == "posix", "Requires Linux shell utilities.")
class PreflightTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.meminfo = self.root / "meminfo"
        self.meminfo.write_text("MemAvailable: 706560 kB\n")  # Pi output: 690 MiB available.
        source = SCRIPT.read_text()
        for original, name, contents in (
            ("/proc/meminfo", "meminfo", None),
            ("/etc/os-release", "os-release", 'PRETTY_NAME="Debian GNU/Linux 13 (trixie)"\n'),
            ("/etc/heatbridge/agent.json", "agent.json", "{}"),
        ):
            destination = self.root / name
            if contents is not None:
                destination.write_text(contents)
            source = source.replace(original, str(destination))
        source = source.replace("/run/systemd/system", str(self.root))
        self.script = self.root / "preflight.sh"
        self.script.write_text(source)
        self.env = os.environ | {
            "PATH": str(self.bin) + ":/usr/bin:/bin", "TEST_TMP_KB": str(453 * 1024),
            "TEST_OPT_KB": str(25 * 1024 * 1024), "TEST_VAR_KB": str(25 * 1024 * 1024),
        }
        self.mock("uname", "echo aarch64")
        self.mock("hostname", "echo printpi")
        self.mock("free", ":")
        self.mock("curl", ":")
        self.mock("df", r'''
path="${@: -1}"
case "$path" in
    /opt) available="$TEST_OPT_KB" ;;
    /tmp) available="$TEST_TMP_KB" ;;
    /var) available="$TEST_VAR_KB" ;;
    *) exit 2 ;;
esac
printf '%s\n' 'Filesystem 1024-blocks Used Available Capacity Mounted-on' "mock 30000000 0 $available 0% $path"
''')
        self.mock("systemctl", r'''
case "$1" in
    is-active) if [[ "${@: -1}" == heatbridge-agent ]]; then echo activating; else echo inactive; fi; exit 3 ;;
    is-enabled) echo enabled ;;
    *) echo 'ERROR: Preflight tried to mutate services.' >&2; exit 99 ;;
esac
''')
        self.mock("ss", 'if [[ "${TEST_BUSY_PORT:-0}" == 1 ]]; then echo "LISTEN 0 512 127.0.0.1:5171 0.0.0.0:*"; fi')

    def mock(self, name, body):
        path = self.bin / name
        path.write_text("#!/bin/bash\nset -eu\n" + body + "\n")
        path.chmod(0o755)

    def run_preflight(self, **overrides):
        return subprocess.run(["bash", str(self.script)], env=self.env | overrides,
                              capture_output=True, text=True, timeout=10)

    def test_pi_with_453_mib_tmpfs_passes(self):
        result = self.run_preflight()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Preflight passed", result.stdout)

    def test_full_temporary_filesystem_still_fails(self):
        result = self.run_preflight(TEST_TMP_KB="0")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("16 MiB free on /tmp", result.stderr)

    def test_small_temporary_filesystem_has_enough_room_for_config(self):
        result = self.run_preflight(TEST_TMP_KB=str(16 * 1024))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_insufficient_release_space_still_fails(self):
        result = self.run_preflight(TEST_OPT_KB=str(511 * 1024))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("512 MiB free on /opt", result.stderr)

    def test_insufficient_memory_still_fails(self):
        self.meminfo.write_text("MemAvailable: 200000 kB\n")
        result = self.run_preflight()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("384 MiB available RAM", result.stderr)

    def test_conflicting_listener_still_fails(self):
        result = self.run_preflight(TEST_BUSY_PORT="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Port 5171 is in use", result.stderr)


if __name__ == "__main__":
    unittest.main()
