"""Exercise the real activation transaction with isolated paths and simulated system services.

Run on Linux (including WSL). No root access or actual service changes are needed.
"""
import os
import pathlib
import subprocess
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "linux" / "heatbridge-webctl"


@unittest.skipUnless(os.name == "posix", "Activation transaction tests require Linux symlinks and utilities.")
class ActivationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.install = self.root / "install"
        self.releases = self.install / "releases"
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.releases.mkdir(parents=True)
        self.unit = self.root / "web.service"
        source = SCRIPT.read_text(encoding="utf-8").split('\ncommand="${1:-help}"')[0]
        for before, after in {
            "/opt/heatbridge/web": str(self.install),
            "/etc/heatbridge-web": str(self.root / "config"),
            "/etc/systemd/system/heatbridge-web.service": str(self.unit),
            "/var/lib/heatbridge-web": str(self.root / "state"),
        }.items():
            source = source.replace(before, after)
        self.script = self.root / "activate.sh"
        self.script.write_text(source + '\nactivate "$1"\n', encoding="utf-8")
        self.env = os.environ | {"PATH": str(self.bin) + ":/usr/bin:/bin", "TEST_ROOT": str(self.root)}
        self.mock("systemctl", r'''
command="$1"; shift
service="${@: -1}"
case "$command" in
  is-active) value=$(cat "$TEST_ROOT/active-$service"); [[ " $* " == *" --quiet "* ]] || echo "$value"; [[ "$value" == active ]] ;;
  is-enabled) value=$(cat "$TEST_ROOT/enabled-$service"); echo "$value"; [[ "$value" == enabled ]] ;;
  show) echo 4321 ;;
  start|restart) echo active > "$TEST_ROOT/active-$service" ;;
  stop) echo inactive > "$TEST_ROOT/active-$service" ;;
  enable) echo enabled > "$TEST_ROOT/enabled-$service" ;;
  disable)
    echo disabled > "$TEST_ROOT/enabled-$service"
    if [[ " $* " == *" --now "* ]]; then echo inactive > "$TEST_ROOT/active-$service"; fi
    if [[ "$service" == heatbridge-agent && -f "$TEST_ROOT/fail-agent-stop" ]]; then exit 1; fi
    ;;
  daemon-reload) : ;;
  *) echo "Unexpected systemctl call: $command $*" >&2; exit 2 ;;
esac
''')
        self.mock("runuser", '[[ ! -f "$TEST_ROOT/fail-config" ]]\n')
        self.mock("python3", r'''
if [[ "$2" == verify && -f "$TEST_ROOT/fail-verify" ]]; then exit 1; fi
if [[ "$2" == probe && -f "$TEST_ROOT/fail-probe" ]]; then exit 1; fi
exit 0
''')
        self.mock("ss", '''echo 'LISTEN 0 512 127.0.0.1:5171 0.0.0.0:* users:(("HeatBridge.Web",pid=4321,fd=12))'\n''')
        self.mock("sleep", 'exit 0\n')
        self.service_state("heatbridge-agent", "active", "enabled")
        self.service_state("heatbridge-web", "inactive", "disabled")
        self.new = self.release("new")

    def mock(self, name, body):
        path = self.bin / name
        path.write_text("#!/bin/bash\nset -eu\n" + body, encoding="utf-8")
        path.chmod(0o755)

    def service_state(self, service, active, enabled):
        (self.root / ("active-" + service)).write_text(active)
        (self.root / ("enabled-" + service)).write_text(enabled)

    def assert_service(self, service, active, enabled):
        self.assertEqual((self.root / ("active-" + service)).read_text().strip(), active)
        self.assertEqual((self.root / ("enabled-" + service)).read_text().strip(), enabled)

    def release(self, name):
        path = self.releases / name
        (path / "app").mkdir(parents=True)
        executable = path / "app" / "HeatBridge.Web"
        executable.write_text("#!/bin/bash\nexit 0\n")
        executable.chmod(0o755)
        (path / "heatbridge-web.service").write_text(name)
        return path

    def activate(self, target, success):
        result = subprocess.run(["bash", str(self.script), str(target)], env=self.env, capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)

    def existing_server(self):
        old = self.release("old")
        older = self.release("older")
        (self.install / "current").symlink_to(old)
        (self.install / "previous").symlink_to(older)
        self.unit.write_text("old")
        self.service_state("heatbridge-web", "active", "enabled")
        self.service_state("heatbridge-agent", "inactive", "disabled")
        return old, older

    def test_first_activation_stops_agent_only_after_server_is_ready(self):
        self.activate(self.new, success=True)
        self.assertEqual((self.install / "current").resolve(), self.new)
        self.assert_service("heatbridge-web", "active", "enabled")
        self.assert_service("heatbridge-agent", "inactive", "disabled")

    def test_failed_first_start_restores_original_agent_and_removes_new_unit(self):
        (self.root / "fail-verify").touch()
        self.activate(self.new, success=False)
        self.assertFalse((self.install / "current").exists())
        self.assertFalse(self.unit.exists())
        self.assert_service("heatbridge-agent", "active", "enabled")
        self.assert_service("heatbridge-web", "inactive", "disabled")

    def test_failed_agent_cutover_restores_agent_running_and_enabled(self):
        (self.root / "fail-agent-stop").touch()
        self.activate(self.new, success=False)
        self.assert_service("heatbridge-agent", "active", "enabled")
        self.assert_service("heatbridge-web", "inactive", "disabled")

    def test_starting_agent_is_stopped_even_when_not_enabled(self):
        self.service_state("heatbridge-agent", "activating", "disabled")
        self.activate(self.new, success=True)
        self.assert_service("heatbridge-agent", "inactive", "disabled")

    def test_failed_cutover_restarts_agent_that_was_activating(self):
        self.service_state("heatbridge-agent", "activating", "enabled")
        (self.root / "fail-agent-stop").touch()
        self.activate(self.new, success=False)
        self.assert_service("heatbridge-agent", "active", "enabled")
        self.assert_service("heatbridge-web", "inactive", "disabled")

    def test_failed_update_restores_both_links_and_previous_unit(self):
        old, older = self.existing_server()
        (self.root / "fail-verify").touch()
        self.activate(self.new, success=False)
        self.assertEqual((self.install / "current").resolve(), old)
        self.assertEqual((self.install / "previous").resolve(), older)
        self.assertEqual(self.unit.read_text(), "old")
        self.assert_service("heatbridge-web", "active", "enabled")
        self.assert_service("heatbridge-agent", "inactive", "disabled")

    def test_successful_update_and_rollback_exchange_releases(self):
        old, _ = self.existing_server()
        self.activate(self.new, success=True)
        self.assertEqual((self.install / "previous").resolve(), old)
        self.activate(old, success=True)
        self.assertEqual((self.install / "current").resolve(), old)
        self.assertEqual((self.install / "previous").resolve(), self.new)

    def test_bad_configuration_or_unreachable_device_changes_no_services(self):
        old, older = self.existing_server()
        for marker in ("fail-config", "fail-probe"):
            with self.subTest(marker=marker):
                failure = self.root / marker
                failure.touch()
                self.activate(self.new, success=False)
                failure.unlink()
                self.assertEqual((self.install / "current").resolve(), old)
                self.assertEqual((self.install / "previous").resolve(), older)
                self.assert_service("heatbridge-web", "active", "enabled")
                self.assert_service("heatbridge-agent", "inactive", "disabled")


if __name__ == "__main__":
    unittest.main()
