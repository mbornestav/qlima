"""Run with python -m unittest discover -s deploy/server/tests -v."""
import importlib.util
import json
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "linux" / "server-config.py"
SPEC = importlib.util.spec_from_file_location("server_config", SCRIPT)
config = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(config)


class ConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.source = pathlib.Path(self.temp.name) / "agent.json"
        self.output = pathlib.Path(self.temp.name) / "web.json"
        self.agent = {"HeatBridge": {
            "Agent": {"Secret": "must-never-be-exported", "ServerUrl": "wss://example.test"},
            "PollingInterval": "00:00:09",
            "Devices": [{"Id": "qlima", "Name": "Qlima Skåne", "BaseUrl": "http://192.0.2.1",
                         "ClimateEntity": "air_conditioner", "SupportedModes": ["OFF", "HEAT"],
                         "AgentId": "printpi", "Transport": "Agent", "Secret": "also-excluded"}],
        }}

    def export(self):
        self.source.write_text(json.dumps(self.agent), encoding="utf-8")
        config.import_agent(self.source, self.output)
        return json.loads(self.output.read_text(encoding="utf-8"))

    def test_import_preserves_device_settings_without_agent_secrets(self):
        result = self.export()
        heat = result["HeatBridge"]
        self.assertEqual(heat["PollingInterval"], "00:00:09")
        self.assertEqual(heat["Devices"][0]["Name"], "Qlima Skåne")
        self.assertEqual(heat["Devices"][0]["SupportedModes"], ["OFF", "HEAT"])
        self.assertEqual(heat["Devices"][0]["Transport"], "Direct")
        self.assertFalse(heat["RemoteAgents"]["Enabled"])
        for forbidden in ("Secret", "ServerUrl", "AgentId", "must-never-be-exported", "also-excluded"):
            self.assertNotIn(forbidden, self.output.read_text(encoding="utf-8"))

    def test_import_never_overwrites_existing_settings(self):
        self.output.write_text("existing", encoding="utf-8")
        with self.assertRaises(FileExistsError):
            self.export()
        self.assertEqual(self.output.read_text(encoding="utf-8"), "existing")

    def test_public_binding_wildcard_hosts_and_remote_devices_are_rejected(self):
        result = self.export()
        mutations = [
            {"Urls": "http://0.0.0.0:5171"},
            {"AllowedHosts": "*"},
            {"Kestrel": {"Endpoints": {"Http": {"Url": "http://*:5000"}}}},
            {"HeatBridge": {"Devices": []}},
            {"HeatBridge": {"Devices": [{"Transport": "Agent"}]}},
            {"HeatBridge": {"Devices": [{"Transport": "Direct"}], "RemoteAgents": {"Enabled": True}}},
        ]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                self.output.write_text(json.dumps(result | mutation), encoding="utf-8")
                with self.assertRaises(ValueError):
                    config.validate(self.output)

    def test_duplicate_case_insensitive_keys_are_rejected(self):
        self.source.write_text('{"Urls":"safe","urls":"unsafe"}', encoding="utf-8")
        with self.assertRaises(ValueError):
            config.load(self.source)

    def test_case_insensitive_agent_keys_work(self):
        self.agent = config.folded(self.agent)
        self.assertEqual(self.export()["HeatBridge"]["Devices"][0]["Id"], "qlima")


if __name__ == "__main__":
    unittest.main()
