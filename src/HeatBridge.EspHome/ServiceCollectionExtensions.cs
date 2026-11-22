using HeatBridge.Core.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HeatBridge.EspHome;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="IClimateDevice"/> per entry in <paramref name="devicesSection"/>
    /// (normally <c>HeatBridge:Devices</c>) and the HTTP client used to talk to them.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device configuration is invalid.</exception>
    public static IServiceCollection AddEspHomeDevices(this IServiceCollection services, IConfiguration devicesSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(devicesSection);

        var devices = devicesSection.Get<List<EspHomeDeviceOptions>>() ?? [];
        var errors = devices.SelectMany(device => device.Validate()).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid ESPHome device configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        services.AddHttpClient(EspHomeClient.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("HeatBridge/0.1");

                // Per-request timeouts are enforced by EspHomeClient; this is only a safety net.
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Devices live on the local network (or behind a VPN later); never route them through a proxy.
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });

        foreach (var device in devices)
        {
            services.AddSingleton<IClimateDevice>(provider => new EspHomeClimateDevice(
                device,
                provider.GetRequiredService<IHttpClientFactory>(),
                provider.GetRequiredService<ILogger<EspHomeClimateDevice>>()));
        }

        return services;
    }
}
