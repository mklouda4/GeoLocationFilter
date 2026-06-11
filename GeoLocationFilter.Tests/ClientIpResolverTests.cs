using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Net;

namespace GeoLocationFilter.Tests
{
    public class ClientIpResolverTests
    {
        private static ClientIpResolver CreateResolver(params string[] trustedProxies)
        {
            var options = new SecurityOptions
            {
                TrustedProxies = trustedProxies.Length > 0
                    ? [.. trustedProxies]
                    : [.. SecurityOptions.PrivateNetworkDefaults]
            };
            return new ClientIpResolver(new StaticOptionsMonitor(options));
        }

        private static HttpContext CreateContext(string? remoteIp, params (string Key, string Value)[] headers)
        {
            var context = new DefaultHttpContext();
            if (remoteIp != null)
                context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            foreach (var (key, value) in headers)
                context.Request.Headers[key] = value;
            return context;
        }

        [Fact]
        public void UntrustedRemote_IgnoresForwardingHeaders()
        {
            // Direct client from a public IP claims to be someone else — must not be honored
            var context = CreateContext("203.0.113.50",
                ("X-Real-IP", "8.8.8.8"),
                ("X-Forwarded-For", "8.8.8.8"),
                ("CF-Connecting-IP", "8.8.8.8"));

            Assert.Equal("203.0.113.50", CreateResolver().GetClientIp(context));
        }

        [Fact]
        public void TrustedProxy_HonorsXRealIp()
        {
            var context = CreateContext("10.0.0.2", ("X-Real-IP", "203.0.113.7"));

            Assert.Equal("203.0.113.7", CreateResolver().GetClientIp(context));
        }

        [Fact]
        public void TrustedProxy_PrefersCfConnectingIp()
        {
            var context = CreateContext("10.0.0.2",
                ("CF-Connecting-IP", "198.51.100.1"),
                ("X-Real-IP", "203.0.113.7"));

            Assert.Equal("198.51.100.1", CreateResolver().GetClientIp(context));
        }

        [Fact]
        public void TrustedProxy_ForwardedFor_ReturnsRightmostUntrustedHop()
        {
            // client (spoofed entry), real client, trusted proxy
            var context = CreateContext("10.0.0.2",
                ("X-Forwarded-For", "1.1.1.1, 203.0.113.7, 10.0.0.3"));

            Assert.Equal("203.0.113.7", CreateResolver().GetClientIp(context));
        }

        [Fact]
        public void TrustedProxy_ForwardedFor_AllTrusted_ReturnsLeftmost()
        {
            var context = CreateContext("10.0.0.2",
                ("X-Forwarded-For", "192.168.1.10, 10.0.0.3"));

            Assert.Equal("192.168.1.10", CreateResolver().GetClientIp(context));
        }

        [Fact]
        public void TrustedProxy_GarbageHeaders_FallsBackToRemote()
        {
            var context = CreateContext("10.0.0.2",
                ("X-Real-IP", "not-an-ip"),
                ("X-Forwarded-For", "garbage, also-garbage"));

            Assert.Equal("10.0.0.2", CreateResolver().GetClientIp(context));
        }

        [Fact]
        public void NoRemoteAddress_ReturnsNull()
        {
            var context = CreateContext(null, ("X-Real-IP", "8.8.8.8"));

            Assert.Null(CreateResolver().GetClientIp(context));
        }

        [Fact]
        public void Ipv4MappedRemote_IsNormalizedAndMatchedAgainstTrustedProxies()
        {
            var context = CreateContext("::ffff:10.0.0.2", ("X-Real-IP", "203.0.113.7"));

            Assert.Equal("203.0.113.7", CreateResolver().GetClientIp(context));
        }

        [Fact]
        public void RestrictedTrustedProxies_RejectPrivateRemote()
        {
            // Only one specific proxy is trusted — another private address is not
            var resolver = CreateResolver("10.0.0.99/32");
            var context = CreateContext("10.0.0.2", ("X-Real-IP", "8.8.8.8"));

            Assert.Equal("10.0.0.2", resolver.GetClientIp(context));
        }

        [Fact]
        public void ForwardedForWithPort_IsParsed()
        {
            var context = CreateContext("10.0.0.2", ("X-Forwarded-For", "203.0.113.7:51234"));

            Assert.Equal("203.0.113.7", CreateResolver().GetClientIp(context));
        }

        [Fact]
        public void ResolvedIp_IsCachedPerRequest()
        {
            var resolver = CreateResolver();
            var context = CreateContext("10.0.0.2", ("X-Real-IP", "203.0.113.7"));

            var first = resolver.GetClientIp(context);
            // header changes mid-request must not change the already resolved value
            context.Request.Headers["X-Real-IP"] = "198.51.100.1";
            var second = resolver.GetClientIp(context);

            Assert.Equal(first, second);
        }

        private class StaticOptionsMonitor(SecurityOptions value) : IOptionsMonitor<SecurityOptions>
        {
            public SecurityOptions CurrentValue => value;
            public SecurityOptions Get(string? name) => value;
            public IDisposable? OnChange(Action<SecurityOptions, string?> listener) => null;
        }
    }
}
