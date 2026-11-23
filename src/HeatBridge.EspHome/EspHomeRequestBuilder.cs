using System.Globalization;
using System.Text;

namespace HeatBridge.EspHome;

/// <summary>
/// Builds relative request URIs for the ESPHome web server REST API.
/// URL scheme: <c>/&lt;domain&gt;/&lt;entity&gt;[/&lt;action&gt;?&lt;param&gt;=&lt;value&gt;]</c>.
/// </summary>
public static class EspHomeRequestBuilder
{
    /// <summary><c>GET /climate/{entity}?detail=all</c>. Newer firmware only includes limits and
    /// supported modes with <c>detail=all</c>; older firmware ignores the parameter.</summary>
    public static Uri ClimateState(string entity) =>
        new($"climate/{Escape(entity)}?detail=all", UriKind.Relative);

    /// <summary><c>GET /sensor/{entity}</c>.</summary>
    public static Uri SensorState(string entity) =>
        new($"sensor/{Escape(entity)}", UriKind.Relative);

    /// <summary><c>GET /events</c> (server-sent events).</summary>
    public static Uri Events() => new("events", UriKind.Relative);

    /// <summary><c>POST /climate/{entity}/set?...</c>.</summary>
    public static Uri ClimateSet(string entity, EspHomeClimateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.IsEmpty)
        {
            throw new ArgumentException("A climate command must set at least one value.", nameof(command));
        }

        var query = new StringBuilder();
        if (command.Mode is { } mode)
        {
            Append(query, "mode", EspHomeClimateModes.ToEspHome(mode));
        }

        if (command.TargetTemperature is { } temperature)
        {
            Append(query, "target_temperature", FormatTemperature(temperature));
        }

        // ESPHome's REST setter resolves both standard and custom values through these parameters.
        if (command.FanMode is { } fan) Append(query, "fan_mode", fan.Value);
        if (command.Preset is { } preset) Append(query, "preset", preset.Value);
        if (command.SwingMode is { } swing) Append(query, "swing_mode", swing);
        return new Uri($"climate/{Escape(entity)}/set?{query}", UriKind.Relative);
    }

    /// <summary>
    /// Formats a temperature with invariant culture so that a Swedish locale never produces "24,5".
    /// </summary>
    public static string FormatTemperature(double temperature)
    {
        if (!double.IsFinite(temperature))
        {
            throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be a finite number.");
        }

        return temperature.ToString("0.0##", CultureInfo.InvariantCulture);
    }

    private static void Append(StringBuilder query, string name, string value)
    {
        if (query.Length > 0)
        {
            query.Append('&');
        }

        query.Append(name).Append('=').Append(Uri.EscapeDataString(value));
    }

    private static string Escape(string entity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        return Uri.EscapeDataString(entity.Trim());
    }
}
