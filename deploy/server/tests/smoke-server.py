#!/usr/bin/env python3
"""Smoke-test a framework-dependent production publish with a fake ESPHome device.

Usage: python -B deploy/server/tests/smoke-server.py artifacts/server-smoke/app
Uses localhost port 5171 and a temporary config/state directory; no real device is contacted.
"""
import importlib.util
import json
import os
import pathlib
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Device(BaseHTTPRequestHandler):
    reads = 0
    writes = 0

    def do_GET(self):
        type(self).reads += 1
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps({
            "id": "climate-air_conditioner", "mode": "HEAT", "current_temperature": "23.5",
            "target_temperature": "24.5", "min_temp": "17", "max_temp": "30", "step": 0.5,
        }).encode())

    def do_POST(self):
        type(self).writes += 1
        self.send_error(405)

    def log_message(self, *_):
        pass


def main():
    app = pathlib.Path(sys.argv[1]).resolve()
    with socket.socket() as available:
        available.bind(("127.0.0.1", 5171))
    helper = pathlib.Path(__file__).resolve().parents[1] / "linux" / "server-config.py"
    spec = importlib.util.spec_from_file_location("server_config", helper)
    config_module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(config_module)
    device = ThreadingHTTPServer(("127.0.0.1", 0), Device)
    threading.Thread(target=device.serve_forever, daemon=True).start()
    try:
        with tempfile.TemporaryDirectory(prefix="heatbridge-smoke-") as temporary:
            root = pathlib.Path(temporary)
            state = root / "state"
            state.mkdir()
            config = root / "web.json"
            config.write_text(json.dumps({
                "AllowedHosts": "localhost;127.0.0.1;[::1]",
                "HeatBridge": {"PollingInterval": "00:00:01", "RemoteAgents": {"Enabled": False}, "Devices": [{
                    "Id": "smoke-qlima", "Name": "Qlima smoke", "Transport": "Direct",
                    "BaseUrl": "http://127.0.0.1:" + str(device.server_port),
                    "SupportedModes": ["OFF", "HEAT"],
                }]},
            }), encoding="utf-8")
            env = {key: value for key, value in os.environ.items() if not key.casefold().startswith(("heatbridge", "aspnetcore_"))}
            env |= {"DOTNET_ENVIRONMENT": "Production", "ASPNETCORE_ENVIRONMENT": "Production",
                    "HEATBRIDGE_WEB_CONFIG": str(config), "HEATBRIDGE_WEB_STATE": str(state),
                    "Logging__EventLog__LogLevel__Default": "None"}
            command = ["dotnet", str(app / "HeatBridge.Web.dll")]
            check = subprocess.run(command + ["--check-config"], cwd=app, env=env, capture_output=True, text=True, timeout=30)
            assert check.returncode == 0, check.stdout + check.stderr
            assert Device.reads == 0, "Configuration validation polled the device."
            assert "1 direct device(s)" in check.stdout, check.stdout

            first_keys = None
            for restart in range(2):
                with open(root / "server.log", "w+", encoding="utf-8") as log:
                    process = subprocess.Popen(command + ["--urls", "http://127.0.0.1:5171"], cwd=app, env=env,
                                               stdout=log, stderr=subprocess.STDOUT)
                    try:
                        for _ in range(100):
                            if process.poll() is not None:
                                log.seek(0)
                                raise AssertionError(log.read()[-6000:])
                            try:
                                dashboard = config_module.get("http://127.0.0.1:5171/").decode()
                                if "Qlima smoke" in dashboard and Device.reads > 0:
                                    break
                            except (OSError, urllib.error.URLError):
                                pass
                            time.sleep(0.1)
                        else:
                            log.seek(0)
                            raise AssertionError("Dashboard was not ready.\n" + log.read()[-6000:])
                        config_module.verify()
                        config_module.probe(config)
                        request = urllib.request.Request("http://127.0.0.1:5171/", headers={"Host": "untrusted.invalid"})
                        try:
                            urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request, timeout=5)
                            raise AssertionError("An untrusted host header was accepted.")
                        except urllib.error.HTTPError as error:
                            assert error.code == 400, error.code
                        keys = {file.name: file.read_bytes() for file in (state / "keys").glob("*.xml")}
                        assert keys, "No data-protection keys were persisted."
                        if restart == 0:
                            first_keys = keys
                        else:
                            assert keys == first_keys, "Restart replaced the data-protection keys."
                    finally:
                        process.terminate()
                        process.wait(timeout=15)
            assert Device.writes == 0, "Smoke test issued a control command."
            print("Production smoke passed: external config, no-poll validation, dashboard/assets, host restriction, restart and persistent keys.")
    finally:
        device.shutdown()
        device.server_close()


if __name__ == "__main__":
    main()
