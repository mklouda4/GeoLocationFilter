using GeoLocationFilter.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Prometheus;
using System.Net;

namespace GeoLocationFilter.Controllers
{
    [ApiController]
    [Route("[controller]")]
    [EnableRateLimiting("validation")]
    public class ValidationController(
        IConfiguration configuration,
        IOptions<SecurityOptions> options,
        ILogger<ValidationController> logger,
        IGeoLocationService locationService,
        IClientIpResolver clientIpResolver,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache) : ControllerBase
    {
        private readonly string fallbackApiUri = configuration["FallbackApi"] ?? "https://get.geojs.io/v1/ip/country/{0}";

        private readonly SecurityOptions _options = options.Value;
        private static readonly TimeSpan CacheTimeout = TimeSpan.FromHours(24);
        private static readonly TimeSpan NegativeCacheTimeout = TimeSpan.FromMinutes(5);

        private static readonly Counter RequestsTotal = Metrics
            .CreateCounter("geoguard_requests_total", "Total validation requests", "result", "country", "reason");

        private static readonly Histogram RequestDuration = Metrics
            .CreateHistogram("geoguard_request_duration_seconds", "Request processing time");

        private static readonly Counter CacheHits = Metrics
            .CreateCounter("geoguard_cache_hits_total", "Cache hits for geo lookups");

        private static readonly Counter CacheMisses = Metrics
            .CreateCounter("geoguard_cache_misses_total", "Cache misses for geo lookups");

        private static readonly Counter GeoApiCalls = Metrics
            .CreateCounter("geoguard_geo_api_calls_total", "External geo API calls", "result");

        [HttpGet("/")]
        [HttpGet("/validate")]
        public async Task<IActionResult> ValidateRequest(
            [FromQuery] string? blockedCountries = null,
            [FromQuery] string? allowedCountries = null,
            [FromQuery] bool? blockUnknown = null,
            [FromQuery] bool? ignoreLocalIps = null,
            [FromQuery] string? localIps = null)
        {
            using var timer = RequestDuration.NewTimer();

            var clientIp = clientIpResolver.GetClientIp(HttpContext);
            var forwardedHost = Request.Headers["X-Forwarded-Host"].FirstOrDefault();
            var forwardedUri = Request.Headers["X-Forwarded-Uri"].FirstOrDefault();
            var userAgent = Request.Headers.UserAgent.FirstOrDefault();

            logger.LogInformation("Validating request: IP={ClientIp}, Host={ForwardedHost}, URI={ForwardedUri}",
                clientIp, forwardedHost, forwardedUri);

            var securityOptions = CreateSecurityOptionsFromQuery(
                blockedCountries, allowedCountries, blockUnknown, ignoreLocalIps, localIps);

            try
            {
                var blockResult = await ShouldBlockRequest(clientIp, forwardedHost, forwardedUri, userAgent, securityOptions);

                Response.Headers.AddOrReplace("X-GeoFilter-Access", blockResult.IsBlocked ? "blocked" : "allowed");
                Response.Headers.AddOrReplace("X-GeoFilter-Country", blockResult.CountryCode ?? "unknown");
                Response.Headers.AddOrReplace("X-GeoFilter-Reason", blockResult.Reason ?? "geo-check");

                RequestsTotal.WithLabels(
                    blockResult.IsBlocked ? "blocked" : "allowed",
                    blockResult.CountryCode ?? "unknown",
                    blockResult.Reason ?? "unknown"
                ).Inc();

                if (blockResult.IsBlocked)
                {
                    logger.LogWarning("Request blocked: IP={ClientIp}, Country={CountryCode}, Reason={Reason}",
                        clientIp, blockResult.CountryCode, blockResult.Reason);
                    return StatusCode(403, new { message = "Access denied", country = blockResult.CountryCode, reason = blockResult.Reason, ipAddress = clientIp });
                }

                logger.LogDebug("Request allowed: IP={ClientIp}, Country={CountryCode}", clientIp, blockResult.CountryCode);
                return Ok(new { message = "Access granted", country = blockResult.CountryCode, ipAddress = clientIp });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during request validation for IP={ClientIp}", clientIp);

                RequestsTotal.WithLabels("error", "unknown", "exception").Inc();

                var allowOnError = !securityOptions.BlockUnknown;
                Response.Headers.AddOrReplace("X-GeoFilter-Access", allowOnError ? "allowed" : "blocked");
                Response.Headers.AddOrReplace("X-GeoFilter-Country", "error");
                Response.Headers.AddOrReplace("X-GeoFilter-Reason", "system-error");

                return allowOnError ? Ok() : StatusCode(403);
            }
        }

        [HttpGet("/ip")]
        public IActionResult IpRequest()
        {
            var ipAddress = clientIpResolver.GetClientIp(HttpContext);
            return Ok(new { ipAddress });
        }

        [HttpGet("/check")]
        public async Task<IActionResult> CheckIp([FromQuery] string ipAddress)
        {
            if (!IPAddress.TryParse(ipAddress, out _))
            {
                return BadRequest(new { error = "Invalid IP address", ipAddress });
            }

            try
            {
                var countryCode = await GetCountryCodeForIp(ipAddress);
                return Ok(new { ipAddress, countryCode });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking country for IP={IpAddress}", ipAddress);
                return Ok(new { ipAddress, countryCode = "NA", status = "Error" });
            }
        }

        private SecurityOptions CreateSecurityOptionsFromQuery(
            string? blockedCountries,
            string? allowedCountries,
            bool? blockUnknown,
            bool? ignoreLocalIps,
            string? localIps)
        {
            if (blockedCountries == null &&
                allowedCountries == null &&
                localIps == null &&
                blockUnknown == null &&
                ignoreLocalIps == null
            )
            {
                return _options;
            }

            var options = new SecurityOptions
            {
                BlockUnknown = blockUnknown ?? _options.BlockUnknown,
                IgnoreLocalIps = ignoreLocalIps ?? _options.IgnoreLocalIps,

                BlockedCountries = blockedCountries != null ? IpUtils.ParseCountryList(blockedCountries) : _options.BlockedCountries,
                AllowedCountries = allowedCountries != null ? IpUtils.ParseCountryList(allowedCountries) : _options.AllowedCountries,
                LocalIps = localIps != null ? IpUtils.ParseList(localIps) : _options.LocalIps,
                TrustedProxies = _options.TrustedProxies
            };

            logger.LogDebug("Using query-based security options: BlockUnknown={BlockUnknown}, " +
                            "IgnoreLocalIps={IgnoreLocalIps}, BlockedCountries={BlockedCountries}, " +
                            "AllowedCountries={AllowedCountries}, LocalIps={LocalIps}",
                options.BlockUnknown, options.IgnoreLocalIps,
                string.Join(",", options.BlockedCountries),
                string.Join(",", options.AllowedCountries),
                string.Join(",", options.LocalIps));

            return options;
        }

        private async Task<BlockResult> ShouldBlockRequest(string? clientIp, string? host, string? uri, string? userAgent, SecurityOptions securityOptions)
        {
            if (string.IsNullOrWhiteSpace(clientIp))
            {
                return new BlockResult(securityOptions.BlockUnknown, null, "no-ip", host, uri, userAgent);
            }

            if (securityOptions.IgnoreLocalIps && IsLocalIpAddress(clientIp, securityOptions))
            {
                return new BlockResult(false, "LOCAL", "local-ip", host, uri, userAgent);
            }

            var countryCode = await GetCountryCodeForIp(clientIp);
            if (string.IsNullOrEmpty(countryCode))
            {
                return new BlockResult(securityOptions.BlockUnknown, "UNKNOWN", "unknown-country", host, uri, userAgent);
            }

            if (securityOptions.BlockedCountries.Count > 0 && securityOptions.BlockedCountries.Contains(countryCode, StringComparer.OrdinalIgnoreCase))
            {
                return new BlockResult(true, countryCode, "in-blocklist", host, uri, userAgent);
            }

            if (securityOptions.AllowedCountries.Count > 0 && !securityOptions.AllowedCountries.Contains(countryCode, StringComparer.OrdinalIgnoreCase))
            {
                return new BlockResult(true, countryCode, "not-in-allowlist", host, uri, userAgent);
            }

            return new BlockResult(false, countryCode, "geo-allowed", host, uri, userAgent);
        }

        private static bool IsLocalIpAddress(string ipAddress, SecurityOptions securityOptions)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
                return false;

            return IpUtils.IsInAnyRange(ip, securityOptions.LocalIps);
        }

        private async Task<string?> GetCountryCodeForIp(string ipAddress)
        {
            var cacheKey = $"geo_{ipAddress}";
            if (cache.TryGetValue(cacheKey, out string? cachedCountry))
            {
                logger.LogDebug("Country code for IP {IpAddress} found in cache: {CountryCode}", ipAddress, cachedCountry);
                CacheHits.Inc();
                return cachedCountry;
            }

            CacheMisses.Inc();

            var countryCode = await MaxMind(ipAddress);
            countryCode ??= await Fallback(ipAddress);

            if (countryCode != null)
            {
                cache.Set(cacheKey, countryCode, CacheTimeout);
                logger.LogDebug("Cached country code for IP {IpAddress}: {CountryCode}", ipAddress, countryCode);
            }
            else
            {
                // Negative cache so repeated requests from unresolvable IPs don't hammer the fallback API
                cache.Set(cacheKey, (string?)null, NegativeCacheTimeout);
            }
            return countryCode;
        }

        private Task<string?> MaxMind(string ipAddress)
            => locationService.GetCountryCodeAsync(ipAddress);

        private async Task<string?> Fallback(string ipAddress)
        {
            try
            {
                if (!fallbackApiUri.Contains("{0}"))
                {
                    logger.LogError("FallbackApi URL must contain {{0}} placeholder for IP address");
                    return null;
                }

                using var httpClient = httpClientFactory.CreateClient("GeoApi");

                var uri = string.Format(fallbackApiUri, ipAddress);

                var response = await httpClient.GetAsync(uri);
                if (response.IsSuccessStatusCode)
                {
                    var countryCode = (await response.Content.ReadAsStringAsync())?.Trim();

                    if (string.IsNullOrEmpty(countryCode) || countryCode.Equals("nil", StringComparison.OrdinalIgnoreCase))
                    {
                        GeoApiCalls.WithLabels("empty").Inc();
                        return null;
                    }

                    logger.LogDebug("Geo API returned country code for IP {IpAddress}: {CountryCode}", ipAddress, countryCode);
                    GeoApiCalls.WithLabels("success").Inc();
                    return countryCode.ToUpperInvariant();
                }

                logger.LogWarning("Geo API returned non-success status for IP {IpAddress}: {StatusCode}", ipAddress, response.StatusCode);
                GeoApiCalls.WithLabels("http_error").Inc();
                return null;
            }
            catch (TaskCanceledException)
            {
                logger.LogWarning("Geo API timeout for IP {IpAddress}", ipAddress);
                GeoApiCalls.WithLabels("timeout").Inc();
                return null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error calling geo API for IP {IpAddress}", ipAddress);
                GeoApiCalls.WithLabels("exception").Inc();
                return null;
            }
        }
    }
}
