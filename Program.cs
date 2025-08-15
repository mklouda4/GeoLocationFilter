
using GeoLocationFilter.Services;
using Microsoft.AspNetCore.RateLimiting;
using Prometheus;
using System.Threading.RateLimiting;

namespace GeoLocationFilter
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            ConfigureEnvironmentOverrides(builder.Configuration);

            // Add services to the container.
            _ = builder.Services.Configure<SecurityOptions>(
                builder.Configuration.GetSection(SecurityOptions.SectionName));

            builder.Services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    var ipAddress = GetClientIpAddress(context);

                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: ipAddress ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = builder.Configuration.GetValue<int>("RateLimit:PermitLimit", 100),
                            Window = TimeSpan.FromMinutes(builder.Configuration.GetValue<int>("RateLimit:WindowMinutes", 1)),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = builder.Configuration.GetValue<int>("RateLimit:QueueLimit", 0)
                        });
                });

                options.AddFixedWindowLimiter("validation", options =>
                {
                    options.PermitLimit = builder.Configuration.GetValue<int>("RateLimit:Validation:PermitLimit", 100);
                    options.Window = TimeSpan.FromMinutes(builder.Configuration.GetValue<int>("RateLimit:Validation:WindowMinutes", 1));
                    options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                    options.QueueLimit = 0;
                });

                options.AddFixedWindowLimiter("health", options =>
                {
                    options.PermitLimit = builder.Configuration.GetValue<int>("RateLimit:Health:PermitLimit", 10);
                    options.Window = TimeSpan.FromMinutes(builder.Configuration.GetValue<int>("RateLimit:Health:WindowMinutes", 1));
                    options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                    options.QueueLimit = 0;
                });

                options.AddSlidingWindowLimiter("burst", options =>
                {
                    options.PermitLimit = builder.Configuration.GetValue<int>("RateLimit:Burst:PermitLimit", 200);
                    options.Window = TimeSpan.FromMinutes(builder.Configuration.GetValue<int>("RateLimit:Burst:WindowMinutes", 1));
                    options.SegmentsPerWindow = 5;
                    options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                    options.QueueLimit = 0;
                });

                options.OnRejected = async (context, token) =>
                {
                    var policy = "unknown";
                    if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName is string policyName)
                    {
                        policy = policyName;
                    }
                    else if (context.HttpContext.Request.Path.StartsWithSegments("/validate"))
                    {
                        policy = "validation";
                    }
                    else if (context.HttpContext.Request.Path.StartsWithSegments("/health"))
                    {
                        policy = "health";
                    }
                    else
                    {
                        policy = "global";
                    }

                    // Increment rate limit metric
                    Metrics.CreateCounter("geoguard_rate_limit_hits_total", "Rate limit hits by policy", "policy")
                        .WithLabels(policy).Inc();

                    context.HttpContext.Response.StatusCode = 429;

                    TimeSpan retryAfter = TimeSpan.Zero;
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterTmp))
                    {
                        retryAfter = retryAfterTmp;
                        context.HttpContext.Response.Headers.RetryAfter = retryAfter.TotalSeconds.ToString();
                    }

                    context.HttpContext.Response.Headers.AddOrReplace("X-RateLimit-Limit", context.Lease.TryGetMetadata(MetadataName.ReasonPhrase, out var limit) ? limit.ToString() : "unknown");
                    context.HttpContext.Response.Headers.AddOrReplace("X-RateLimit-Remaining", "0");
                    context.HttpContext.Response.Headers.AddOrReplace("X-RateLimit-Reset", DateTimeOffset.UtcNow.Add(retryAfter).ToUnixTimeSeconds().ToString());

                    await context.HttpContext.Response.WriteAsync(
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            error = "Rate limit exceeded",
                            retryAfter = retryAfter.TotalSeconds,
                            policy = policy
                        }),
                        token);
                };
            });

            builder.Services.AddSingleton<IGeoLocationService, MaxMindGeoLocationService>();
            builder.Services.AddMetrics();
            builder.Services.AddHealthChecks();
            builder.Services.AddMemoryCache();
            builder.Services.AddHttpClient();
            builder.Services.AddHttpClient("GeoApi", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.Add("User-Agent", "GeoLocationFilterApi/1.0");
            });

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.MapHealthChecks("/health");
            app.UseHttpMetrics();
            app.MapMetrics();
            app.UseHttpsRedirection();
            app.UseAuthorization();
            app.MapControllers();

            app.Run();
        }

        private static string GetClientIpAddress(HttpContext context)
        {
            var ipSources = new[]
            {
                context.Request.Headers["X-Real-IP"].FirstOrDefault(),
                context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim(),
                context.Request.Headers["CF-Connecting-IP"].FirstOrDefault(),
                context.Connection.RemoteIpAddress?.ToString()
            };

            return ipSources.FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip)) ?? "unknown";
        }

        /// <summary>
        /// Configures environment variable overrides for Docker deployment
        /// </summary>
        /// <param name="configuration">Configuration builder</param>
        private static void ConfigureEnvironmentOverrides(IConfiguration configuration)
        {
            var environmentMappings = new Dictionary<string, string>
            {
                ["DB_PATH"] = $"DbPath",
                ["BLOCK_UNKNOWN"] = $"{SecurityOptions.SectionName}:{nameof(SecurityOptions.BlockUnknown)}",
                ["IGNORE_LOCAL_IPS"] = $"{SecurityOptions.SectionName}:{nameof(SecurityOptions.IgnoreLocalIps)}",

                ["RATELIMIT_PERMIT_LIMIT"] = "RateLimit:PermitLimit",
                ["RATELIMIT_WINDOW_MINUTES"] = "RateLimit:WindowMinutes",
                ["RATELIMIT_VALIDATION_PERMIT_LIMIT"] = "RateLimit:Validation:PermitLimit",
                ["RATELIMIT_VALIDATION_WINDOW_MINUTES"] = "RateLimit:Validation:WindowMinutes",
                ["RATELIMIT_HEALTH_PERMIT_LIMIT"] = "RateLimit:Health:PermitLimit",
                ["RATELIMIT_HEALTH_WINDOW_MINUTES"] = "RateLimit:Health:WindowMinutes",
                ["RATELIMIT_BURST_PERMIT_LIMIT"] = "RateLimit:Burst:PermitLimit",
                ["RATELIMIT_BURST_WINDOW_MINUTES"] = "RateLimit:Burst:WindowMinutes",
                ["FALLBACK_API"] = "FallbackApi"
            };

            // Apply simple mappings
            foreach (var mapping in environmentMappings)
            {
                var envValue = Environment.GetEnvironmentVariable(mapping.Key);
                if (!string.IsNullOrEmpty(envValue))
                {
                    configuration[mapping.Value] = envValue;
                }
            }

            // Handle array environment variables
            HandleArrayEnvironmentVariable("BLOCKED_COUNTRIES",
                $"{SecurityOptions.SectionName}:{nameof(SecurityOptions.BlockedCountries)}", configuration);
            HandleArrayEnvironmentVariable("ALLOWED_COUNTRIES",
                $"{SecurityOptions.SectionName}:{nameof(SecurityOptions.AllowedCountries)}", configuration);
            HandleArrayEnvironmentVariable("LOCAL_IPS",
                $"{SecurityOptions.SectionName}:{nameof(SecurityOptions.LocalIps)}", configuration);
        }
        private static void HandleArrayEnvironmentVariable(string envKey, string configPath, IConfiguration configuration)
        {
            var envValue = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrEmpty(envValue))
            {
                // Support comma-separated values: "CZ,SK,DE"
                var values = envValue.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(v => v.Trim())
                                    .ToArray();

                for (int i = 0; i < values.Length; i++)
                {
                    configuration[$"{configPath}:{i}"] = values[i];
                }
            }
        }
    }
}
