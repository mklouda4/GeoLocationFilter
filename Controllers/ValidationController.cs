using GeoLocationFilter.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Prometheus;
using System.Net;

namespace GeoLocationFilter.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ValidationController(
        IConfiguration configuration,
        IOptions<SecurityOptions> options,
        ILogger<ValidationController> logger,
        IGeoLocationService locationService,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache) : ControllerBase
    {
        private readonly string fallbackApiUri = configuration["FallbackApi"] ?? "https://get.geojs.io/v1/ip/country/{0}";

        private readonly SecurityOptions _options = options.Value;
        private static readonly TimeSpan CacheTimeout = TimeSpan.FromHours(24);

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

            var clientIp = GetClientIpAddress();
            var forwardedHost = Request.Headers["X-Forwarded-Host"].FirstOrDefault();
            var forwardedUri = Request.Headers["X-Forwarded-Uri"].FirstOrDefault();
            var userAgent = Request.Headers.UserAgent.FirstOrDefault();

            logger.LogInformation($"Validating request: IP={clientIp}, Host={forwardedHost}, URI={forwardedUri}");

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
                    logger.LogWarning($"Request blocked: IP={clientIp}, Country={blockResult.CountryCode}, Reason={blockResult.Reason}");
                    return StatusCode(403, new { message = "Access denied", country = blockResult.CountryCode, reason = blockResult.Reason, ipAddress = clientIp });
                }

                logger.LogDebug($"Request allowed: IP={clientIp}, Country={blockResult.CountryCode}");
                return Ok(new { message = "Access granted", country = blockResult.CountryCode, ipAddress = clientIp });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error during request validation for IP={clientIp}");

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
            var ipAddress = GetClientIpAddress();
            return Ok(new { ipAddress });
        }


        [HttpGet("/check")]
        public async Task<IActionResult> CheckIp([FromQuery] string ipAddress)
        {
            try
            {
                var countryCode = await GetCountryCodeForIp(ipAddress);
                return Ok(new { ipAddress, countryCode });
            }
            catch 
            {
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
            if (string.IsNullOrEmpty(blockedCountries?.Trim()) && 
                string.IsNullOrEmpty(allowedCountries?.Trim()) &&
                string.IsNullOrEmpty(localIps?.Trim()) &&
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

                BlockedCountries = blockedCountries != null ? (ParseCountryList(blockedCountries) ?? []) : _options.BlockedCountries,
                AllowedCountries = allowedCountries != null ? (ParseCountryList(allowedCountries) ?? []) : _options.AllowedCountries,
                LocalIps = localIps != null ? (ParseIpList(localIps) ?? []) : _options.LocalIps
            };

            logger.LogDebug($"Using query-based security options: BlockUnknown={options.BlockUnknown}, " +
                           $"IgnoreLocalIps={options.IgnoreLocalIps}, " +
                           $"BlockedCountries=[{string.Join(",", options.BlockedCountries)}], " +
                           $"AllowedCountries=[{string.Join(",", options.AllowedCountries)}], " +
                           $"LocalIps=[{string.Join(",", options.LocalIps)}]");

            return options;
        }
        private static List<string>? ParseCountryList(string? countries)
        {
            if (string.IsNullOrWhiteSpace(countries))
                return null;

            return [.. countries
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim().ToUpperInvariant())
                .Where(c => !string.IsNullOrEmpty(c))];
        }

        private static List<string>? ParseIpList(string? ips)
        {
            if (string.IsNullOrWhiteSpace(ips))
                return null;

            return [.. ips
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(ip => ip.Trim())
                .Where(ip => !string.IsNullOrEmpty(ip))];
        }
        private string? GetClientIpAddress()
        {
            var ipSources = new[]
            {
                Request.Headers["X-Real-IP"].FirstOrDefault(),
                Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim(),
                Request.Headers["CF-Connecting-IP"].FirstOrDefault(),
                Request.HttpContext.Connection.RemoteIpAddress?.ToString()
            };

            return ipSources.FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip));
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

            if (securityOptions.BlockedCountries.Count > 0 && securityOptions.BlockedCountries.Contains(countryCode))
            {
                return new BlockResult(true, countryCode, "in-blocklist", host, uri, userAgent);
            }

            if (securityOptions.AllowedCountries.Count > 0 && !securityOptions.AllowedCountries.Contains(countryCode))
            {
                return new BlockResult(true, countryCode, "not-in-allowlist", host, uri, userAgent);
            }

            return new BlockResult(false, countryCode, "geo-allowed", host, uri, userAgent);
        }

        private static bool IsLocalIpAddress(string ipAddress, SecurityOptions securityOptions)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
                return false;

            foreach (var localRange in securityOptions.LocalIps)
            {
                if (IsIpInRange(ip, localRange))
                    return true;
            }

            return false;
        }

        private static bool IsIpInRange(IPAddress ip, string cidrRange)
        {
            try
            {
                var parts = cidrRange.Split('/');
                if (parts.Length != 2) return false;

                var networkAddress = IPAddress.Parse(parts[0]);
                var prefixLength = int.Parse(parts[1]);

                var networkBytes = networkAddress.GetAddressBytes();
                var ipBytes = ip.GetAddressBytes();

                if (networkBytes.Length != ipBytes.Length) return false;

                var bytesToCheck = prefixLength / 8;
                var bitsToCheck = prefixLength % 8;

                for (int i = 0; i < bytesToCheck; i++)
                {
                    if (networkBytes[i] != ipBytes[i]) return false;
                }

                if (bitsToCheck > 0 && bytesToCheck < networkBytes.Length)
                {
                    var mask = (byte)(0xFF << (8 - bitsToCheck));
                    if ((networkBytes[bytesToCheck] & mask) != (ipBytes[bytesToCheck] & mask))
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string?> GetCountryCodeForIp(string ipAddress)
        {
            var cacheKey = $"geo_{ipAddress}";
            if (cache.TryGetValue(cacheKey, out string? cachedCountry))
            {
                logger.LogDebug($"Country code for IP {ipAddress} found in cache: {cachedCountry}");
                CacheHits.Inc();
                return cachedCountry;
            }

            CacheMisses.Inc();

            var countryCode = await MaxMind(ipAddress);
            countryCode ??= await Fallback(ipAddress);

            if (countryCode != null)
            {
                cache.Set(cacheKey, countryCode, CacheTimeout);
                logger.LogDebug($"Cached country code for IP {ipAddress}: {countryCode}");
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

                    logger.LogDebug($"Geo API returned country code for IP {ipAddress}: {countryCode}");
                    GeoApiCalls.WithLabels("success").Inc();
                    return countryCode.ToUpperInvariant();
                }

                logger.LogWarning($"Geo API returned non-success status for IP {ipAddress}: {response.StatusCode}");
                GeoApiCalls.WithLabels("geojs", "http_error").Inc();
                return null;
            }
            catch (TaskCanceledException)
            {
                logger.LogWarning($"Geo API timeout for IP {ipAddress}");
                GeoApiCalls.WithLabels("geojs", "timeout").Inc();
                return null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error calling geo API for IP {ipAddress}");
                GeoApiCalls.WithLabels("geojs", "exception").Inc();
                return null;
            }
        }
    }
}