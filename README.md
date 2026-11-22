# HeatBridge

HeatBridge is a self-hosted platform for controlling air conditioners and heat pumps through open
protocols, without proprietary clouds such as Tuya, Smart Life or NetHome Plus.

This repository contains the first working MVP: a local web dashboard (in Swedish) for a
**Qlima heat pump** with an **SMLIGHT SLWF-01Pro** Wi-Fi module running **ESPHome** with the
Midea UART component.

![Dashboard](docs/dashboard.jpg)

## Features

- Live dashboard showing online/offline status, indoor temperature, target temperature, operating mode,
  fan mode, outdoor temperature and the time of the last successful update.
- Large +/− controls in 0.5 °C steps, limited to the range the device reports (17–30 °C on the Qlima).
- Operating modes discovered from the device. Only the modes the device supports are shown
  (Av, Värme, Kyla, Auto, Fläkt, Avfuktning).
- Every command is confirmed by reading the device state back. The UI shows when a command is
  being sent, when it succeeds and when it fails.
- Automatic refresh every 5 seconds without page reloads. Background refreshes never overwrite a
  change you are making.
- Responsive layout for mobile and desktop, light and dark mode.
- Listens on `localhost` only. There is no public control API.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or later)
- A computer on the same network as the heat pump (the MVP talks to the device directly)

Check your installation:

```powershell
dotnet --list-sdks
```

## Quick start

```powershell
git clone <repository-url> heatbridge
cd heatbridge
dotnet run --project src/HeatBridge.Web
```

Open <http://localhost:5171>. The development configuration points to the Qlima at `192.168.50.215`.

## Configuration

Devices are configured under `HeatBridge:Devices`. The default development device lives in
[`src/HeatBridge.Web/appsettings.Development.json`](src/HeatBridge.Web/appsettings.Development.json):

```json
{
  "HeatBridge": {
    "Devices": [
      {
        "Id": "qlima-skane",
        "Name": "Qlima Skåne",
        "BaseUrl": "http://192.168.50.215",
        "ClimateEntity": "air_conditioner",
        "OutdoorTemperatureSensor": "air_conditioner_outdoor_temperature",
        "RequestTimeout": "00:00:05"
      }
    ]
  }
}
```

| Setting | Description |
| --- | --- |
| `Id` | Stable identifier, unique per device. |
| `Name` | Name shown in the UI. |
| `BaseUrl` | Address of the ESPHome web server. |
| `ClimateEntity` | Climate entity in the REST URL (`/climate/<ClimateEntity>`). |
| `OutdoorTemperatureSensor` | Optional sensor entity (`/sensor/<name>`) for the outdoor temperature. |
| `RequestTimeout` | Timeout for a single request to the device. |
| `DiscoveryTimeout` | How long to listen on `/events` when discovering supported modes (default 8 s). |
| `SupportedModes` | Optional override, e.g. `["OFF","HEAT"]`. Leave empty to discover modes from the device. |

Application-wide settings live in [`appsettings.json`](src/HeatBridge.Web/appsettings.json):
`PollingInterval` (default 5 s), `ConfirmationAttempts` (6), `ConfirmationDelay` (0.5 s) and
`CapabilityRetryInterval` (1 min).

There are three ways to point HeatBridge at another device without editing tracked files:

1. Copy [`appsettings.Local.example.json`](src/HeatBridge.Web/appsettings.Local.example.json) to
   `src/HeatBridge.Web/appsettings.Local.json`. This file is git-ignored and overrides all other JSON settings.
2. Use environment variables:
   ```powershell
   $env:HeatBridge__Devices__0__BaseUrl = "http://192.168.1.60"; dotnet run --project src/HeatBridge.Web
   ```
3. Use command-line arguments:
   ```powershell
   dotnet run --project src/HeatBridge.Web -- --HeatBridge:Devices:0:BaseUrl=http://192.168.1.60
   ```

### Network binding

`dotnet run` uses the `http` launch profile and listens on `http://localhost:5171` only.
`AllowedHosts` is also restricted to `localhost`, `127.0.0.1` and `[::1]`. The app has **no
authentication yet**, so do not bind it to `0.0.0.0` or expose it through your router.

## Testing against the real device

1. Make sure the computer can reach the device:
   ```powershell
   Invoke-RestMethod http://192.168.50.215/climate/air_conditioner
   ```
   You should get JSON with `mode`, `target_temperature` and so on.
2. Start the app with `dotnet run --project src/HeatBridge.Web` and open <http://localhost:5171>.
3. Within a few seconds the card shows **Online**, the temperatures, and the modes the device supports.
   The log shows the discovered modes, for example
   `qlima-skane supports modes: Off, HeatCool, Cool, Heat, FanOnly, Dry`.
4. Press **+** once. The value turns orange ("Ny måltemperatur") and is sent after a short pause, so
   repeated presses become one command. The app then reads the state back, and the dashboard shows
   "Måltemperaturen är … °C". The log shows `confirmed target temperature … after N read-back(s)`.
5. Check the device independently:
   ```powershell
   Invoke-RestMethod http://192.168.50.215/climate/air_conditioner | Select-Object mode, target_temperature
   ```
6. Unplug the module or change `BaseUrl` to an unused address to see the **Offline** state. The last
   known values stay visible and the controls are disabled.

## Automated tests

```powershell
dotnet test
```

The tests use mocked HTTP responses (captured from the real device). They cover JSON parsing
(numbers encoded as strings, `nan`, unexpected payloads), request building with invariant culture,
timeouts and unreachable devices, `/events` parsing, temperature limit enforcement, command
confirmation, and serialisation of polling and commands.

## Architecture

```
HeatBridge.slnx
├── src/HeatBridge.Core        Device-agnostic models and application services
├── src/HeatBridge.EspHome     ESPHome web server integration (REST + SSE)
├── src/HeatBridge.Web         Blazor Web App (Interactive Server), hosting and background polling
└── tests/HeatBridge.Tests     xUnit tests with mocked HTTP
```

```
Browser ⇄ SignalR ⇄ ThermostatCard ─┐
                                    ▼
DevicePollingService ──► DeviceRegistry ──► DeviceSession ──► IClimateDevice
   (every 5 s)              (singleton)     (state, lock,         │
                                             confirmation)        ▼
                                                        EspHomeClimateDevice
                                                                  │  IHttpClientFactory
                                                                  ▼
                                                            EspHomeClient ──► http://<device>/
```

- **`IClimateDevice`** (Core) is the protocol boundary. It covers reading state, discovering
  capabilities, and setting the target temperature and mode. It throws
  `DeviceCommunicationException` with a `DeviceErrorKind` (`Timeout`, `Unreachable`, `HttpError`,
  `InvalidResponse`, …). Supporting another kind of device means implementing this interface.
- **`DeviceSession`** (Core) owns the live `DeviceSnapshot` of one device and serialises all
  communication with it:
  - A refresh is skipped while a command is running, and a command waits for a running refresh.
    The ESP never gets overlapping requests, and a stale poll cannot overwrite a command result.
  - Commands are validated against the device limits and supported modes, sent, and then confirmed
    by reading the state back. ESPHome answers `200 OK` even for values it ignores, so the read-back
    is the only real confirmation.
  - Snapshot changes are pushed to subscribers through the `SnapshotChanged` event.
- **`DevicePollingService`** (Web) is a `BackgroundService` that polls all devices with a
  non-overlapping `PeriodicTimer`. There is one poller per server, however many browser tabs are open.
  To switch to live updates, replace it with a listener on `/events` that feeds the same sessions.
  `EspHomeClient.ReadClimateFromEventStreamAsync` already parses that stream.
- **`ThermostatCard`** (Web) keeps pending user input (`_pendingTarget`, `_pendingMode`) separate
  from the device snapshot, so incoming updates never overwrite the user's change. +/− presses are
  debounced into a single command.
- **`EspHomeClient`** gets its `HttpClient` from `IHttpClientFactory`
  (named client `HeatBridge.EspHome`, no proxy, short connect timeout). It enforces per-request
  timeouts and formats every numeric parameter with `CultureInfo.InvariantCulture`.

### ESPHome API used

The two endpoints marked "verified" were tested on the real Qlima before this project started.
The mode endpoint is documented in ESPHome's `web_server` source (`handle_climate_request`).

| Purpose | Request |
| --- | --- |
| Read climate (verified) | `GET /climate/air_conditioner?detail=all` |
| Read outdoor temperature | `GET /sensor/air_conditioner_outdoor_temperature` |
| Set target temperature (verified) | `POST /climate/air_conditioner/set?target_temperature=24.5` |
| Set mode | `POST /climate/air_conditioner/set?mode=HEAT_COOL` |
| Discover supported modes | `GET /events` (first `state` event for `climate-air_conditioner`) |

Notes about this device and firmware:

- Numbers such as `min_temp`, `max_temp`, `current_temperature` and `target_temperature` are encoded
  as JSON strings, while `step` is a number. The parser accepts both, and treats `nan` as "unknown".
- The Midea component reports `OFF, HEAT_COOL, COOL, HEAT, FAN_ONLY, DRY`. There is no `AUTO`, so
  the Swedish "Auto" button sends `HEAT_COOL`.
- On this firmware the REST response does not include the supported modes, but the `/events` stream
  does. Newer ESPHome versions include them with `?detail=all`, which HeatBridge also uses.

## Roadmap

The MVP is structured so these can be added without rewriting the core:

- **Docker on a Linux VPS.** The Web project has no Windows dependencies, and configuration comes
  from environment variables. TLS will be terminated by a reverse proxy. `/healthz` is available for
  container health checks.
- **Multiple heat pumps.** `HeatBridge:Devices` is already a list, and the dashboard renders one card per device.
- **Remote sites over WireGuard.** The cloud server cannot reach private LAN addresses. Each site
  will be connected over WireGuard, or will run a small local agent. Only `BaseUrl` (or the
  `IClimateDevice` implementation) changes.
- **Live updates via SSE** to replace polling (see `DevicePollingService`).
- **Authentication** before the app is ever exposed beyond `localhost`.
- **Temperature history** in a database, fed from `DeviceSession.SnapshotChanged`.
- More controls (fan mode, swing, presets). These are exposed by the device and parsed already.

## License

To be decided by the project owners before publishing.
