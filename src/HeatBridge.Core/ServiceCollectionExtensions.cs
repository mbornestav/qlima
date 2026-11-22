using HeatBridge.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HeatBridge.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the device registry and its dependencies. Device integrations (e.g. ESPHome)
    /// register their <see cref="Devices.IClimateDevice"/> instances separately.
    /// Configure <see cref="HeatBridgeOptions"/> with the options pattern.
    /// </summary>
    public static IServiceCollection AddHeatBridgeCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<HeatBridgeOptions>().ValidateDataAnnotations().ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DeviceRegistry>();
        return services;
    }
}
