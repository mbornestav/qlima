namespace HeatBridge.EspHome;

/// <summary>
/// Configuration for one ESPHome device, bound from an item in <c>HeatBridge:Devices</c>.
/// </summary>
public sealed class EspHomeDeviceOptions
{
    /// <summary>Stable identifier, e.g. <c>qlima-skane</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name shown in the UI, e.g. <c>Qlima Skåne</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base URL of the ESPHome web server, e.g. <c>http://192.168.50.215</c>.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// The climate entity as it appears in the REST URL, i.e. <c>/climate/{ClimateEntity}</c>.
    /// For the Midea UART component this is typically <c>air_conditioner</c>.
    /// </summary>
    public string ClimateEntity { get; set; } = "air_conditioner";

    /// <summary>Optional sensor entity for outdoor temperature, read from <c>/sensor/{OutdoorTemperatureSensor}</c>.</summary>
    public string? OutdoorTemperatureSensor { get; set; }

    /// <summary>Timeout for a single REST request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to listen to the <c>/events</c> stream when discovering capabilities.</summary>
    public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Optional explicit list of supported ESPHome modes (e.g. <c>OFF</c>, <c>HEAT</c>). When empty,
    /// the modes are discovered from the device. Use only if discovery does not work for your firmware.
    /// </summary>
    public IList<string> SupportedModes { get; set; } = [];

    /// <summary>Returns a list of configuration problems, empty if the options are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var label = string.IsNullOrWhiteSpace(Id) ? "(device without Id)" : $"'{Id}'";

        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("Id is required.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add($"{label}: Name is required.");
        }

        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || (BaseUrl.Scheme != Uri.UriSchemeHttp && BaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{label}: BaseUrl must be an absolute http(s) URL, e.g. http://192.168.1.50.");
        }

        if (string.IsNullOrWhiteSpace(ClimateEntity))
        {
            errors.Add($"{label}: ClimateEntity is required.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            errors.Add($"{label}: RequestTimeout must be positive.");
        }

        if (DiscoveryTimeout <= TimeSpan.Zero)
        {
            errors.Add($"{label}: DiscoveryTimeout must be positive.");
        }

        foreach (var mode in SupportedModes)
        {
            if (!EspHomeClimateModes.TryParse(mode, out _))
            {
                errors.Add($"{label}: Unknown mode '{mode}' in SupportedModes.");
            }
        }

        return errors;
    }
}
