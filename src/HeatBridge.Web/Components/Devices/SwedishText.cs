using System.Globalization;
using HeatBridge.Core.Devices;

namespace HeatBridge.Web.Components.Devices;

/// <summary>Swedish UI texts and formatting. All user-facing strings live here.</summary>
internal static class SwedishText
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("sv-SE");

    public static string Temperature(double? value) =>
        value is { } v ? v.ToString("0.0", Culture) : "–";

    public static string TemperatureWithUnit(double? value) =>
        value is null ? "–" : $"{Temperature(value)} °C";

    public static string ModeLabel(ClimateMode mode) => mode switch
    {
        ClimateMode.Off => "Av",
        ClimateMode.Heat => "Värme",
        ClimateMode.Cool => "Kyla",
        ClimateMode.HeatCool => "Auto",
        ClimateMode.Auto => "Auto",
        ClimateMode.FanOnly => "Fläkt",
        ClimateMode.Dry => "Avfuktning",
        _ => "Okänt",
    };

    public static string ModeDescription(ClimateMode mode) => mode switch
    {
        ClimateMode.Off => "Värmepumpen är avstängd",
        ClimateMode.Heat => "Värmer till måltemperaturen",
        ClimateMode.Cool => "Kyler till måltemperaturen",
        ClimateMode.HeatCool => "Värmer eller kyler automatiskt",
        ClimateMode.Auto => "Automatisk drift",
        ClimateMode.FanOnly => "Endast fläkt, ingen värme eller kyla",
        ClimateMode.Dry => "Avfuktar luften",
        _ => "Okänt driftläge",
    };

    public static string ModeIcon(ClimateMode mode) => mode switch
    {
        ClimateMode.Off => "power",
        ClimateMode.Heat => "flame",
        ClimateMode.Cool => "snowflake",
        ClimateMode.HeatCool or ClimateMode.Auto => "auto",
        ClimateMode.FanOnly => "fan",
        ClimateMode.Dry => "droplet",
        _ => "help",
    };

    /// <summary>CSS-friendly mode key used for theming (<c>data-mode</c> attribute).</summary>
    public static string ModeKey(ClimateMode mode) => mode switch
    {
        ClimateMode.Off => "off",
        ClimateMode.Heat => "heat",
        ClimateMode.Cool => "cool",
        ClimateMode.HeatCool or ClimateMode.Auto => "auto",
        ClimateMode.FanOnly => "fan",
        ClimateMode.Dry => "dry",
        _ => "unknown",
    };

    public static string FanModeLabel(string? fanMode) => fanMode?.ToUpperInvariant() switch
    {
        null or "" => "–",
        "AUTO" => "Auto",
        "LOW" => "Låg",
        "MEDIUM" or "MIDDLE" => "Medel",
        "HIGH" => "Hög",
        "SILENT" or "QUIET" => "Tyst",
        "TURBO" => "Turbo",
        "ON" => "På",
        "OFF" => "Av",
        _ => fanMode,
    };

    public static string ErrorMessage(DeviceErrorKind kind) => kind switch
    {
        DeviceErrorKind.Timeout => "Värmepumpen svarade inte i tid.",
        DeviceErrorKind.Unreachable => "Kunde inte nå värmepumpen. Kontrollera att den har ström och är ansluten till nätverket.",
        DeviceErrorKind.HttpError => "Värmepumpen svarade med ett fel.",
        DeviceErrorKind.InvalidResponse => "Värmepumpen skickade ett oväntat svar.",
        DeviceErrorKind.NotConfirmed => "Värmepumpen bekräftade inte ändringen. Försök igen.",
        DeviceErrorKind.InvalidRequest => "Värdet stöds inte av värmepumpen.",
        DeviceErrorKind.AgentUnavailable => "HeatBridge-agenten på platsen är inte ansluten.",
        _ => "Ett okänt fel inträffade.",
    };

    public static string Time(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return "–";
        }

        var local = timestamp.Value.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm:ss", Culture)
            : local.ToString("d MMM HH:mm", Culture);
    }
}
