using GeoLocationFilter.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GeoLocationFilter
{
    /// <summary>
    /// Reports Degraded (not Unhealthy) when the MaxMind database is missing,
    /// because the service can still resolve countries via the fallback API.
    /// </summary>
    public class MaxMindDatabaseHealthCheck(IGeoLocationService locationService) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(locationService.IsReady
                ? HealthCheckResult.Healthy("MaxMind database loaded")
                : HealthCheckResult.Degraded("MaxMind database not loaded — falling back to external geo API"));
    }
}
