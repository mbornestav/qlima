using System.Globalization;
using HeatBridge.Core.Devices;

namespace HeatBridge.Protocol;

/// <summary>
/// Structural validation of every incoming message: identifiers, enum values, string lengths, list sizes
/// and numeric ranges. Business rules (device limits, supported modes) are enforced by the device session.
/// </summary>
public static class ProtocolValidator
{
    public static IReadOnlyList<string> Validate(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var errors = new List<string>();

        switch (message)
        {
            case HelloMessage hello:
                Identifier(errors, "agentId", hello.AgentId);
                Text(errors, "agentVersion", hello.AgentVersion, AgentProtocol.MaxShortTextLength, required: true);
                Text(errors, "hostName", hello.HostName, AgentProtocol.MaxNameLength, required: false);
                if (ListSize(errors, "devices", hello.Devices))
                {
                    foreach (var device in hello.Devices)
                    {
                        if (device is null)
                        {
                            errors.Add("devices contains null.");
                            continue;
                        }

                        Identifier(errors, "devices[].deviceId", device.DeviceId);
                        Text(errors, "devices[].displayName", device.DisplayName, AgentProtocol.MaxNameLength, required: true);
                    }

                    if (hello.Devices.Where(d => d is not null).Select(d => d.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                        != hello.Devices.Count(d => d is not null))
                    {
                        errors.Add("devices contains duplicate ids.");
                    }
                }

                break;

            case WelcomeMessage welcome:
                Identifier(errors, "agentId", welcome.AgentId);
                IdentifierList(errors, "acceptedDevices", welcome.AcceptedDevices);
                IdentifierList(errors, "rejectedDevices", welcome.RejectedDevices);
                if (welcome.HeartbeatIntervalSeconds is < 1 or > 3600)
                {
                    errors.Add("heartbeatIntervalSeconds must be between 1 and 3600.");
                }

                break;

            case DeviceStatusMessage status:
                Identifier(errors, "deviceId", status.DeviceId);
                Snapshot(errors, "snapshot", status.Snapshot);
                break;

            case HeartbeatMessage heartbeat:
                if (heartbeat.Sequence < 0)
                {
                    errors.Add("sequence must not be negative.");
                }

                if (heartbeat.Health is null)
                {
                    errors.Add("health is required.");
                }
                else if (heartbeat.Health.UptimeSeconds < 0 || heartbeat.Health.ReconnectCount < 0 || heartbeat.Health.WorkingSetBytes < 0
                    || heartbeat.Health.LastRoundTripMilliseconds is { } rtt && (!double.IsFinite(rtt) || rtt < 0))
                {
                    errors.Add("health contains invalid values.");
                }

                break;

            case HeartbeatAckMessage ack:
                if (ack.Sequence < 0)
                {
                    errors.Add("sequence must not be negative.");
                }

                break;

            case ClimateCommandMessage command:
                CommandId(errors, command.CommandId);
                Identifier(errors, "deviceId", command.DeviceId);
                if (command.Kind is ClimateCommandKind.SetFanMode or ClimateCommandKind.SetPreset or ClimateCommandKind.SetSwingMode)
                {
                    Text(errors, "option.value", command.Option?.Value, AgentProtocol.MaxShortTextLength, required: true);
                    if (command.Mode is not null || command.TargetTemperature is not null)
                        errors.Add("Option commands cannot set mode or temperature.");
                    if (command.Kind == ClimateCommandKind.SetSwingMode && command.Option?.IsCustom == true)
                        errors.Add("Swing mode cannot be custom.");
                }
                else if (command.Option is not null) errors.Add("option is not allowed for this command.");
                switch (command.Kind)
                {
                    case ClimateCommandKind.SetTargetTemperature:
                        if (command.TargetTemperature is not { } temperature)
                        {
                            errors.Add("targetTemperature is required for SetTargetTemperature.");
                        }
                        else
                        {
                            Temperature(errors, "targetTemperature", temperature);
                        }

                        if (command.Mode is not null)
                        {
                            errors.Add("mode is not allowed for SetTargetTemperature.");
                        }

                        break;
                    case ClimateCommandKind.SetMode:
                        if (command.Mode is not { } mode || mode == ClimateMode.Unknown || !Enum.IsDefined(mode))
                        {
                            errors.Add("a known mode is required for SetMode.");
                        }

                        if (command.TargetTemperature is not null)
                        {
                            errors.Add("targetTemperature is not allowed for SetMode.");
                        }

                        break;
                    case ClimateCommandKind.SetFanMode:
                    case ClimateCommandKind.SetPreset:
                    case ClimateCommandKind.SetSwingMode:
                        break;
                    case ClimateCommandKind.Rediscover:
                        if (command.Mode is not null || command.TargetTemperature is not null)
                            errors.Add("Rediscover cannot set climate values.");
                        break;
                    default:
                        errors.Add("kind is not a supported command.");
                        break;
                }

                break;

            case CommandAckMessage ack:
                CommandId(errors, ack.CommandId);
                OptionalEnum(errors, "errorKind", ack.ErrorKind);
                Text(errors, "detail", ack.Detail, AgentProtocol.MaxDetailLength, required: false);
                if (!ack.Accepted && ack.ErrorKind is null)
                {
                    errors.Add("errorKind is required when a command is not accepted.");
                }

                break;

            case CommandResultMessage result:
                CommandId(errors, result.CommandId);
                Identifier(errors, "deviceId", result.DeviceId);
                OptionalEnum(errors, "errorKind", result.ErrorKind);
                Text(errors, "detail", result.Detail, AgentProtocol.MaxDetailLength, required: false);
                if (!result.Succeeded && result.ErrorKind is null)
                {
                    errors.Add("errorKind is required for a failed command.");
                }

                if (result.Snapshot is not null)
                {
                    Snapshot(errors, "snapshot", result.Snapshot);
                }

                break;

            default:
                errors.Add($"Unsupported message type {message.GetType().Name}.");
                break;
        }

        return errors;
    }

    private static void Snapshot(List<string> errors, string path, DeviceSnapshotDto? snapshot)
    {
        if (snapshot is null)
        {
            errors.Add($"{path} is required.");
            return;
        }

        if (!Enum.IsDefined(snapshot.Status))
        {
            errors.Add($"{path}.status is invalid.");
        }

        if (ListSize(errors, $"{path}.supportedModes", snapshot.SupportedModes)
            && snapshot.SupportedModes.Any(mode => mode == ClimateMode.Unknown || !Enum.IsDefined(mode)))
        {
            errors.Add($"{path}.supportedModes contains an invalid mode.");
        }

        if (ListSize(errors, $"{path}.fanModes", snapshot.FanModes))
        {
            foreach (var fanMode in snapshot.FanModes)
            {
                Text(errors, $"{path}.fanModes[]", fanMode, AgentProtocol.MaxShortTextLength, required: true);
            }
        }

        OptionalTemperature(errors, $"{path}.outdoorTemperature", snapshot.OutdoorTemperature);

        foreach (var list in new[] { snapshot.CustomFanModes, snapshot.Presets, snapshot.CustomPresets, snapshot.SwingModes })
            if (ListSize(errors, path + ".options", list))
                foreach (var value in list) Text(errors, path + ".option", value, AgentProtocol.MaxShortTextLength, true);
        if (ListSize(errors, path + ".entities", snapshot.Entities))
            foreach (var entity in snapshot.Entities)
            {
                if (entity is null) { errors.Add("Null entity."); continue; }
                Text(errors, "entity.id", entity.Id, 100, true);
                Text(errors, "entity.name", entity.Name, 100, true);
                Text(errors, "entity.unit", entity.Unit, 32, false);
                if (entity.Domain is not ("sensor" or "climate" or "switch" or "button")) errors.Add("Invalid entity domain.");
            }
        if (ListSize(errors, path + ".sensors", snapshot.Sensors))
            foreach (var sensor in snapshot.Sensors)
            {
                if (sensor is null) { errors.Add("Null sensor."); continue; }
                Text(errors, "sensor.id", sensor.Id, 100, true);
                Text(errors, "sensor.name", sensor.Name, 100, true);
                Text(errors, "sensor.unit", sensor.Unit, 32, false);
                if (sensor.Value is { } value && !double.IsFinite(value)) errors.Add("Invalid sensor value.");
            }

        if (snapshot.LastError is { } error)
        {
            if (!Enum.IsDefined(error.Kind))
            {
                errors.Add($"{path}.lastError.kind is invalid.");
            }

            Text(errors, $"{path}.lastError.detail", error.Detail, AgentProtocol.MaxDetailLength, required: false);
        }

        if (snapshot.Climate is not { } climate)
        {
            return;
        }

        if (!Enum.IsDefined(climate.Mode))
        {
            errors.Add($"{path}.climate.mode is invalid.");
        }

        Temperature(errors, $"{path}.climate.minTemperature", climate.MinTemperature);
        Temperature(errors, $"{path}.climate.maxTemperature", climate.MaxTemperature);
        OptionalTemperature(errors, $"{path}.climate.currentTemperature", climate.CurrentTemperature);
        OptionalTemperature(errors, $"{path}.climate.targetTemperature", climate.TargetTemperature);

        if (climate.MinTemperature > climate.MaxTemperature)
        {
            errors.Add($"{path}.climate.minTemperature must not exceed maxTemperature.");
        }

        if (!double.IsFinite(climate.TemperatureStep) || climate.TemperatureStep <= 0 || climate.TemperatureStep > 10)
        {
            errors.Add($"{path}.climate.temperatureStep must be in (0, 10].");
        }

        Text(errors, $"{path}.climate.rawMode", climate.RawMode, AgentProtocol.MaxShortTextLength, required: false);
        Text(errors, $"{path}.climate.fanMode", climate.FanMode, AgentProtocol.MaxShortTextLength, required: false);
        Text(errors, $"{path}.climate.preset", climate.Preset, AgentProtocol.MaxShortTextLength, required: false);
        Text(errors, $"{path}.climate.swingMode", climate.SwingMode, AgentProtocol.MaxShortTextLength, required: false);
    }

    private static void CommandId(List<string> errors, Guid id)
    {
        if (id == Guid.Empty)
        {
            errors.Add("commandId must not be empty.");
        }
    }

    private static void Identifier(List<string> errors, string path, string? value)
    {
        if (!AgentProtocol.IsValidIdentifier(value))
        {
            errors.Add($"{path} is not a valid identifier.");
        }
    }

    private static void IdentifierList(List<string> errors, string path, IReadOnlyList<string>? values)
    {
        if (ListSize(errors, path, values))
        {
            foreach (var value in values!)
            {
                Identifier(errors, $"{path}[]", value);
            }
        }
    }

    private static bool ListSize<T>(List<string> errors, string path, IReadOnlyList<T>? list)
    {
        if (list is null)
        {
            errors.Add($"{path} is required.");
            return false;
        }

        if (list.Count > AgentProtocol.MaxListLength)
        {
            errors.Add($"{path} has more than {AgentProtocol.MaxListLength} entries.");
            return false;
        }

        return true;
    }

    private static void Text(List<string> errors, string path, string? value, int maxLength, bool required)
    {
        if (value is null)
        {
            if (required)
            {
                errors.Add($"{path} is required.");
            }

            return;
        }

        if ((required && string.IsNullOrWhiteSpace(value)) || value.Length > maxLength || value.Any(char.IsControl))
        {
            errors.Add($"{path} is empty, too long or contains control characters.");
        }
    }

    private static void OptionalEnum<T>(List<string> errors, string path, T? value)
        where T : struct, Enum
    {
        if (value is { } v && !Enum.IsDefined(v))
        {
            errors.Add($"{path} is invalid.");
        }
    }

    private static void OptionalTemperature(List<string> errors, string path, double? value)
    {
        if (value is { } v)
        {
            Temperature(errors, path, v);
        }
    }

    private static void Temperature(List<string> errors, string path, double value)
    {
        if (!double.IsFinite(value) || value < AgentProtocol.MinTemperature || value > AgentProtocol.MaxTemperature)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture,
                $"{path} must be a number between {AgentProtocol.MinTemperature} and {AgentProtocol.MaxTemperature}."));
        }
    }
}
