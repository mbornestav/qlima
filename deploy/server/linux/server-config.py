#!/usr/bin/env python3
"""Import only device settings, validate private hosting, and perform read-only smoke checks."""
import json
import sys
import urllib.parse
import urllib.request
from html.parser import HTMLParser


def unique_object(pairs):
    result = {}
    seen = set()
    for key, value in pairs:
        if key.casefold() in seen:
            raise ValueError("Duplicate configuration key: " + key)
        seen.add(key.casefold())
        result[key] = value
    return result


def load(path):
    with open(path, encoding="utf-8-sig") as stream:
        return json.load(stream, object_pairs_hook=unique_object)


def folded(value):
    if isinstance(value, dict):
        return {key.casefold(): folded(item) for key, item in value.items()}
    if isinstance(value, list):
        return [folded(item) for item in value]
    return value


def import_agent(source, destination):
    heat = folded(load(source))["heatbridge"]
    device_keys = ("Id", "Name", "BaseUrl", "ClimateEntity", "OutdoorTemperatureSensor",
                   "RequestTimeout", "DiscoveryTimeout", "SupportedModes")
    devices = [{key: device[key.casefold()] for key in device_keys if key.casefold() in device}
               for device in heat["devices"]]
    for device in devices:
        device["Transport"] = "Direct"
    config = {
        "Urls": "http://127.0.0.1:5171",
        "AllowedHosts": "localhost;127.0.0.1;[::1]",
        "HeatBridge": {"Devices": devices, "RemoteAgents": {"Enabled": False, "Agents": []}},
    }
    for key in ("PollingInterval", "ConfirmationAttempts", "ConfirmationDelay", "CapabilityRetryInterval"):
        if key.casefold() in heat:
            config["HeatBridge"][key] = heat[key.casefold()]
    # Exclusive creation: an update must never overwrite a user's configuration.
    with open(destination, "x", encoding="utf-8", newline="\n") as stream:
        json.dump(config, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
    validate(destination)


def validate(path):
    config = folded(load(path))
    if "kestrel" in config or config.get("urls", "http://127.0.0.1:5171") != "http://127.0.0.1:5171":
        raise ValueError("Private Pi hosting requires the sole listener http://127.0.0.1:5171; remove Kestrel overrides.")
    hosts = set(config.get("allowedhosts", "").split(";"))
    if not hosts or not hosts.issubset({"localhost", "127.0.0.1", "[::1]"}):
        raise ValueError("AllowedHosts must contain only localhost, 127.0.0.1 and/or [::1].")
    heat = config["heatbridge"]
    if heat.get("remoteagents", {}).get("enabled", False) is not False:
        raise ValueError("RemoteAgents must be disabled for this server-only deployment.")
    if not isinstance(heat.get("devices"), list) or not heat["devices"]:
        raise ValueError("At least one direct device is required.")
    for device in heat["devices"]:
        if device.get("transport", "Direct").casefold() != "direct":
            raise ValueError("Every device must use Direct transport.")
    return heat["devices"]


def get(url):
    # Device and loopback requests must never use a proxy inherited from the shell.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(url, timeout=10) as response:
        return response.read(4 * 1024 * 1024)


def probe(path):
    for device in validate(path):
        entity = urllib.parse.quote(device.get("climateentity", "air_conditioner"), safe="")
        url = device["baseurl"].rstrip("/") + "/climate/" + entity + "?detail=all"
        state = json.loads(get(url))
        if not isinstance(state, dict) or "mode" not in state or "target_temperature" not in state:
            raise ValueError("Device did not return an ESPHome climate reading: " + device["id"])
        print("Read-only device check passed: " + device["id"])


class Assets(HTMLParser):
    def __init__(self):
        super().__init__()
        self.urls = []

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "link" and attrs.get("rel") == "stylesheet":
            self.urls.append(attrs["href"])
        if tag == "script" and attrs.get("src"):
            self.urls.append(attrs["src"])


def verify():
    base = "http://127.0.0.1:5171/"
    if get(base + "healthz").strip() != b"ok":
        raise ValueError("Server health check failed.")
    dashboard = get(base).decode("utf-8")
    if "HeatBridge" not in dashboard:
        raise ValueError("Unexpected dashboard response.")
    assets = Assets()
    assets.feed(dashboard)
    if not assets.urls:
        raise ValueError("Dashboard has no CSS or JavaScript assets.")
    for asset in assets.urls:
        url = urllib.parse.urljoin(base, asset)
        if urllib.parse.urlsplit(url).netloc != "127.0.0.1:5171":
            raise ValueError("Unexpected external dashboard asset.")
        if not get(url):
            raise ValueError("Empty dashboard asset: " + asset)
    print("Dashboard, health and static assets verified.")


if __name__ == "__main__":
    try:
        command = sys.argv[1]
        if command == "import":
            import_agent(sys.argv[2], sys.argv[3])
        elif command == "validate":
            validate(sys.argv[2])
        elif command == "probe":
            probe(sys.argv[2])
        elif command == "verify":
            verify()
        else:
            raise ValueError("Unknown command.")
    except (OSError, ValueError, KeyError, TypeError, IndexError) as error:
        sys.exit("ERROR: " + str(error))
